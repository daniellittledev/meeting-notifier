namespace MeetingNotifier

open System
open System.IO
open System.Runtime.InteropServices

/// Unpackaged Win32 apps need a Start-Menu `.lnk` whose
/// `System.AppUserModel.ID` property matches the AUMID passed to
/// `ToastNotificationManager.CreateToastNotifier`. Without it, constructing
/// the notifier throws. We create the shortcut at first run (or whenever
/// it's missing/stale) and write our AUMID into its property store.
module AumidRegistration =

    // ---- IShellLinkW / IPersistFile / IPropertyStore COM interop ----

    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type private PROPERTYKEY =
        val fmtid: Guid
        val pid: uint32
        new (fmt, p) = { fmtid = fmt; pid = p }

    // PROPVARIANT is 24 bytes on both x86 and x64 (8-byte header +
    // 16-byte union). Fix the size explicitly so we don't have to care
    // about which union field is actually live — InitPropVariantFromString
    // fills it in for us.
    [<Struct; StructLayout(LayoutKind.Sequential, Size = 24)>]
    type private PROPVARIANT =
        val mutable vt: uint16
        val mutable wReserved1: uint16
        val mutable wReserved2: uint16
        val mutable wReserved3: uint16

    [<DllImport("Ole32.dll", CharSet = CharSet.Unicode)>]
    extern int private PropVariantClear(PROPVARIANT& pvar)

    [<DllImport("Ole32.dll", CharSet = CharSet.Unicode)>]
    extern int private InitPropVariantFromString(
        [<MarshalAs(UnmanagedType.LPWStr)>] string psz,
        PROPVARIANT& ppropvar)

    [<ComImport; Guid("000214F9-0000-0000-C000-000000000046")>]
    [<InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IShellLinkW =
        abstract GetPath : [<Out; MarshalAs(UnmanagedType.LPWStr)>] pszFile: System.Text.StringBuilder * cchMaxPath: int * [<Out>] pfd: IntPtr * fFlags: uint32 -> int
        abstract GetIDList : [<Out>] ppidl: IntPtr -> int
        abstract SetIDList : pidl: IntPtr -> int
        abstract GetDescription : [<Out; MarshalAs(UnmanagedType.LPWStr)>] pszName: System.Text.StringBuilder * cchMaxName: int -> int
        abstract SetDescription : [<MarshalAs(UnmanagedType.LPWStr)>] pszName: string -> int
        abstract GetWorkingDirectory : [<Out; MarshalAs(UnmanagedType.LPWStr)>] pszDir: System.Text.StringBuilder * cchMaxPath: int -> int
        abstract SetWorkingDirectory : [<MarshalAs(UnmanagedType.LPWStr)>] pszDir: string -> int
        abstract GetArguments : [<Out; MarshalAs(UnmanagedType.LPWStr)>] pszArgs: System.Text.StringBuilder * cchMaxPath: int -> int
        abstract SetArguments : [<MarshalAs(UnmanagedType.LPWStr)>] pszArgs: string -> int
        abstract GetHotkey : [<Out>] pwHotkey: uint16 byref -> int
        abstract SetHotkey : wHotkey: uint16 -> int
        abstract GetShowCmd : [<Out>] piShowCmd: int byref -> int
        abstract SetShowCmd : iShowCmd: int -> int
        abstract GetIconLocation : [<Out; MarshalAs(UnmanagedType.LPWStr)>] pszIconPath: System.Text.StringBuilder * cchIconPath: int * [<Out>] piIcon: int byref -> int
        abstract SetIconLocation : [<MarshalAs(UnmanagedType.LPWStr)>] pszIconPath: string * iIcon: int -> int
        abstract SetRelativePath : [<MarshalAs(UnmanagedType.LPWStr)>] pszPathRel: string * dwReserved: uint32 -> int
        abstract Resolve : hwnd: IntPtr * fFlags: uint32 -> int
        abstract SetPath : [<MarshalAs(UnmanagedType.LPWStr)>] pszFile: string -> int

    [<ComImport; Guid("0000010b-0000-0000-C000-000000000046")>]
    [<InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IPersistFile =
        abstract GetClassID : [<Out>] pClassID: Guid byref -> int
        abstract IsDirty : unit -> int
        abstract Load : [<MarshalAs(UnmanagedType.LPWStr)>] pszFileName: string * dwMode: uint32 -> int
        abstract Save : [<MarshalAs(UnmanagedType.LPWStr)>] pszFileName: string * fRemember: bool -> int
        abstract SaveCompleted : [<MarshalAs(UnmanagedType.LPWStr)>] pszFileName: string -> int
        abstract GetCurFile : [<Out; MarshalAs(UnmanagedType.LPWStr)>] ppszFileName: string byref -> int

    [<ComImport; Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")>]
    [<InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IPropertyStore =
        abstract GetCount : [<Out>] cProps: uint32 byref -> int
        abstract GetAt : iProp: uint32 * [<Out>] pkey: PROPERTYKEY byref -> int
        abstract GetValue : key: PROPERTYKEY byref * [<Out>] pv: PROPVARIANT byref -> int
        abstract SetValue : key: PROPERTYKEY byref * pv: PROPVARIANT byref -> int
        abstract Commit : unit -> int

    [<ComImport; Guid("00021401-0000-0000-C000-000000000046")>]
    type private CShellLink = class end

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5
    let private pkeyAumid =
        PROPERTYKEY(Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5u)

    let private shortcutPath aumid =
        let programs =
            Environment.GetFolderPath Environment.SpecialFolder.Programs
        Path.Combine(programs, $"MeetingNotifier.lnk")

    let private currentExePath () =
        let proc = System.Diagnostics.Process.GetCurrentProcess()
        // On .NET 8 MainModule.FileName returns the host exe path.
        proc.MainModule.FileName

    /// Create (or overwrite) the Start-Menu `.lnk` with the AUMID property
    /// set. Idempotent — safe to call at every startup.
    let ensure (aumid: string) : unit =
        try
            let lnk = shortcutPath aumid
            // Ensure the Programs directory exists (on some locked-down
            // profiles it might not). Failing silently is fine: toast
            // notifications will just be a no-op, which the user will
            // quickly notice.
            let dir = Path.GetDirectoryName lnk
            if not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore

            let link =
                Activator.CreateInstance(typeof<CShellLink>) :?> IShellLinkW
            let exe = currentExePath ()
            link.SetPath(exe) |> ignore
            link.SetArguments("") |> ignore
            link.SetWorkingDirectory(Path.GetDirectoryName exe) |> ignore
            link.SetDescription("Meeting Notifier") |> ignore

            // Write the AUMID property.
            let store = link :?> IPropertyStore
            let mutable pv = Unchecked.defaultof<PROPVARIANT>
            let mutable key = pkeyAumid
            InitPropVariantFromString(aumid, &pv) |> ignore
            store.SetValue(&key, &pv) |> ignore
            store.Commit() |> ignore
            PropVariantClear(&pv) |> ignore

            let persist = link :?> IPersistFile
            persist.Save(lnk, true) |> ignore
        with _ ->
            // Registration failures shouldn't crash the app; the user will
            // notice the missing toasts and we'll log to stderr.
            eprintfn "AUMID registration failed (toasts may not work)."
