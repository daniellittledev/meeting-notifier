namespace MeetingNotifier

open System
open System.Globalization
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Text.Json.Serialization
open MeetingNotifier.Domain

/// Calendar-source abstraction + Microsoft Graph implementation.
/// Wrapping Graph behind `ICalendarSource` leaves the door open for an
/// ICS/Exchange implementation without touching the rest of the app.
module GraphClient =

    type ICalendarSource =
        abstract GetMeetingsInWindow :
            startT: DateTimeOffset * endT: DateTimeOffset ->
                Async<Result<Meeting list, string>>

    // ----- Internal DTOs matching the Graph /me/calendarView shape.

    [<CLIMutable>]
    type private GraphDateTimeTz =
        { dateTime: string
          timeZone: string }

    [<CLIMutable>]
    type private GraphLocation =
        { displayName: string }

    [<CLIMutable>]
    type private GraphOnlineMeeting =
        { joinUrl: string }

    [<CLIMutable>]
    type private GraphEvent =
        { id: string
          subject: string
          start: GraphDateTimeTz
          ``end``: GraphDateTimeTz
          location: GraphLocation
          onlineMeeting: GraphOnlineMeeting
          isCancelled: bool }

    [<CLIMutable>]
    type private GraphEventsResponse =
        { value: GraphEvent[] }

    let private jsonOpts =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        o.Converters.Add(JsonFSharpConverter())
        o

    /// Parse Graph's ISO-ish date string + IANA zone back into a UTC
    /// `DateTimeOffset`. We request timezone=UTC via the Prefer header so in
    /// practice the value is already Z — the zone handling here is a
    /// defence against Graph returning a different zone anyway.
    let private parseGraphDateTime (dt: GraphDateTimeTz) : DateTimeOffset =
        if isNull (box dt) || isNull dt.dateTime then DateTimeOffset.MinValue
        else
            let parsed =
                DateTime.Parse(
                    dt.dateTime,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))

    let private opt (s: string) =
        if String.IsNullOrWhiteSpace s then None else Some s

    let private toDomain (e: GraphEvent) : Meeting =
        { Id = MeetingId e.id
          Subject = (if isNull e.subject then "(no subject)" else e.subject)
          Start = parseGraphDateTime e.start
          End = parseGraphDateTime e.``end``
          Location =
              if isNull (box e.location) then None
              else opt e.location.displayName
          JoinUrl =
              if isNull (box e.onlineMeeting) then None
              else opt e.onlineMeeting.joinUrl
          IsCancelled = e.isCancelled }

    /// Build the calendarView URL. Graph expects round-trip ISO datetimes
    /// without offsets when combined with Prefer: outlook.timezone="UTC".
    let private buildUrl (startT: DateTimeOffset) (endT: DateTimeOffset) =
        let fmt (d: DateTimeOffset) =
            d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        $"https://graph.microsoft.com/v1.0/me/calendarView"
        + $"?startDateTime={fmt startT}&endDateTime={fmt endT}"
        + "&$select=id,subject,start,end,location,onlineMeeting,isCancelled"
        + "&$orderby=start/dateTime"
        + "&$top=50"

    type GraphCalendarSource(http: HttpClient, token: Auth.TokenProvider) =
        interface ICalendarSource with
            member _.GetMeetingsInWindow(startT, endT) =
                async {
                    try
                        let! accessToken = token.GetAccessToken()
                        use req =
                            new HttpRequestMessage(HttpMethod.Get, buildUrl startT endT)
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
                            let parsed =
                                JsonSerializer.Deserialize<GraphEventsResponse>(
                                    body, jsonOpts)
                            let events =
                                if isNull (box parsed) || isNull parsed.value
                                then [||]
                                else parsed.value
                            return
                                events
                                |> Array.map toDomain
                                |> Array.toList
                                |> Ok
                    with ex ->
                        return Error ex.Message
                }
