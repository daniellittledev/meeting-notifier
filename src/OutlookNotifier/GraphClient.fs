namespace OutlookNotifier

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open OutlookNotifier.Domain
open OutlookNotifier.GraphParsing

/// Calendar/mail source abstractions + Microsoft Graph implementations.
/// Wrapping Graph behind `ICalendarSource` / `IMailSource` leaves the door
/// open for other implementations without touching the rest of the app. The
/// payload parsing lives in `GraphParsing` so it can be tested in isolation.
module GraphClient =

    type ICalendarSource =
        abstract GetMeetingsInWindow :
            startT: DateTimeOffset * endT: DateTimeOffset ->
                Async<Result<Meeting list, string>>

    /// Safety cap on pages followed via @odata.nextLink, so a misbehaving
    /// server can't spin us forever. A 24h calendar window at $top=50 is one
    /// page in practice; a mail catch-up can hit it, which is reported as
    /// truncation rather than silently dropping the rest.
    let private maxPages = 20

    /// An HTTP failure: status code (None when the request never got a
    /// response) and a message.
    type private HttpError = HttpStatusCode option * string

    /// GET `url` with a bearer token and any extra headers.
    let private getBody
        (http: HttpClient)
        (accessToken: string)
        (headers: (string * string) list)
        (url: string)
        : Async<Result<string, HttpError>>
        =
        async {
            use req = new HttpRequestMessage(HttpMethod.Get, url)
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
            for name, value in headers do
                req.Headers.TryAddWithoutValidation(name, value) |> ignore
            let! resp = http.SendAsync(req) |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            return
                if resp.IsSuccessStatusCode then Ok body
                else Error(Some resp.StatusCode, $"Graph {int resp.StatusCode}: {body}")
        }

    /// Follow @odata.nextLink from `firstUrl` up to `maxPages`, accumulating
    /// items. The flag is true when the cap cut paging short.
    let private fetchAllPages
        (fetchPage: string -> Async<Result<'a list * string option, HttpError>>)
        (firstUrl: string)
        : Async<Result<'a list * bool, HttpError>>
        =
        let rec loop url pagesLeft acc =
            async {
                let! page = fetchPage url
                match page with
                | Error e -> return Error e
                | Ok(items, next) ->
                    let acc' = acc @ items
                    match next with
                    | Some nextUrl when pagesLeft > 1 -> return! loop nextUrl (pagesLeft - 1) acc'
                    | Some _ -> return Ok(acc', true)
                    | None -> return Ok(acc', false)
            }
        loop firstUrl maxPages []

    let private asyncMap (f: 'a -> 'b) (a: Async<'a>) : Async<'b> =
        async {
            let! x = a
            return f x
        }

    let private parsed (parse: string -> 'b) (r: Result<string, HttpError>) =
        r |> Result.map parse

    type GraphCalendarSource(http: HttpClient, token: Auth.TokenProvider) =
        interface ICalendarSource with
            member _.GetMeetingsInWindow(startT, endT) =
                async {
                    try
                        let! accessToken = token.GetAccessToken()
                        // Ask for UTC so the timezone handling stays trivial.
                        let get url =
                            getBody http accessToken [ "Prefer", "outlook.timezone=\"UTC\"" ] url
                            |> asyncMap (parsed parsePage)
                        let! result = fetchAllPages get (buildUrl startT endT)
                        return result |> Result.map fst |> Result.mapError snd
                    with ex ->
                        return Error ex.Message
                }

    // ----- Mail

    type IMailSource =
        /// Mail received at or after `since` (oldest first), excluding the
        /// built-in and configured folders and their sub-folders.
        abstract GetEmailsSince : since: DateTimeOffset -> Async<Result<MailBatch, string>>
        /// Allow one more interactive sign-in for Mail.Read (after the user
        /// re-authenticates from the tray menu).
        abstract ResetSignIn : unit -> unit

    /// How long a resolved folder tree is trusted before it's re-read, so
    /// folders created mid-session are picked up.
    let private folderRefresh = TimeSpan.FromHours 1.0

    /// Upper bound on folders walked, purely defensive.
    let private maxFolders = 2000

    type GraphMailSource(http: HttpClient, token: Auth.TokenProvider, includedNames: string list) =
        // The resolved ids of the explicitly-included folders and when they
        // were resolved, cached so the folder tree isn't re-walked every poll.
        // Only set on a fully-successful resolve.
        let mutable includedCache : (Set<string> * DateTimeOffset) option = None
        // Mail.Read is normally consented in the calendar's sign-in prompt.
        // When it wasn't (signed in before any rules existed), prompt for it
        // at most once per session, so a refusal can't pop the browser every
        // poll.
        let mutable interactiveTried = false

        // Immutable ids survive a message being moved between folders, so
        // moving a reminder email won't re-alert it.
        let mailHeaders = [ "Prefer", "IdType=\"ImmutableId\"" ]

        let acquireToken () =
            async {
                try
                    return! token.GetTokenFor(Auth.mailScopes, false)
                with Auth.SignInRequired _ ->
                    let! hasAccount = token.HasCachedAccount()
                    if not hasAccount then
                        // The calendar's first sign-in (which also asks for
                        // Mail.Read) hasn't finished yet; don't open a second
                        // prompt alongside it.
                        return failwith "Waiting for sign-in to finish."
                    elif token.ExtraConsentDeclined || interactiveTried then
                        return
                            failwith (
                                "Mail.Read isn't consented yet. Use Re-authenticate... in the tray "
                                + "menu, or ask an admin to grant it.")
                    else
                        // Signed in before rules existed: ask for Mail.Read once.
                        interactiveTried <- true
                        return! token.GetTokenFor(Auth.mailScopes, true)
            }

        /// Every folder in the mailbox (breadth-first over childFolders).
        let fetchFolderTree accessToken : Async<Result<MailFolder list, HttpError>> =
            let fetchLevel url =
                fetchAllPages
                    (fun u -> getBody http accessToken mailHeaders u |> asyncMap (parsed parseFoldersPage))
                    url
            let rec walk (acc: MailFolder list) (pending: string list) =
                async {
                    match pending with
                    | [] -> return Ok acc
                    | _ when List.length acc >= maxFolders -> return Ok acc
                    | parentId :: rest ->
                        let! level = fetchLevel (buildChildFoldersUrl parentId)
                        match level with
                        | Error e -> return Error e
                        | Ok(items, _) ->
                            let withKids = items |> List.filter (fun (_, n) -> n > 0) |> List.map (fun (f, _) -> f.FolderId)
                            return! walk (acc @ List.map fst items) (rest @ withKids)
                }
            async {
                let! top = fetchLevel (buildFoldersUrl ())
                match top with
                | Error e -> return Error e
                | Ok(items, _) ->
                    let withKids = items |> List.filter (fun (_, n) -> n > 0) |> List.map (fun (f, _) -> f.FolderId)
                    return! walk (List.map fst items) withKids
            }

        /// Ids of the explicitly-included folders, beyond the always-scanned
        /// Inbox. Empty when nothing is configured, which skips the folder
        /// walk entirely so the common case makes no extra Graph calls.
        let resolveIncluded accessToken : Async<Result<Set<string>, HttpError>> =
            async {
                if List.isEmpty includedNames then return Ok Set.empty
                else
                    match includedCache with
                    | Some(ids, at) when DateTimeOffset.Now - at < folderRefresh -> return Ok ids
                    | _ ->
                        let! tree = fetchFolderTree accessToken
                        match tree with
                        | Ok folders ->
                            let ids = EmailRules.includedFolderIds folders includedNames
                            includedCache <- Some(ids, DateTimeOffset.Now)
                            return Ok ids
                        | Error e -> return Error e
            }

        interface IMailSource with
            member _.ResetSignIn() = interactiveTried <- false

            member _.GetEmailsSince(since) =
                async {
                    try
                        let! accessToken = acquireToken ()
                        // A failed folder resolve means we don't know which
                        // included folders to scan, so fail the poll (the cursor
                        // stays put and the next poll retries) rather than guess.
                        let! included = resolveIncluded accessToken
                        match included with
                        | Error(_, msg) -> return Error $"resolving mail folders failed: {msg}"
                        | Ok includedIds ->
                            let fetchFolder folderId =
                                fetchAllPages
                                    (fun url -> getBody http accessToken mailHeaders url |> asyncMap (parsed parseMessagesPage))
                                    (buildMessagesUrl folderId since)
                            let newestOf (emails: Email list) =
                                emails
                                |> List.fold (fun acc e -> Some(match acc with Some a -> max a e.Received | None -> e.Received)) None
                            // Query each folder on its own, sequentially (one
                            // request in flight keeps us under the mailbox
                            // concurrency cap). The cursor may only advance to
                            // the OLDEST truncation point across folders, so a
                            // folder that paged out can't have its unfetched
                            // mail stepped over.
                            let rec loop remaining emails truncated (watermark: DateTimeOffset option) =
                                async {
                                    match remaining with
                                    | [] ->
                                        return Ok { Emails = emails; Truncated = truncated; NewestReceived = watermark }
                                    | folderId :: rest ->
                                        let! page = fetchFolder folderId
                                        match page with
                                        | Error(_, msg) -> return Error msg
                                        | Ok(pairs, pageTruncated) ->
                                            let these = pairs |> List.map snd
                                            let watermark' =
                                                if pageTruncated then
                                                    match newestOf these with
                                                    | Some t -> Some(match watermark with Some w -> min w t | None -> t)
                                                    | None -> watermark
                                                else watermark
                                            return! loop rest (emails @ these) (truncated || pageTruncated) watermark'
                                }
                            return! loop ("inbox" :: Set.toList includedIds) [] false None
                    with ex ->
                        return Error ex.Message
                }
