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
