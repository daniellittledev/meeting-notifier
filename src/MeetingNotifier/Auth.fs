namespace MeetingNotifier

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Identity.Client
open Microsoft.Identity.Client.Extensions.Msal

/// MSAL-based delegated token acquisition with a persistent on-disk cache.
/// Exposes a minimal `TokenProvider` the rest of the app uses; the MSAL
/// types never leak upward.
module Auth =

    /// Scopes are requested per resource so a missing Mail.Read consent can
    /// never break calendar polling: the calendar only ever asks for
    /// Calendars.Read, and the mail source asks for Mail.Read on its own.
    let calendarScopes = [| "Calendars.Read" |]
    let mailScopes = [| "Mail.Read" |]

    /// Attach a DPAPI-protected cache so the refresh token survives restarts.
    let private attachCache (app: IPublicClientApplication) =
        async {
            let storageProps =
                StorageCreationPropertiesBuilder(
                    Path.GetFileName Config.msalCachePath,
                    Path.GetDirectoryName Config.msalCachePath
                ).Build()
            let! helper =
                MsalCacheHelper.CreateAsync(storageProps)
                |> Async.AwaitTask
            helper.RegisterCache(app.UserTokenCache)
            return ()
        }

    /// Raised when a token needs an interactive sign-in that the caller
    /// didn't allow (e.g. a scope the user hasn't consented to yet).
    exception SignInRequired of string

    /// `extraConsent` scopes are consented to in the SAME browser prompt as
    /// any interactive sign-in (e.g. Mail.Read alongside the calendar), so the
    /// user signs in once. Tokens are still requested per resource.
    type TokenProvider(app: IPublicClientApplication, extraConsent: string[]) =

        // Set when a combined prompt failed (e.g. Mail.Read needs admin
        // approval, or the user backed out): from then on sign-in asks for
        // the requested scopes only, so the calendar can't be blocked by mail.
        let mutable extraConsentDeclined = false

        let prompt (scopes: string[]) (extras: string[]) (ct: CancellationToken) =
            let builder =
                app.AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(SystemWebViewOptions())
            let builder =
                if Array.isEmpty extras then builder
                else builder.WithExtraScopesToConsent(extras)
            builder.ExecuteAsync(ct) |> Async.AwaitTask

        let interactive (scopes: string[]) (ct: CancellationToken) =
            async {
                let extras =
                    if extraConsentDeclined then [||]
                    else extraConsent |> Array.except scopes
                if Array.isEmpty extras then
                    return! prompt scopes [||] ct
                else
                    try
                        return! prompt scopes extras ct
                    with
                    | :? MsalException as ex ->
                        extraConsentDeclined <- true
                        // Respect an explicit cancel; otherwise retry right
                        // away without the extra scopes.
                        if ex.ErrorCode = MsalError.AuthenticationCanceledError then
                            return raise ex
                        else
                            return! prompt scopes [||] ct
            }

        /// Silently refresh a token for `scopes`, falling back to the system
        /// browser only when `allowInteractive` is set; otherwise a needed
        /// sign-in raises `SignInRequired`.
        member _.GetTokenFor(scopes: string[], allowInteractive: bool, ?ct: CancellationToken) : Async<string> =
            async {
                let ct = defaultArg ct CancellationToken.None
                let! accounts = app.GetAccountsAsync() |> Async.AwaitTask
                let needSignIn () =
                    async {
                        if not allowInteractive then
                            return raise (SignInRequired(String.Join(", ", scopes)))
                        else
                            let! r = interactive scopes ct
                            return r.AccessToken
                    }
                match Seq.tryHead accounts with
                | None -> return! needSignIn ()
                | Some acc ->
                    let! silent =
                        async {
                            try
                                let! r = app.AcquireTokenSilent(scopes, acc).ExecuteAsync(ct) |> Async.AwaitTask
                                return Some r.AccessToken
                            with
                            | :? MsalUiRequiredException -> return None
                            | :? AggregateException as ae when (ae.InnerException :? MsalUiRequiredException) -> return None
                        }
                    match silent with
                    | Some t -> return t
                    | None -> return! needSignIn ()
            }

        /// Calendar token: silent, or the system browser on first use.
        /// Subsequent restarts should never hit the interactive path because
        /// the cache holds the refresh token.
        member this.GetAccessToken(?ct: CancellationToken) : Async<string> =
            this.GetTokenFor(calendarScopes, true, ?ct = ct)

        /// True once a combined sign-in prompt failed, so callers shouldn't
        /// prompt for the extra scopes again this session.
        member _.ExtraConsentDeclined = extraConsentDeclined

        member _.HasCachedAccount() : Async<bool> =
            async {
                let! accounts = app.GetAccountsAsync() |> Async.AwaitTask
                return not (Seq.isEmpty accounts)
            }

        /// Remove all cached accounts — used by a "Sign out" menu action.
        member _.SignOut() : Async<unit> =
            async {
                extraConsentDeclined <- false
                let! accounts = app.GetAccountsAsync() |> Async.AwaitTask
                for acc in accounts do
                    do! app.RemoveAsync(acc) |> Async.AwaitTask
            }

    /// Build an MSAL public client from config values, attach the cache,
    /// and wrap it as a `TokenProvider`.
    let createProvider (cfg: Config.AppConfig) : Async<TokenProvider> =
        async {
            if String.IsNullOrWhiteSpace cfg.ClientId then
                invalidArg "clientId" "config.clientId is required"
            if String.IsNullOrWhiteSpace cfg.TenantId then
                invalidArg "tenantId" "config.tenantId is required"
            let app =
                PublicClientApplicationBuilder
                    .Create(cfg.ClientId)
                    .WithTenantId(cfg.TenantId)
                    .WithRedirectUri("http://localhost")
                    .Build()
            do! attachCache app
            // Consent to Mail.Read in the first sign-in when there are email
            // rules, so there's one prompt, not two.
            let extra =
                if List.isEmpty (EmailRules.activeRules cfg.EmailRules) then [||]
                else mailScopes
            return TokenProvider(app, extra)
        }
