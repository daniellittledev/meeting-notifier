namespace MeetingNotifier

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Custom
open MeetingNotifier.Domain

/// Pure parsing of the Microsoft Graph /me/calendarView payload into domain
/// `Meeting` values. Deliberately free of any HTTP/auth dependency so the
/// DTO shape, the timezone handling, and the null-guards can be unit-tested
/// without faking the network.
module GraphParsing =

    // ----- DTOs matching the Graph /me/calendarView shape.

    [<CLIMutable>]
    type GraphDateTimeTz =
        { dateTime: string
          timeZone: string }

    [<CLIMutable>]
    type GraphLocation =
        { displayName: string }

    [<CLIMutable>]
    type GraphOnlineMeeting =
        { joinUrl: string }

    [<CLIMutable>]
    type GraphEvent =
        { id: string
          subject: string
          start: GraphDateTimeTz
          ``end``: GraphDateTimeTz
          location: GraphLocation
          onlineMeeting: GraphOnlineMeeting
          isCancelled: bool }

    [<CLIMutable>]
    type GraphEventsResponse =
        { value: GraphEvent[]
          // Option so an absent nextLink (the common single-page case)
          // deserialises to None rather than risking a missing-field error.
          [<JsonPropertyName("@odata.nextLink")>] nextLink: string option }

    let jsonOpts =
        let o = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        // The CLIMutable DTOs deserialise via System.Text.Json's default
        // object handler; OptionJsonConverter maps a present value to Some and
        // null to None. An absent property (e.g. the common single-page case
        // with no @odata.nextLink) simply stays at its default, which for an
        // option is null = None.
        o.Converters.Add(OptionJsonConverter())
        o

    /// Parse Graph's ISO-ish date string + IANA zone back into a UTC
    /// `DateTimeOffset`. We request timezone=UTC via the Prefer header so in
    /// practice the value is already Z — the zone handling here is a
    /// defence against Graph returning a different zone anyway.
    let parseGraphDateTime (dt: GraphDateTimeTz) : DateTimeOffset =
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

    let toDomain (e: GraphEvent) : Meeting =
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

    /// Deserialise one calendarView page into domain meetings plus the
    /// `@odata.nextLink` (if any) so the caller can follow pagination.
    let parsePage (body: string) : Meeting list * string option =
        let parsed = JsonSerializer.Deserialize<GraphEventsResponse>(body, jsonOpts)
        if isNull (box parsed) then [], None
        else
            let meetings =
                if isNull parsed.value then []
                else parsed.value |> Array.map toDomain |> Array.toList
            let next =
                match parsed.nextLink with
                | Some s when not (String.IsNullOrWhiteSpace s) -> Some s
                | _ -> None
            meetings, next

    /// Build the first calendarView URL. Graph expects round-trip ISO
    /// datetimes without offsets when combined with
    /// Prefer: outlook.timezone="UTC".
    let buildUrl (startT: DateTimeOffset) (endT: DateTimeOffset) =
        let fmt (d: DateTimeOffset) =
            d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        $"https://graph.microsoft.com/v1.0/me/calendarView"
        + $"?startDateTime={fmt startT}&endDateTime={fmt endT}"
        + "&$select=id,subject,start,end,location,onlineMeeting,isCancelled"
        + "&$orderby=start/dateTime"
        + "&$top=50"
