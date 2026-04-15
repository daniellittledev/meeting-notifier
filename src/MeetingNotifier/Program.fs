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
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(false)
        Application.SetHighDpiMode(HighDpiMode.SystemAware) |> ignore

        let cfg = Config.loadOrDefault ()

        if String.IsNullOrWhiteSpace cfg.ClientId
           || String.IsNullOrWhiteSpace cfg.TenantId then
            MessageBox.Show(
                sprintf
                    "Please set clientId and tenantId in:\n%s\n\nMeeting Notifier will now open the config and exit."
                    Config.configPath,
                "Meeting Notifier — first-run setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information)
            |> ignore
            try
                System.Diagnostics.Process.Start(
                    System.Diagnostics.ProcessStartInfo(
                        Config.configPath, UseShellExecute = true))
                |> ignore
            with _ -> ()
            0
        else
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
