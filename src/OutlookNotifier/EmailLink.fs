namespace OutlookNotifier

open System

/// Pure helpers for opening an email's Graph webLink in the browser.
///
/// New Outlook registers itself as the Windows handler for Outlook web
/// links, and handed a deep link that way it fails to open the message
/// ("WebView2bridgeNotInitialized"). So the link goes straight to the default
/// browser, and loses the `exvsurl` flag that would otherwise make Outlook on
/// the web bounce the message back to the desktop app.
module EmailLink =

    /// The query flag asking Outlook on the web to hand off to the desktop app.
    let private desktopHandoffParam = "exvsurl"

    /// The placeholder a Windows shell `open` command uses for its argument.
    let private argumentPlaceholder = "%1"

    let private paramName (pair: string) = pair.Split('=').[0]

    let private isDesktopHandoff (pair: string) =
        String.Equals(paramName pair, desktopHandoffParam, StringComparison.OrdinalIgnoreCase)

    /// Drop the query parameters matching `exclude`, leaving every other
    /// parameter exactly as encoded.
    let private withoutParams (exclude: string -> bool) (url: string) =
        let fragmentAt = url.IndexOf '#'
        let beforeFragment, fragment =
            if fragmentAt < 0 then url, "" else url.Substring(0, fragmentAt), url.Substring fragmentAt
        match beforeFragment.IndexOf '?' with
        | -1 -> url
        | queryAt ->
            let path = beforeFragment.Substring(0, queryAt)
            let kept =
                beforeFragment.Substring(queryAt + 1).Split('&')
                |> Array.filter (fun pair -> pair <> "" && not (exclude pair))
            let query = if Array.isEmpty kept then "" else "?" + String.Join("&", kept)
            path + query + fragment

    let private isAbsoluteUrl (s: string) = Uri.TryCreate(s, UriKind.Absolute, ref null)

    /// The webLink as it should be opened in a browser: unchanged, minus the
    /// desktop hand-off flag.
    let forBrowser (webLink: string) : string =
        if isAbsoluteUrl webLink then withoutParams isDesktopHandoff webLink else webLink

    /// Split a shell command into its executable and the rest of the line.
    let private splitCommand (command: string) =
        if command.StartsWith "\"" then
            match command.IndexOf('"', 1) with
            | -1 -> command.Trim('"'), ""
            | closeAt -> command.Substring(1, closeAt - 1), command.Substring(closeAt + 1).Trim()
        else
            match command.IndexOf ' ' with
            | -1 -> command, ""
            | spaceAt -> command.Substring(0, spaceAt), command.Substring(spaceAt + 1).Trim()

    let private withUrl (url: string) (args: string) =
        if args.Contains argumentPlaceholder then args.Replace(argumentPlaceholder, url)
        else (args + " \"" + url + "\"").Trim()

    /// The executable and arguments that open `url` with a registered
    /// browser's `open` command (e.g. `"chrome.exe" --single-argument %1`).
    let launchFor (command: string) (url: string) : (string * string) option =
        match command.Trim() with
        | "" -> None
        | trimmed ->
            let exe, args = splitCommand trimmed
            if String.IsNullOrWhiteSpace exe then None else Some(exe, withUrl url args)
