using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using PoRepoLineTracker.API.Features.CodeHealth;
using PoRepoLineTracker.API.Features.Contributors;
using PoRepoLineTracker.API.Features.Insights;
using PoRepoLineTracker.API.Features.Repositories;
using PoRepoLineTracker.API.Features.Diagnostics;
using PoRepoLineTracker.API.Hubs;
using Microsoft.AspNetCore.HttpOverrides;
using Azure.Identity;

namespace PoRepoLineTracker.API.Extensions;

/// <summary>
/// SOLID — Composition Root: Centralizes all DI registrations as required by the
/// Composition Root pattern (Mark Seemann). All service lifetimes, decorators,
/// and interceptor configurations are defined in one place.
/// 
/// SOLID — Dependency Inversion Principle: Services are registered by their
/// application-layer interfaces (I*) and resolved to infrastructure implementations.
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Azure Table Storage — Azurite, cloud endpoint, or connection string fallback
        services.AddSingleton<Azure.Data.Tables.TableServiceClient>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connStr = config[ConfigKeys.AzureTableStorage.ConnectionString] ?? config[ConfigKeys.AzureTableStorage.TablesConnectionString];

            if (!string.IsNullOrEmpty(connStr) && connStr.Contains("UseDevelopmentStorage", StringComparison.OrdinalIgnoreCase))
                return new Azure.Data.Tables.TableServiceClient(connStr);

            var storageEndpoint = config[ConfigKeys.AzureTableStorage.ServiceUrl];
            if (!string.IsNullOrEmpty(storageEndpoint))
                return new Azure.Data.Tables.TableServiceClient(new Uri(storageEndpoint), new DefaultAzureCredential());

            if (!string.IsNullOrEmpty(connStr))
                return new Azure.Data.Tables.TableServiceClient(connStr);

            return new Azure.Data.Tables.TableServiceClient("UseDevelopmentStorage=true");
        });

        // OpenAPI
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                document.Info.Title = "PoRepoLineTracker API";
                document.Info.Version = "v1";
                document.Info.Description = "API for tracking repository line counts and code statistics";
                return Task.CompletedTask;
            });
        });

        // Source-generated JSON metadata ahead of the reflection resolver.
        //
        // Inserting at index 0 means every type declared in AppJsonSerializerContext (i.e. every
        // wire contract) is resolved from generated metadata; the default reflection resolver
        // stays behind it as the tail so the dev-only client-log endpoint, whose payload carries
        // a Dictionary<string, object>, still binds. Server-side there is no trimming benefit to
        // removing that tail — the client is where the reflection-free path matters, and it uses
        // AppJsonOptions.Default, which has no fallback at all.
        //
        // PropertyNameCaseInsensitive is no longer set here: JsonSerializerDefaults.Web (which
        // minimal APIs already apply, and which the generated context declares) implies it.
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

        // Antiforgery for every state-changing endpoint (see AntiforgeryMiddleware).
        //
        // HeaderName is what makes this work for a JSON SPA at all: the default validator reads
        // the request token from a form field, and none of these endpoints post a form. With a
        // header configured the pair becomes a standard double-submit — the cookie half stays
        // HttpOnly, and the client echoes the other half from /api/antiforgery/token.
        //
        // The __Host- prefix is applied only where it can be honoured. RFC 6265bis requires a
        // cookie with that prefix to carry Secure, and a browser rejects one that does not — so
        // pairing the prefix with SameAsRequest, as this did, was self-contradictory over plain
        // HTTP: the Set-Cookie went out without Secure, the browser dropped it on the floor, and
        // every state-changing request then failed antiforgery with "the required antiforgery
        // cookie is not present". That made the whole app read-only on the http profile — adding
        // a repository, re-analysing, deleting, saving settings — with a 400 the only symptom.
        //
        // The decision has to be made here, at registration, long before any request exists, so
        // it cannot key off the request scheme. What it keys off instead is one question: will
        // this host be reached over HTTPS? Not "is this Development" — the integration tier runs
        // under the environment name "Test" on plain http://localhost, so an IsDevelopment() test
        // hands it __Host- + Always, TestServer's cookie container dutifully withholds a Secure
        // cookie from an http request, and every write in the suite fails. Config-driven with a
        // secure default keeps deployed slots (including Staging) hardened while letting any
        // plain-HTTP host — the http launch profile, the test server — say so explicitly.
        var requireSecureCookies = configuration.GetValue(
            ConfigKeys.Security.RequireSecureCookies,
            defaultValue: !environment.IsDevelopment());

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;

            if (requireSecureCookies)
            {
                // The prefix defends against a sibling subdomain overwriting the cookie — a real
                // concern for a deployed origin, and not one on localhost.
                options.Cookie.Name = "__Host-PoRepoLineTracker.Antiforgery";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            }
            else
            {
                options.Cookie.Name = "PoRepoLineTracker.Antiforgery";
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            }
        });

        // HybridCache is the single caching abstraction. It is in-memory only here
        // (no IDistributedCache registered), which is correct for a single App Service instance
        // and gains an L2 for free if Redis is ever added. Its stampede protection matters more
        // than raw hit rate: GitHub's REST API is rate-limited per token, so N concurrent page
        // loads must produce one upstream call, not N.
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5),
                LocalCacheExpiration = TimeSpan.FromMinutes(5)
            };
        });

        services.AddHttpClient("GitHubClient", (sp, client) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            client.DefaultRequestHeaders.Add("User-Agent", "PoRepoLineTracker");
            var pat = config[ConfigKeys.GitHub.Pat];
            if (!string.IsNullOrEmpty(pat))
                client.DefaultRequestHeaders.Add("Authorization", $"token {pat}");
        })
        // The standard pipeline replaces the hand-rolled circuit-breaker-only handler:
        // rate limiter → total timeout → retry → circuit breaker → attempt timeout. The retry is
        // what the old configuration was missing; a single 503 from GitHub used to surface to the
        // user instead of being retried with backoff.
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        });

        // Domain services. GitClient and FileIgnoreFilter are registered as their concrete
        // selves — each has one implementation and one consumer (GitHubService), and no test
        // ever substituted the interfaces they used to hide behind. The line-counter catalog
        // lives in SourceLineCounter.DefaultSet (one counter per tracked extension plus the "*"
        // fallback — see the type remarks for why every extension gets the same treatment).
        services.AddSingleton<ClaudeAssistant>();
        services.AddScoped<GitClient>();
        services.AddScoped<FileIgnoreFilter>();

        // Singleton: tracks live analysis progress across the queue's background jobs, and pushes
        // each update to the owning user over AnalysisHub (hence AddSignalR below it — IHubContext
        // has to be resolvable for the singleton above to take a dependency on it).
        services.AddSignalR();
        services.AddSingleton<IAnalysisProgressService, AnalysisProgressService>();
        // Drains the bounded AnalysisHub.ProgressChannel on a single reader thread; takes
        // IHubContext from the SignalR registration above. Long-running by definition.
        services.AddHostedService<AnalysisProgressReader>();
        // The analysis queue: endpoints enqueue, a fixed worker pool drains, and at startup it
        // re-queues whatever a restart interrupted. One instance serves both roles, so it is a
        // singleton the endpoints inject AND the hosted service the host starts.
        services.AddSingleton<AnalysisQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<AnalysisQueue>());

        services.AddScoped<IGitHubService>(sp => new GitHubService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubClient"),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<GitHubService>>(),
            SourceLineCounter.DefaultSet(),
            sp.GetRequiredService<GitClient>(),
            sp.GetRequiredService<FileIgnoreFilter>()));

        services.AddScoped<IRepositoryDataService, RepositoryDataService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IUserPreferencesService, UserPreferencesService>();
        // The per-commit code-health memo. Scoped like the other table services: it holds a
        // TableClient, which is cheap to resolve and safe to share within a request.
        services.AddScoped<ICodeHealthSnapshotStore, CodeHealthSnapshotStore>();

        // Slice handlers, one per route, called directly by their endpoint — no mediator in
        // between. Each has exactly one caller; the registration replaces the old MediatR
        // assembly scan (dropped with its commercial-license nag).
        services.AddScoped<GetAllRepositoriesQueryHandler>();
        services.AddScoped<GetLineCountHistoryQueryHandler>();
        services.AddScoped<GetRepositoryPunchcardQueryHandler>();
        services.AddScoped<GetRepositoryHotspotsQueryHandler>();
        services.AddScoped<GetAllRepositoriesLineCountHistoryQueryHandler>();
        services.AddScoped<GetFileExtensionPercentagesQueryHandler>();
        services.AddScoped<DeleteRepositoryCommandHandler>();
        services.AddScoped<RemoveAllRepositoriesCommandHandler>();
        services.AddScoped<DeleteAccountCommandHandler>();
        services.AddScoped<AddMultipleRepositoriesCommandHandler>();
        services.AddScoped<AnalyzeRepositoryCommitsCommandHandler>();
        services.AddScoped<GetContributorStatsQueryHandler>();
        services.AddScoped<GetPortfolioInsightsQueryHandler>();
        services.AddScoped<GetWeeklyDigestQueryHandler>();
        services.AddScoped<GetYearInReviewQueryHandler>();
        services.AddScoped<GetCodeHealthQueryHandler>();
        services.AddScoped<GetCodeHealthTrendQueryHandler>();
        services.AddScoped<GetPortfolioCodeHealthQueryHandler>();

        // Health checks — all three dependencies share the same registry so /health and
        // /api/diagnostics agree (SPEC §11.3). Names use hyphens to match the documented contract and
        // the diagnostic page's ExternalConnections rows.
        services.AddHealthChecks()
            .AddCheck<AzureTableStorageHealthCheck>("azure-table-storage")
            .AddCheck<KeyVaultHealthCheck>("key-vault")
            .AddCheck<GitHubApiHealthCheck>("github-api");

        // Rate limits. Nothing was limited before: the two endpoints that queue a clone and a
        // full history walk could be called in a loop, and the anonymous /health (a Table query
        // and a GitHub call per hit) by anyone.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            void PerUserPerMinute(string policy, int limit) =>
                options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
                    context.User.UserKey(),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) }));
            PerUserPerMinute(RateLimitPolicies.Analysis, 30);
            PerUserPerMinute(RateLimitPolicies.Assistant, 10);
            options.AddPolicy(RateLimitPolicies.Health, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
            // Higher than /health: one README page view can fetch two badges, and GitHub's image
            // proxy fetches from a handful of addresses for everyone who views it.
            options.AddPolicy(RateLimitPolicies.Public, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        });

        // Forwarded headers for Azure Container Apps reverse proxy
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        return services;
    }
}

/// <summary>Names of the rate-limit policies registered in <c>AddInfrastructure</c>.</summary>
internal static class RateLimitPolicies
{
    /// <summary>Per user: the endpoints that queue a clone + history walk.</summary>
    public const string Analysis = "analysis";

    /// <summary>Per user: the endpoints that make a paid Claude API call.</summary>
    public const string Assistant = "assistant";

    /// <summary>Per client address: the anonymous health probe.</summary>
    public const string Health = "health";

    /// <summary>Per client address: the anonymous badge and share routes.</summary>
    public const string Public = "public";
}
