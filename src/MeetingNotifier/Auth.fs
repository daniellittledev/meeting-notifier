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

    let private scopes = [| "Calendars.Read" |]

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

    type TokenProvider(app: IPublicClientApplication) =
        /// Either silently refresh or, on first use, pop the system browser
        /// for interactive login. Subsequent restarts should never hit the
        /// interactive path because the cache holds the refresh token.
        member _.GetAccessToken(?ct: CancellationToken) : Async<string> =
            async {
                let ct = defaultArg ct CancellationToken.None
                let! accounts =
                    app.GetAccountsAsync() |> Async.AwaitTask
                let account = accounts |> Seq.tryHead
                try
                    let! result =
                        match account with
                        | Some acc ->
                            app.AcquireTokenSilent(scopes, acc)
                                .ExecuteAsync(ct)
                            |> Async.AwaitTask
                        | None ->
                            app.AcquireTokenInteractive(scopes)
                                .WithUseEmbeddedWebView(false)
                                .WithSystemWebViewOptions(SystemWebViewOptions())
                                .ExecuteAsync(ct)
                            |> Async.AwaitTask
                    return result.AccessToken
                with
                | :? MsalUiRequiredException ->
                    let! result =
                        app.AcquireTokenInteractive(scopes)
                            .WithUseEmbeddedWebView(false)
                            .WithSystemWebViewOptions(SystemWebViewOptions())
                            .ExecuteAsync(ct)
                        |> Async.AwaitTask
                    return result.AccessToken
            }

        member _.HasCachedAccount() : Async<bool> =
            async {
                let! accounts = app.GetAccountsAsync() |> Async.AwaitTask
                return not (Seq.isEmpty accounts)
            }

        /// Remove all cached accounts — used by a "Sign out" menu action.
        member _.SignOut() : Async<unit> =
            async {
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
            return TokenProvider(app)
        }
