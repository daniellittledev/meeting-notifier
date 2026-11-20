namespace OutlookNotifier

open System
open OutlookNotifier.Domain

/// Pure email-rule matching and "which emails are new this tick" logic. No
/// I/O, so the matching rules and the polling cursor can be unit-tested.
module EmailRules =

    let private hasText (s: string) = not (String.IsNullOrWhiteSpace s)

    let private containsCI (needle: string) (haystack: string) =
        hasText haystack
        && haystack.IndexOf(needle.Trim(), StringComparison.OrdinalIgnoreCase) >= 0

    /// Split a comma-separated criterion into its non-empty, trimmed terms.
    let private terms (s: string) : string list =
        if isNull s then []
        else
            s.Split(',')
            |> Array.map (fun t -> t.Trim())
            |> Array.filter hasText
            |> List.ofArray

    let private startsWithCI (prefix: string) (s: string) =
        hasText s
        && s.TrimStart().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)

    /// A rule needs at least one matching criterion, otherwise it would match
    /// every email in the mailbox. A subject exclusion doesn't count — on its
    /// own it would match nearly everything.
    let isActive (rule: Config.EmailRule) =
        not (List.isEmpty (terms rule.From))
        || hasText rule.RecipientContains
        || hasText rule.SubjectContains

    let activeRules (rules: Config.EmailRule seq) : Config.EmailRule list =
        rules |> Seq.filter isActive |> List.ofSeq

    /// Does `email` satisfy `rule`? Each criterion that's set must match
    /// (case-insensitive partial match); unset criteria are ignored.
    let matches (rule: Config.EmailRule) (email: Email) : bool =
        let fromTerms = terms rule.From
        let fromOk =
            List.isEmpty fromTerms
            || fromTerms
               |> List.exists (fun t ->
                   containsCI t email.FromAddress || containsCI t email.FromName)
        let recipientOk =
            not (hasText rule.RecipientContains)
            || email.Recipients |> List.exists (containsCI rule.RecipientContains)
        let subjectOk =
            not (hasText rule.SubjectContains)
            || containsCI rule.SubjectContains email.Subject
        let notExcluded =
            terms rule.SubjectExcludesPrefix
            |> List.forall (fun p -> not (startsWithCI p email.Subject))
        isActive rule && fromOk && recipientOk && subjectOk && notExcluded

    /// The label shown for a rule: its name, or a summary of its criteria.
    let label (rule: Config.EmailRule) : string =
        if hasText rule.Name then rule.Name.Trim()
        else
            [ (if hasText rule.From then Some(rule.From.Trim()) else None)
              (if hasText rule.RecipientContains then Some(sprintf "to/cc %s" (rule.RecipientContains.Trim())) else None)
              (if hasText rule.SubjectContains then Some(sprintf "\"%s\"" (rule.SubjectContains.Trim())) else None)
              (if hasText rule.SubjectExcludesPrefix then Some(sprintf "not %s" (rule.SubjectExcludesPrefix.Trim())) else None) ]
            |> List.choose id
            |> String.concat " · "

    let firstMatch (rules: Config.EmailRule list) (email: Email) : Config.EmailRule option =
        rules |> List.tryFind (fun r -> matches r email)

    /// Folder ids to scan in addition to the Inbox: the id of every folder
    /// whose display name matches one of `includedNames` (case-insensitive,
    /// exact name, no sub-folders — a sub-folder is only scanned when named
    /// in its own right). The Inbox is always scanned, so naming it here is a
    /// no-op rather than a second scan of the same folder.
    let includedFolderIds
        (folders: MailFolder list)
        (includedNames: string list)
        : Set<string>
        =
        let names = includedNames |> List.map (fun n -> n.Trim()) |> List.filter hasText
        let isInbox (f: MailFolder) =
            String.Equals(f.DisplayName.Trim(), "Inbox", StringComparison.OrdinalIgnoreCase)
        let nameMatches (f: MailFolder) =
            names
            |> List.exists (fun n ->
                String.Equals(f.DisplayName.Trim(), n, StringComparison.OrdinalIgnoreCase))
        folders
        |> List.filter (fun f -> nameMatches f && not (isInbox f))
        |> List.map (fun f -> f.FolderId)
        |> Set.ofList

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
