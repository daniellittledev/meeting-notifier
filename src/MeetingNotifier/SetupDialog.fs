namespace MeetingNotifier

open System
open System.Diagnostics
open System.Drawing
open System.Windows.Forms

/// First-run configuration dialog. Collects the Azure AD app-registration
/// values the app needs before it can authenticate, so users don't have to
/// hand-edit config.json. Returns the entered (clientId, tenantId) on Save,
/// or None if the user cancelled.
module SetupDialog =

    [<Literal>]
    let private helpUrl =
        "https://github.com/daniellittledev/meeting-notifier#first-run-setup"

    let show (current: Config.AppConfig) : (string * string) option =
        use form =
            new Form(
                Text = "Meeting Notifier — setup",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = true,
                ClientSize = Size(460, 250))

        let intro =
            new Label(
                Text =
                    "Meeting Notifier needs an Azure AD app registration to "
                    + "read your calendar. Enter the Application (client) ID "
                    + "and Directory (tenant) ID from your app registration.",
                AutoSize = false,
                Location = Point(16, 14),
                Size = Size(428, 48))

        let helpLink =
            new LinkLabel(
                Text = "How do I get these?",
                AutoSize = true,
                Location = Point(16, 62))
        helpLink.LinkClicked.Add(fun _ ->
            try
                Process.Start(ProcessStartInfo(helpUrl, UseShellExecute = true))
                |> ignore
            with _ -> ())

        let clientLabel =
            new Label(Text = "Client ID:", AutoSize = true, Location = Point(16, 102))
        let clientBox =
            new TextBox(
                Location = Point(120, 98),
                Size = Size(324, 23),
                Text = current.ClientId)

        let tenantLabel =
            new Label(Text = "Tenant ID:", AutoSize = true, Location = Point(16, 140))
        let tenantBox =
            new TextBox(
                Location = Point(120, 136),
                Size = Size(324, 23),
                Text = current.TenantId)

        let saveButton =
            new Button(
                Text = "Save",
                DialogResult = DialogResult.OK,
                Location = Point(268, 204),
                Size = Size(84, 30))
        let cancelButton =
            new Button(
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = Point(360, 204),
                Size = Size(84, 30))

        // Only allow saving once both IDs are filled in.
        let validate () =
            saveButton.Enabled <-
                not (String.IsNullOrWhiteSpace clientBox.Text)
                && not (String.IsNullOrWhiteSpace tenantBox.Text)
        clientBox.TextChanged.Add(fun _ -> validate ())
        tenantBox.TextChanged.Add(fun _ -> validate ())
        validate ()

        form.Controls.AddRange(
            [| intro :> Control
               helpLink :> Control
               clientLabel :> Control
               clientBox :> Control
               tenantLabel :> Control
               tenantBox :> Control
               saveButton :> Control
               cancelButton :> Control |])
        form.AcceptButton <- saveButton
        form.CancelButton <- cancelButton

        match form.ShowDialog() with
        | DialogResult.OK -> Some(clientBox.Text.Trim(), tenantBox.Text.Trim())
        | _ -> None
