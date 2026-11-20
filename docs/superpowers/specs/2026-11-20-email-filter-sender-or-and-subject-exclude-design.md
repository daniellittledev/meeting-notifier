# Email rules: multi-sender From and subject exclusion

## Intent / why

GitHub notifications all arrive from `notifications@github.com`, so the
sender *name* is the only thing that distinguishes one author from another.
A user wants one rule to catch notifications from either of two named
authors (e.g. *Tom Dullemond* or *Adam Grande*), and to drop reply/forward
noise — subjects starting with `Re:` or `Fwd:`.

## Goals

- A rule's `From` can list several sender terms; it matches when **any** one
  hits the sender name or address (OR), so both authors fit in one rule.
- A rule can skip emails whose subject **starts with** a configured term
  (e.g. `Re:`, `Fwd:`), so replies and forwards don't surface.

## Non-goals / anti-goals

- No multi-value support for the To/Cc or Subject-contains criteria (out of
  scope; `From` only).
- Exclusion is a *prefix* match, not a substring match: excluding `Re:` must
  not drop a subject like `Core: sprint`.
- An exclusion alone never makes a rule active — it only narrows a rule that
  already matches on an inclusion criterion. Otherwise a lone exclusion would
  match nearly the whole mailbox.

## Approaches considered

1. **Multi-value `From` + a prefix "subject excludes" field** (chosen). The
   `From` string becomes a comma-separated OR list; a new
   `SubjectExcludesPrefix` string (also comma-separated) drops a match when
   the subject starts with any listed term. Keeps the GitHub filter as one
   rule and reads naturally.
2. Two separate rules per author + a fixed "skip replies" toggle. Rejected:
   duplicates a rule per author and bakes in `Re:`/`Fwd:` rather than letting
   the user say what to skip.

## The how (decided)

- `Config.EmailRule` gains `SubjectExcludesPrefix: string`. Missing in old
  JSON → null → normalised to `""` in `sanitize` (back-compatible).
- `EmailRules`:
  - `From` is split on commas into terms; `fromOk` is true when the From
    criterion is empty or **any** term matches sender name or address.
  - `SubjectExcludesPrefix` is split on commas into prefixes; a rule is
    rejected when the subject starts with any prefix (case-insensitive,
    after trimming leading space).
  - `isActive` is unchanged — the exclusion does not count as a criterion.
  - `label`'s generated summary notes the exclusion (e.g. `not Re:, Fwd:`).
- `SettingsWindow` rule dialog gains a "Skip if subject starts with" box
  (hint: `Re:, Fwd:`); the From label notes it accepts a comma-separated
  list. `isActive`-based save validation already keeps exclusion-only rules
  unsaveable.

## Left to the builder

- TDD: tests first in `EmailRulesTests` (multi-value From OR; prefix
  exclusion incl. the `Core:` false-positive case; case-insensitivity;
  exclusion-only rule inactive) and a `ConfigTests` round-trip for the new
  field default. Update every `EmailRule` literal for the new field.
