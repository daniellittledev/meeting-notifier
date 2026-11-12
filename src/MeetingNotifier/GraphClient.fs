namespace MeetingNotifier

open System
open System.Net.Http
open System.Net.Http.Headers
open MeetingNotifier.Domain
open MeetingNotifier.GraphParsing

/// Calendar-source abstraction + Microsoft Graph implementation.
/// Wrapping Graph behind `ICalendarSource` leaves the door open for an
/// ICS/Exchange implementation without touching the rest of the app. The
/// payload parsing lives in `GraphParsing` so it can be tested in isolation.
module GraphClient =

    type ICalendarSource =
        abstract GetMeetingsInWindow :
            startT: DateTimeOffset * endT: DateTimeOffset ->
                Async<Result<Meeting list, string>>

    /// Safety cap on pages followed via @odata.nextLink, so a misbehaving
    /// server can't spin us forever. A 24h window at $top=50 is one page in
    /// practice; this is purely defensive.
    let private maxPages = 20

    type GraphCalendarSource(http: HttpClient, token: Auth.TokenProvider) =
        interface ICalendarSource with
            member _.GetMeetingsInWindow(startT, endT) =
                async {
                    try
                        let! accessToken = token.GetAccessToken()

                        let fetchPage (url: string) =
                            async {
                                use req =
                                    new HttpRequestMessage(HttpMethod.Get, url)
                                req.Headers.Authorization <-
                                    AuthenticationHeaderValue("Bearer", accessToken)
                                req.Headers.TryAddWithoutValidation(
                                    "Prefer",
                                    "outlook.timezone=\"UTC\"") |> ignore
                                let! resp =
                                    http.SendAsync(req) |> Async.AwaitTask
                                let! body =
                                    resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                                if not resp.IsSuccessStatusCode then
                                    return Error $"Graph {int resp.StatusCode}: {body}"
                                else
                                    return Ok(parsePage body)
                            }

                        // Follow @odata.nextLink until exhausted (or the cap),
                        // accumulating meetings across pages.
                        let rec loop url pagesLeft acc =
                            async {
                                let! page = fetchPage url
                                match page with
                                | Error e -> return Error e
                                | Ok(meetings, next) ->
                                    let acc' = acc @ meetings
                                    match next with
                                    | Some nextUrl when pagesLeft > 1 ->
                                        return! loop nextUrl (pagesLeft - 1) acc'
                                    | _ -> return Ok acc'
                            }

                        return! loop (buildUrl startT endT) maxPages []
                    with ex ->
                        return Error ex.Message
                }

    // ----- Mail

    type IMailSource =
        /// Emails received at or after `since`, excluding Sent/Drafts/
        /// Deleted/Junk/Outbox.
        abstract GetEmailsSince : since: DateTimeOffset -> Async<Result<Email list, string>>

    type GraphMailSource(http: HttpClient, token: Auth.TokenProvider) =
        // Folder ids for the excluded well-known folders, resolved once.
        let mutable excludedFolders : Set<string> option = None

        let get (accessToken: string) (url: string) =
            async {
                use req = new HttpRequestMessage(HttpMethod.Get, url)
                req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
                // Immutable ids survive a message being moved between
                // folders, so moving a reminder email won't re-alert it.
                req.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"") |> ignore
                let! resp = http.SendAsync(req) |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                return
                    if resp.IsSuccessStatusCode then Ok body
                    else Error $"Graph mail {int resp.StatusCode}: {body}"
            }

        let resolveExcluded accessToken =
            async {
                match excludedFolders with
                | Some s -> return s
                | None ->
                    let! ids =
                        excludedWellKnownFolders
                        |> List.map (fun name ->
                            async {
                                let! r = get accessToken (buildFolderUrl name)
                                return
                                    match r with
                                    | Ok body -> (try parseFolderId body with _ -> None)
                                    | Error _ -> None
                            })
                        |> Async.Sequential
                    let s = ids |> Array.choose id |> Set.ofArray
                    // Only cache once we've got at least one id back, so a
                    // transient failure is retried next tick.
                    if not (Set.isEmpty s) then excludedFolders <- Some s
                    return s
            }

        interface IMailSource with
            member _.GetEmailsSince(since) =
                async {
                    try
                        let! accessToken = token.GetAccessToken()
                        let! excluded = resolveExcluded accessToken
                        let rec loop url pagesLeft acc =
                            async {
                                let! page = get accessToken url
                                match page with
                                | Error e -> return Error e
                                | Ok body ->
                                    let emails, next = parseMessagesPage body
                                    let acc' = acc @ emails
                                    match next with
                                    | Some nextUrl when pagesLeft > 1 ->
                                        return! loop nextUrl (pagesLeft - 1) acc'
                                    | _ -> return Ok acc'
                            }
                        let! result = loop (buildMessagesUrl since) maxPages []
                        return
                            result
                            |> Result.map (
                                List.filter (fun (folder, _) -> not (Set.contains folder excluded))
                                >> List.map snd)
                    with ex ->
                        return Error ex.Message
                }
