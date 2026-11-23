namespace OutlookNotifier

open System.Diagnostics
open Microsoft.Win32

/// Opens links in the user's default web browser, bypassing the Windows
/// "apps for websites" routing that hands Outlook links to new Outlook.
module Browser =

    let private httpsChoiceKey =
        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"

    let private readValue (root: RegistryKey) (path: string) (name: string) =
        match root.OpenSubKey path with
        | null -> None
        | key ->
            use key = key
            match key.GetValue name with
            | :? string as s when s <> "" -> Some s
            | _ -> None

    /// The default browser's shell `open` command, e.g.
    /// `"C:\...\chrome.exe" --single-argument %1`.
    let private defaultBrowserCommand () =
        readValue Registry.CurrentUser httpsChoiceKey "ProgId"
        |> Option.bind (fun progId ->
            readValue Registry.ClassesRoot (progId + @"\shell\open\command") "")

    let private start (psi: ProcessStartInfo) =
        try Process.Start psi |> ignore
        with _ -> ()

    let private shellOpen (url: string) = start (ProcessStartInfo(url, UseShellExecute = true))

    /// Open `url` directly in the default browser, falling back to the normal
    /// shell open when the browser can't be resolved or fails to start.
    let openInDefault (url: string) : unit =
        let launch =
            try defaultBrowserCommand () |> Option.bind (fun cmd -> EmailLink.launchFor cmd url)
            with _ -> None
        match launch with
        | Some(exe, args) ->
            try Process.Start(ProcessStartInfo(exe, args, UseShellExecute = false)) |> ignore
            with _ -> shellOpen url
        | None -> shellOpen url

    /// Open an email's Graph webLink so it shows the message in Outlook on
    /// the web rather than bouncing into new Outlook (see EmailLink).
    let openEmail (webLink: string) : unit =
        openInDefault (EmailLink.forBrowser webLink)
