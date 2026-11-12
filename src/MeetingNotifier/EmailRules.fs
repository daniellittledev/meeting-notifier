namespace MeetingNotifier

open System
open MeetingNotifier.Domain

/// Pure email-rule matching and "which emails are new this tick" logic. No
/// I/O, so the matching rules and the polling cursor can be unit-tested.
module EmailRules =

    let private hasText (s: string) = not (String.IsNullOrWhiteSpace s)

    let private containsCI (needle: string) (haystack: string) =
        hasText haystack
        && haystack.IndexOf(needle.Trim(), StringComparison.OrdinalIgnoreCase) >= 0

    /// A rule needs at least one criterion, otherwise it would match every
    /// email in the mailbox.
    let isActive (rule: Config.EmailRule) =
        hasText rule.From || hasText rule.SubjectContains

    let activeRules (rules: Config.EmailRule seq) : Config.EmailRule list =
        rules |> Seq.filter isActive |> List.ofSeq

    /// Does `email` satisfy `rule`? Each criterion that's set must match
    /// (case-insensitive partial match); unset criteria are ignored.
    let matches (rule: Config.EmailRule) (email: Email) : bool =
        let fromOk =
            not (hasText rule.From)
            || containsCI rule.From email.FromAddress
            || containsCI rule.From email.FromName
        let subjectOk =
            not (hasText rule.SubjectContains)
            || containsCI rule.SubjectContains email.Subject
        isActive rule && fromOk && subjectOk

    /// The label shown for a rule: its name, or a summary of its criteria.
    let label (rule: Config.EmailRule) : string =
        if hasText rule.Name then rule.Name.Trim()
        else
            [ (if hasText rule.From then Some(rule.From.Trim()) else None)
              (if hasText rule.SubjectContains then Some(sprintf "\"%s\"" (rule.SubjectContains.Trim())) else None) ]
            |> List.choose id
            |> String.concat " · "

    let firstMatch (rules: Config.EmailRule list) (email: Email) : Config.EmailRule option =
        rules |> List.tryFind (fun r -> matches r email)

    /// Folder ids to skip: the given root ids (the built-in well-known
    /// folders), any folder whose display name matches a configured name
    /// (case-insensitive), and every descendant of those.
    let excludedFolderIds
        (folders: MailFolder list)
        (rootIds: Set<string>)
        (excludedNames: string list)
        : Set<string>
        =
        let names = excludedNames |> List.map (fun n -> n.Trim()) |> List.filter hasText
        let nameMatches (f: MailFolder) =
            names
            |> List.exists (fun n ->
                String.Equals(f.DisplayName.Trim(), n, StringComparison.OrdinalIgnoreCase))
        let childrenOf =
            folders
            |> List.groupBy (fun f -> f.ParentFolderId)
            |> List.map (fun (parent, kids) -> parent, kids |> List.map (fun k -> k.FolderId))
            |> Map.ofList
        let roots =
            folders
            |> List.filter nameMatches
            |> List.map (fun f -> f.FolderId)
            |> Set.ofList
            |> Set.union rootIds
        let rec expand (acc: Set<string>) (frontier: string list) =
            match frontier with
            | [] -> acc
            | id :: rest ->
                let kids =
                    Map.tryFind id childrenOf
                    |> Option.defaultValue []
                    |> List.filter (fun k -> not (Set.contains k acc))
                expand (Set.union acc (Set.ofList kids)) (kids @ rest)
        expand roots (Set.toList roots)

    /// Polling cursor. `Since` is the receivedDateTime lower bound for the
    /// next fetch; `Seen` holds every email already evaluated (with its
    /// received time, so it can be pruned once it falls behind `Since`).
    type EmailState =
        { Since: DateTimeOffset
          Seen: Map<EmailId, DateTimeOffset> }

    /// How far behind `now` the cursor trails, so mail that lands with a
    /// slightly earlier receivedDateTime (server delays, clock skew) is still
    /// picked up. Re-fetched mail is de-duplicated via `Seen`.
    let overlap = TimeSpan.FromMinutes 15.0

    /// Given one poll's batch (fetched from `state.Since`), return the alerts
    /// for newly-seen matching emails (oldest first) and the advanced state.
    /// A truncated batch only advances the cursor to the newest message it
    /// actually fetched, so the rest is picked up on the next poll instead
    /// of being skipped.
    let computeNew
        (now: DateTimeOffset)
        (rules: Config.EmailRule list)
        (batch: MailBatch)
        (state: EmailState)
        : EmailAlert list * EmailState
        =
        let fresh =
            batch.Emails
            |> List.distinctBy (fun e -> e.Id)
            |> List.filter (fun e -> not (Map.containsKey e.Id state.Seen))
        let alerts =
            fresh
            |> List.sortBy (fun e -> e.Received)
            |> List.choose (fun e ->
                firstMatch rules e
                |> Option.map (fun r -> { Email = e; RuleName = label r }))
        let target =
            if batch.Truncated then
                batch.NewestReceived |> Option.defaultValue state.Since
            else now - overlap
        let since' = max state.Since target
        let seen' =
            fresh
            |> List.fold (fun acc e -> Map.add e.Id e.Received acc) state.Seen
            |> Map.filter (fun _ received -> received >= since')
        alerts, { Since = since'; Seen = seen' }
