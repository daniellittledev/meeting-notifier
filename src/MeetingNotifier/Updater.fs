namespace MeetingNotifier

open Velopack
open Velopack.Sources

/// Self-update via Velopack against this repo's GitHub Releases. All calls
/// are no-ops unless the app is running from a Velopack-installed build
/// (i.e. not `dotnet run` or a loose publish), so it's always safe to call.
module Updater =

    [<Literal>]
    let private repoUrl = "https://github.com/daniellittledev/meeting-notifier"

    /// An UpdateManager, but only when we're an installed build with
    /// somewhere to update *to*. None otherwise.
    let private tryManager () : UpdateManager option =
        try
            let source = GithubSource(repoUrl, (null: string), false)
            let mgr = UpdateManager(source)
            if mgr.IsInstalled then Some mgr else None
        with _ -> None

    /// Check for a newer release and, if found, download it. Returns the
    /// manager + update info ready to apply, or None if up to date / not an
    /// installed build / the check failed (failures are non-fatal — a tray
    /// app shouldn't die because GitHub was unreachable).
    let checkForUpdate () : Async<(UpdateManager * UpdateInfo) option> =
        async {
            match tryManager () with
            | None -> return None
            | Some mgr ->
                try
                    let! info = mgr.CheckForUpdatesAsync() |> Async.AwaitTask
                    if isNull (box info) then return None
                    else
                        do! mgr.DownloadUpdatesAsync(info) |> Async.AwaitTask
                        return Some(mgr, info)
                with _ -> return None
        }

    /// Apply a previously-downloaded update and restart. Does not return on
    /// success — Velopack exits the process to swap files in.
    let applyAndRestart (mgr: UpdateManager) (info: UpdateInfo) =
        mgr.ApplyUpdatesAndRestart(info)
