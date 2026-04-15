namespace MeetingNotifier

open System
open System.Diagnostics
open Microsoft.Win32

/// Toggle whether this app runs at Windows login via the per-user Run key.
/// Keeping this in HKCU means no admin prompt, which matches the rest of
/// the app's "no elevated install" stance.
module Startup =

    [<Literal>]
    let private runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run"

    [<Literal>]
    let private valueName = "MeetingNotifier"

    let private exePath () =
        let proc = Process.GetCurrentProcess()
        // Quote the path so spaces in Program Files etc. are preserved.
        sprintf "\"%s\"" proc.MainModule.FileName

    let isEnabled () : bool =
        use key = Registry.CurrentUser.OpenSubKey(runKeyPath, false)
        if isNull key then false
        else
            match key.GetValue(valueName) with
            | null -> false
            | v -> not (String.IsNullOrWhiteSpace(string v))

    let setEnabled (enabled: bool) : unit =
        use key =
            Registry.CurrentUser.CreateSubKey(runKeyPath,
                                              writable = true)
        if enabled then
            key.SetValue(valueName, exePath (), RegistryValueKind.String)
        else
            try key.DeleteValue(valueName, false) with _ -> ()
