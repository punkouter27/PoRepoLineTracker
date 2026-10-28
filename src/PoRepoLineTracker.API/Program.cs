using PoRepoLineTracker.API.Platform;
using Serilog;
using Scalar.AspNetCore;
using Azure.Identity;
using PoRepoLineTracker.API.Hubs;
using Microsoft.AspNetCore.Http;

namespace PoRepoLineTracker.API
{
    public partial class Program
    {
        public static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .CreateBootstrapLogger();

            try
            {
                var app = CreateWebApplication(args);
                app.Run();
            }
            catch (Exception ex) when (ex is not HostAbortedException)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static WebApplication CreateWebApplication(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Azure Key Vault — managed identity in production; DefaultAzureCredential locally
            var keyVaultUrl = builder.Configuration[ConfigKeys.KeyVault.Uri];
            if (!string.IsNullOrEmpty(keyVaultUrl))
            {
                try
                {
                    builder.Configuration.AddAzureKeyVault(
                        new Uri(keyVaultUrl),
                        new DefaultAzureCredential(),
                        new PrefixKeyVaultSecretManager());
                    Log.Information("Azure Key Vault configuration loaded from {KeyVaultUrl}", keyVaultUrl);
                }
                catch (Exception ex) when (ex is Azure.Identity.CredentialUnavailableException or System.AggregateException)
                {
                    Log.Warning(ex, "Azure Key Vault unavailable — secrets must come from user-secrets, environment variables, or appsettings.Development.local.json. KeyVault:Uri={KeyVaultUrl}", keyVaultUrl);
                }
            }
            else
            {
                Log.Warning("KeyVault:Uri not configured — secrets must come from user-secrets or environment variables");
            }

            // Local developer override (not committed).
            // Loaded unconditionally (optional: true) so that secrets are available
            // regardless of ASPNETCORE_ENVIRONMENT — prevents the "GitHub:ClientId is
            // not configured" 500 when the env var is accidentally omitted at startup.
            builder.Configuration.AddJsonFile("appsettings.Development.local.json", optional: true, reloadOnChange: true);

            // Structured logging via Serilog
            builder.Host.UseSerilog((context, services, cfg) =>
            {
                cfg
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty(PoPlatform.ApplicationProperty, PoPlatform.AppName)
                    .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
                    .MinimumLevel.Information();

                if (context.HostingEnvironment.IsDevelopment())
                {
                    cfg.WriteTo.File("logs/log.txt",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        shared: true,
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
                }

            });

            // Service registrations via extension methods
            builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
            builder.Services.AddAuth(builder.Configuration, builder.Environment);
            builder.Services.AddTelemetry(builder.Configuration, builder.Environment);

            var app = builder.Build();

            // Fail at startup, where the deploy's health gate sees it, rather than at the first
            // sign-in or table read: without these Production falls back to cookie-only auth and
            // to Azurite, neither of which works there. The GitHub pair comes from Key Vault
            // (PoRepoLineTracker--GitHub--*), the storage URL from an App Setting.
            if (app.Environment.IsProduction())
            {
                string[] required = [ConfigKeys.GitHub.ClientId, ConfigKeys.GitHub.ClientSecret, ConfigKeys.AzureTableStorage.ServiceUrl];
                var missing = required.Where(key => string.IsNullOrWhiteSpace(app.Configuration[key])).ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException($"Missing required Production configuration: {string.Join(", ", missing)}");
            }

            // Middleware pipeline
            app.UseForwardedHeaders();
            // The commit this build came from (SDK-stamped as "<version>+<sha>"), so a deploy can
            // prove which build is answering: CI checks /health/live for X-Commit equal to the pushed sha.
            var commit = PoPlatform.Version.Split('+') is [_, var sha, ..] ? sha : "unknown";
            app.Use((context, next) =>
            {
                context.Response.Headers["X-Commit"] = commit;
                return next(context);
            });
            app.UseMiddleware<LogEnrichmentMiddleware>();
            app.UseMiddleware<SecurityHeadersMiddleware>();
            app.UseMiddleware<ExceptionHandlingMiddleware>();

            if (app.Environment.IsDevelopment())
            {
                // AllowAnonymous: the FallbackPolicy would otherwise put the local
                // API reference behind a login, which defeats its purpose during development.
                app.MapOpenApi().AllowAnonymous();
                app.MapScalarApiReference(options =>
                {
                    options.WithTitle("PoRepoLineTracker API");
                    options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
                }).AllowAnonymous();
            }

            // Only redirect to HTTPS in non-Development environments.
            // In Development the http profile runs without an HTTPS URL, so the
            // middleware can't determine the redirect port and logs a WRN on every request.
            if (!app.Environment.IsDevelopment())
            {
                app.UseHttpsRedirection();
            }

            // Static assets MUST be served before UseAuthorization. The FallbackPolicy
            // is applied by the authorization middleware to requests that matched no endpoint —
            // and static files are served by middleware, not endpoints. With these two calls after
            // UseAuthorization every asset (css/app.css, _framework/*) answered 302-to-login, so the
            // browser got a login page instead of the Blazor runtime and rendered the "unhandled
            // error" shell. Serving them first also matches the documented middleware order.
            app.UseBlazorFrameworkFiles();

            // In Development, force revalidation on every Blazor framework asset. The Blazor
            // runtime's SRI hash for `_framework/<dll>` is computed against the bytes the browser
            // actually receives. When a developer rebuilds the client (the `lna3jt6cyo` content
            // hash flips to a new value), the browser's HTTP cache can otherwise hold the OLD
            // `_framework/blazor.boot.json`, which lists the OLD hash but the OLD content body
            // was already evicted — the integrity check sees an empty body, fails the SRI, and
            // the page never boots. `Cache-Control: no-cache` (NOT `no-store`) keeps the cache
            // slot for performance but forces a 304 round-trip on every reload, so the browser
            // sees fresh hashes that match the freshly-built files.
            if (app.Environment.IsDevelopment())
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        // /_framework/* only — the Blazor runtime assets whose hashes flip on rebuild.
                        // Leaving app.css / favicon.svg / etc. alone keeps the fast cache path for them.
                        if (ctx.Context.Request.Path.StartsWithSegments("/_framework"))
                        {
                            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
                        }
                    }
                });
            }
            else
            {
                app.UseStaticFiles();
            }

            // Before authentication/authorization on purpose: an /api path that matched no route
            // is a 404 regardless of who is asking, and the authorization FallbackPolicy would
            // otherwise answer 401 first — making a typo indistinguishable from an expired session.
            // Routing has already run by here, so the matched endpoint (or the absence of one) is
            // known; static files were served earlier still and never reach this.
            app.UseMiddleware<ApiNotFoundMiddleware>();

            app.UseAuthentication();

            // After authentication, not in LogEnrichmentMiddleware at the top of the pipeline:
            // there the principal has not been established yet, so every log line of every
            // request was tagged UserId "anonymous".
            app.Use(async (context, next) =>
            {
                using (Serilog.Context.LogContext.PushProperty("UserId", context.User.UserKey()))
                {
                    await next(context);
                }
            });

            app.UseAuthorization();
            app.UseRateLimiter();

            // Production Auth Enforcement: require Microsoft/GitHub OAuth in Production.
            // In Development this is a no-op (GUEST mode and local testing still work).
            app.UseMiddleware<ProductionAuthEnforcementMiddleware>();

            // Antiforgery on every state-changing /api endpoint.
            //
            // Ordered AFTER UseAuthorization deliberately: an unauthenticated POST then still
            // answers 401 rather than 400, which is both the more useful diagnosis and what the
            // API tier asserts. It also has to sit after UseRouting (implicit here) so that
            // GetEndpoint() can see the SkipAntiforgery metadata for the matched endpoint.
            app.UseMiddleware<AntiforgeryMiddleware>();

            // All API route mappings - MUST come BEFORE fallback file
            app.MapApiEndpoints();

            // Live analysis progress. Deliberately outside /api: AntiforgeryMiddleware validates
            // every unsafe method under that prefix, and the SignalR negotiate handshake is a POST
            // that carries no CSRF token. The hub is [Authorize] and scopes every connection by the
            // caller's own claim, so it is not relying on the prefix for protection.
            app.MapHub<AnalysisHub>("/hubs/analysis");

            // Both must opt out of the FallbackPolicy: /health is polled by the
            // deploy smoke test and Azure's probe with no credential, and the fallback file is
            // the Blazor shell itself — gating it would make the login page unreachable.
            app.MapHealthChecks("/health").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Health);
            // Uniform cross-app liveness probe (see PoPlatform). Same shape in every Po app, which
            // is what lets the portfolio dashboard poll them all and render one uptime grid.
            app.MapPoLiveness();
            // Marked so ApiNotFoundMiddleware can tell "the SPA shell caught it" from "a real API
            // route answered" — this endpoint matches /api/typo too, and used to serve HTML to
            // callers expecting JSON.
            app.MapFallbackToFile("index.html").AllowAnonymous().WithMetadata(new SpaFallbackAttribute());

            return app;
        }
    }
}