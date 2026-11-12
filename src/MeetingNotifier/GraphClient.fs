namespace MeetingNotifier

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open MeetingNotifier.Domain
open MeetingNotifier.GraphParsing

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

    type GraphMailSource(http: HttpClient, token: Auth.TokenProvider, excludedNames: string list) =
        // The resolved excluded-folder ids and when they were resolved. Only
        // set when EVERY lookup succeeded, so a transient failure can never
        // leave e.g. Sent Items out of the set.
        let mutable excludedCache : (Set<string> * DateTimeOffset) option = None
        // Mail.Read may need its own consent. Prompt for it at most once per
        // session (and only once the calendar sign-in has created an
        // account), so a refused consent can't pop the browser every poll.
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
                    if hasAccount && not interactiveTried then
                        interactiveTried <- true
                        return! token.GetTokenFor(Auth.mailScopes, true)
                    else
                        return
                            failwith (
                                "Mail.Read isn't consented yet. Use Re-authenticate... in the tray "
                                + "menu, or ask an admin to grant it.")
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

        /// Ids of the built-in well-known folders. A 404 means the mailbox
        /// simply doesn't have that folder; any other failure fails the lot.
        let fetchWellKnownIds accessToken : Async<Result<Set<string>, HttpError>> =
            async {
                let! results =
                    excludedWellKnownFolders
                    |> List.map (fun name -> getBody http accessToken mailHeaders (buildFolderUrl name))
                    |> Async.Parallel
                let folded =
                    results
                    |> Array.fold
                        (fun acc r ->
                            match acc, r with
                            | Error e, _ -> Error e
                            | Ok ids, Ok body ->
                                Ok(match parseFolderId body with Some id -> Set.add id ids | None -> ids)
                            | Ok ids, Error(Some HttpStatusCode.NotFound, _) -> Ok ids
                            | Ok _, Error e -> Error e)
                        (Ok Set.empty)
                return folded
            }

        let resolveExcluded accessToken : Async<Result<Set<string>, HttpError>> =
            async {
                match excludedCache with
                | Some(ids, at) when DateTimeOffset.Now - at < folderRefresh -> return Ok ids
                | _ ->
                    let! roots = fetchWellKnownIds accessToken
                    let! tree = fetchFolderTree accessToken
                    match roots, tree with
                    | Ok r, Ok folders ->
                        let ids = EmailRules.excludedFolderIds folders r excludedNames
                        excludedCache <- Some(ids, DateTimeOffset.Now)
                        return Ok ids
                    | Error e, _
                    | _, Error e -> return Error e
            }

        interface IMailSource with
            member _.ResetSignIn() = interactiveTried <- false

            member _.GetEmailsSince(since) =
                async {
                    try
                        let! accessToken = acquireToken ()
                        // Without a complete exclusion set we'd risk alerting on
                        // Sent/Junk mail, so fail the poll (the cursor stays put
                        // and the next poll retries) rather than guess.
                        let! excluded = resolveExcluded accessToken
                        match excluded with
                        | Error(_, msg) -> return Error $"resolving mail folders failed: {msg}"
                        | Ok excludedIds ->
                            let get url =
                                getBody http accessToken mailHeaders url
                                |> asyncMap (parsed parseMessagesPage)
                            let! result = fetchAllPages get (buildMessagesUrl since)
                            return
                                result
                                |> Result.mapError snd
                                |> Result.map (fun (pairs, truncated) ->
                                    { Emails =
                                        pairs
                                        |> List.filter (fun (folder, _) -> not (Set.contains folder excludedIds))
                                        |> List.map snd
                                      Truncated = truncated
                                      NewestReceived =
                                        pairs
                                        |> List.map (fun (_, e) -> e.Received)
                                        |> List.fold (fun acc r -> Some(match acc with Some a -> max a r | None -> r)) None })
                    with ex ->
                        return Error ex.Message
                }
