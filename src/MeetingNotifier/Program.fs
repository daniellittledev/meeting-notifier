namespace MeetingNotifier

open System
open System.Net.Http
open System.Threading
open System.Windows.Forms

/// Entry point — minimal wiring. Everything interesting lives in the
/// modules this composes.
module Program =

    [<EntryPoint; STAThread>]
    let main _ =
        // Must run before anything else: during install/update/uninstall
        // Velopack invokes the exe with hook arguments, services them, and
        // exits. On a normal launch this is a cheap no-op.
        Velopack.VelopackApp.Build().Run()

        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(false)
        Application.SetHighDpiMode(HighDpiMode.SystemAware) |> ignore

        let loaded = Config.loadOrDefault ()

        // First run (or a config still missing its auth IDs): collect the
        // Azure AD values via a dialog rather than making the user hand-edit
        // JSON. None means the user cancelled, so there's nothing to run.
        let cfgOpt =
            if String.IsNullOrWhiteSpace loaded.ClientId
               || String.IsNullOrWhiteSpace loaded.TenantId then
                match SetupDialog.show loaded with
                | Some(clientId, tenantId) ->
                    let updated =
                        { loaded with ClientId = clientId; TenantId = tenantId }
                    Config.save updated
                    Some updated
                | None -> None
            else
                Some loaded

        match cfgOpt with
        | None -> 0
        | Some cfg ->
            // Register the AUMID shortcut so toast notifications work.
            AumidRegistration.ensure "MeetingNotifier.App"

            // Build the MSAL token provider synchronously on the UI thread
            // so first-run interactive login blocks the tray from appearing
            // until consent is granted.
            let tokenProvider =
                Async.RunSynchronously(Auth.createProvider cfg)

            // Warm up the token on a background thread; if interactive
            // login is needed the system browser will pop. Failures here
            // are surfaced on the first poll tick.
            Async.Start(async {
                try
                    let! _ = tokenProvider.GetAccessToken()
                    return ()
                with ex ->
                    eprintfn "initial token acquisition failed: %s" ex.Message
            })

            let http = new HttpClient()
            http.Timeout <- TimeSpan.FromSeconds 30.0

            let source =
                GraphClient.GraphCalendarSource(http, tokenProvider)
                :> GraphClient.ICalendarSource

            // Install a WinForms sync context so we can Post onto the UI
            // thread from the background polling loop.
            let uiContext = WindowsFormsSynchronizationContext()
            SynchronizationContext.SetSynchronizationContext uiContext

            use tray =
                new TrayApp.TrayContext(cfg, source, tokenProvider, uiContext)
            Application.Run(tray)
            http.Dispose()
            0
