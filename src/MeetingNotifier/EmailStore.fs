namespace MeetingNotifier

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open MeetingNotifier.Domain

/// Email reminders persisted across restarts at
/// `%APPDATA%\MeetingNotifier\email-reminders.json`: the reminders still
/// waiting to be checked off, the ones already ticked off (so a re-fetch
/// never re-alerts them) and the polling cursor (so mail that arrives
/// while the app is closed is caught up on next launch).
///
/// Everything except `load`/`save` is pure.
module EmailStore =

    // DTOs first: F# infers a record literal from the last-declared type
    // with matching fields, so the domain record below must come after.
    [<CLIMutable>]
    type PendingDto =
        { Id: string
          Subject: string
          FromName: string
          FromAddress: string
          Received: DateTimeOffset
          WebLink: string
          RuleName: string }

    [<CLIMutable>]
    type CompletedDto = { Id: string; Received: DateTimeOffset }

    [<CLIMutable>]
    type StoreDto =
        { Since: Nullable<DateTimeOffset>
          Pending: PendingDto[]
          Completed: CompletedDto[] }

    type EmailStore =
        { /// The polling cursor as of the last successful poll.
          Since: DateTimeOffset option
          /// Reminders shown and not yet checked off, oldest first.
          Pending: EmailAlert list
          /// Checked-off emails, keyed by id, with their received time so
          /// they can be pruned once they're too old to be fetched again.
          Completed: Map<EmailId, DateTimeOffset> }

    let empty =
        { Since = None
          Pending = []
          Completed = Map.empty }

    /// Checked-off ids are kept this long; older mail is never re-fetched
    /// (catch-up is capped well below this), so there's no need to remember it.
    let completedRetention = TimeSpan.FromDays 30.0

    /// The furthest back a restart catches up, so a week away doesn't
    /// unload a week of matching mail at once.
    let maxCatchUp = TimeSpan.FromDays 3.0

    /// Has this email already been surfaced (pending or checked off)?
    let knows (id: EmailId) (store: EmailStore) : bool =
        Map.containsKey id store.Completed
        || store.Pending |> List.exists (fun a -> a.Email.Id = id)

    let addPending (alerts: EmailAlert list) (store: EmailStore) : EmailStore =
        let fresh = alerts |> List.filter (fun a -> not (knows a.Email.Id store))
        { store with Pending = store.Pending @ fresh }

    /// Check an email off: drop it from Pending and remember it as done.
    let markDone (id: EmailId) (store: EmailStore) : EmailStore =
        let received =
            store.Pending
            |> List.tryFind (fun a -> a.Email.Id = id)
            |> Option.map (fun a -> a.Email.Received)
        match received with
        | None -> store
        | Some r ->
            { store with
                Pending = store.Pending |> List.filter (fun a -> a.Email.Id <> id)
                Completed = Map.add id r store.Completed }

    let withSince (since: DateTimeOffset) (store: EmailStore) : EmailStore =
        { store with Since = Some since }

    /// Forget checked-off emails older than the retention window.
    let prune (now: DateTimeOffset) (store: EmailStore) : EmailStore =
        let cutoff = now - completedRetention
        { store with Completed = store.Completed |> Map.filter (fun _ r -> r >= cutoff) }

    /// Where the first poll after launch should start: from the saved cursor
    /// (capped at `maxCatchUp`), or `lookbackMinutes` back on a first run.
    let startSince (now: DateTimeOffset) (lookbackMinutes: int) (store: EmailStore) =
        match store.Since with
        | Some s -> max s (now - maxCatchUp)
        | None -> now - TimeSpan.FromMinutes(float lookbackMinutes)

    // ----- JSON (pure string <-> store)

    let private jsonOptions =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true)

    let private orEmpty (s: string) = if isNull s then "" else s

    let toJson (store: EmailStore) : string =
        let dto : StoreDto =
            { Since = (match store.Since with Some s -> Nullable s | None -> Nullable())
              Pending =
                store.Pending
                |> List.map (fun a ->
                    let (EmailId id) = a.Email.Id
                    { PendingDto.Id = id
                      Subject = a.Email.Subject
                      FromName = a.Email.FromName
                      FromAddress = a.Email.FromAddress
                      Received = a.Email.Received
                      WebLink = a.Email.WebLink |> Option.toObj
                      RuleName = a.RuleName })
                |> List.toArray
              Completed =
                store.Completed
                |> Map.toArray
                |> Array.map (fun (EmailId id, r) -> { CompletedDto.Id = id; Received = r }) }
        JsonSerializer.Serialize(dto, jsonOptions)

    /// Parse a saved store; anything unreadable yields the empty store
    /// rather than stopping the app.
    let ofJson (json: string) : EmailStore =
        try
            let dto = JsonSerializer.Deserialize<StoreDto>(json, jsonOptions)
            if isNull (box dto) then empty
            else
                let pending =
                    (if isNull dto.Pending then [||] else dto.Pending)
                    |> Array.filter (fun p -> not (isNull (box p)) && not (String.IsNullOrEmpty p.Id))
                    |> Array.map (fun p ->
                        { Email =
                            { Email.Id = EmailId p.Id
                              Subject = orEmpty p.Subject
                              FromName = orEmpty p.FromName
                              FromAddress = orEmpty p.FromAddress
                              Received = p.Received
                              WebLink =
                                if String.IsNullOrWhiteSpace p.WebLink then None
                                else Some p.WebLink }
                          RuleName = orEmpty p.RuleName })
                    |> Array.toList
                let completed =
                    (if isNull dto.Completed then [||] else dto.Completed)
                    |> Array.filter (fun c -> not (isNull (box c)) && not (String.IsNullOrEmpty c.Id))
                    |> Array.map (fun c -> EmailId c.Id, c.Received)
                    |> Map.ofArray
                { Since = Option.ofNullable dto.Since
                  Pending = pending
                  Completed = completed }
        with _ -> empty

    // ----- Disk

    let path = Path.Combine(Config.appDataDir, "email-reminders.json")

    let load () : EmailStore =
        try
            if File.Exists path then ofJson (File.ReadAllText path) else empty
        with _ -> empty

    /// Write via a temp file + move so a crash mid-write can't leave a
    /// truncated file behind.
    let save (store: EmailStore) : unit =
        Directory.CreateDirectory(Config.appDataDir) |> ignore
        let tmp = path + ".tmp"
        File.WriteAllText(tmp, toJson store)
        File.Move(tmp, path, true)
