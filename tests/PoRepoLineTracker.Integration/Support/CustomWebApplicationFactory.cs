using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Azure.Data.Tables;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace PoRepoLineTracker.Integration
{
    /// <summary>
    /// Test authentication handler that always authenticates requests with a test user
    /// </summary>
    public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string TestUserId = "00000000-0000-0000-0000-000000000001";
        public const string TestUsername = "testuser";

        /// <summary>
        /// Requests carrying this header are treated as signed out, so a single host can serve
        /// both the authenticated tests and the FallbackPolicy denial tests. Spinning
        /// up a second WebApplicationFactory instead races the entry-point resolver.
        /// </summary>
        public const string AnonymousHeader = "X-Test-Anonymous";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.ContainsKey(AnonymousHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "github-123"),
                new Claim(ClaimTypes.Name, TestUsername),
                new Claim("UserId", TestUserId),
                new Claim("GitHubId", "github-123"),
                new Claim("Username", TestUsername),
            };
            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        /// <summary>
        /// Scratch directory the app is allowed to delete. Isolated per process so a destructive
        /// handler can never reach the system temp root (or another test run's files).
        /// </summary>
        internal static readonly string TestRepoRoot =
            Path.Combine(Path.GetTempPath(), $"PoRepoLineTracker.Tests-{Environment.ProcessId}");

        /// <summary>
        /// A client that performs the antiforgery token dance on state-changing calls.
        ///
        /// Tests that assert on write endpoints use this; tests asserting that an *unauthenticated*
        /// write is rejected keep using <see cref="WebApplicationFactory{TEntryPoint}.CreateClient"/>,
        /// since the point there is the 401 that authorization raises before antiforgery is reached.
        /// </summary>
        public HttpClient CreateAntiforgeryClient()
        {
            // The redirect + cookie handlers must be listed explicitly. CreateClient() adds them
            // via WebApplicationFactoryClientOptions, but the CreateDefaultClient(handlers)
            // overload does not — and without CookieContainerHandler the antiforgery cookie set
            // by /api/antiforgery/token is dropped, so the token half arrives with nothing to
            // pair against and every write answers 400.
            //
            // Order matters: AntiforgeryTestHandler outermost so it can add the header, and the
            // cookie container innermost so it sees Set-Cookie from the raw server response.
            var options = new WebApplicationFactoryClientOptions();
            return CreateDefaultClient(
                options.BaseAddress,
                new AntiforgeryTestHandler(),
                new RedirectHandler(options.MaxAutomaticRedirections),
                new CookieContainerHandler());
        }

        private UserPreferences _storedPreferences = new()
        {
            UserId = UserId.Parse(TestAuthHandler.TestUserId),
            FileExtensions = UserPreferences.DefaultFileExtensions,
            LastUpdated = DateTime.UtcNow
        };

        // Set by ConfigureAppConfiguration, read by ConfigureServices
        private bool _azuriteAvailable;
        private string _azuriteConnectionString = "";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Set content root to temp directory to prevent loading appsettings.Development.json
            // from the API project output directory (which contains real Azure Key Vault URI)
            builder.UseContentRoot(System.IO.Path.GetTempPath());

            // UseSetting, NOT the inMemorySettings below. This one is read at REGISTRATION time
            // by AddInfrastructure, and ConfigureAppConfiguration delegates do not run until
            // after Program.cs has already executed its top-level statements — so a key added
            // there arrives too late to be seen. Everything else in this factory is read lazily
            // at request time, which is why only this one needs the earlier hook.
            //
            // What it declares: TestServer speaks plain http://localhost, and this factory runs
            // under the environment name "Test" rather than "Development". Left to the secure
            // default, antiforgery registers Cookie.SecurePolicy = Always and then throws
            // outright on the first non-SSL request ("the current request is not an SSL
            // request"), so every state-changing test in the tier fails with a 500.
            builder.UseSetting(ConfigKeys.Security.RequireSecureCookies, "false");

            // Configure app configuration FIRST - this must happen before services are registered
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var inMemorySettings = new Dictionary<string, string?>
                {
                    {"AzureTableStorage:RepositoryTableName", "PoRepoLineTrackerRepositoriesTest"},
                    {"AzureTableStorage:CommitLineCountTableName", "PoRepoLineTrackerCommitLineCountsTest"},
                    // A dedicated per-run directory, NOT Path.GetTempPath(). RemoveAllRepositories
                    // deletes this path recursively; pointed at the system temp root it wiped the
                    // test host's and Testcontainers' own scratch files, which raced other tests
                    // and made DeleteAllRepositories intermittently 500.
                    {"GitHub:LocalReposPath", TestRepoRoot},
                    // Provide mock OAuth credentials for testing
                    {"GitHub:ClientId", "test-client-id"},
                    {"GitHub:ClientSecret", "test-client-secret"},
                    {"GitHub:CallbackPath", "/signin-github"},
                    // Server-side PAT fallback. Without it the user-repositories endpoint short
                    // circuits with 400 before it ever reaches IGitHubService, which would make
                    // the caching tests assert nothing. Also exercises diagnostics masking.
                    {"GitHub:PAT", "test-pat-value-must-never-be-echoed"},
                    // Disable Key Vault to prevent DefaultAzureCredential from throwing
                    {"KeyVault:Uri", ""},
                    // Disable OpenTelemetry export
                    {"OpenTelemetry:OtlpEndpoint", ""},
                    {"EnableConsoleExporters", "false"},
                    // Disable App Insights
                    {"APPLICATIONINSIGHTS_CONNECTION_STRING", ""},
                    {"ApplicationInsights:ConnectionString", ""},
                };
                config.AddInMemoryCollection(inMemorySettings);

                // Prefer the Testcontainers-managed Azurite (AzuriteFixture). If that is not
                // available (Docker absent), fall back to probing a locally-running Azurite on
                // the default table port via IPv4. Either way, no manual container cleanup.
                _azuriteAvailable = false;
                _azuriteConnectionString = "";
                try
                {
                    if (!string.IsNullOrEmpty(AzuriteState.ConnectionString))
                    {
                        _azuriteConnectionString = AzuriteState.ConnectionString!;
                        _azuriteAvailable = true;
                    }
                    else
                    {
                        using (var tcp = new System.Net.Sockets.TcpClient())
                        {
                            var task = tcp.ConnectAsync("127.0.0.1", 10002);
                            _azuriteAvailable = task.Wait(1500) && tcp.Connected;
                        }
                        if (_azuriteAvailable)
                        {
                            _azuriteConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";
                        }
                    }
                    if (_azuriteAvailable)
                    {
                        var serviceClient = new Azure.Data.Tables.TableServiceClient(_azuriteConnectionString);
                        var testTables = new[] {
                            "PoRepoLineTrackerRepositoriesTest",
                            "PoRepoLineTrackerCommitLineCountsTest",
                            "PoRepoLineTrackerUserPreferencesTest",
                            "PoRepoLineTrackerUsersTest"
                        };
                        foreach (var tableName in testTables)
                        {
                            serviceClient.CreateTableIfNotExists(tableName);
                            foreach (var entity in serviceClient.GetTableClient(tableName).Query<Azure.Data.Tables.TableEntity>())
                                serviceClient.GetTableClient(tableName).DeleteEntity(entity.PartitionKey, entity.RowKey);
                        }
                    }
                }
                catch { _azuriteAvailable = false; }

                if (_azuriteAvailable)
                {
                    inMemorySettings["AzureTableStorage:ConnectionString"] = _azuriteConnectionString;
                    inMemorySettings["ConnectionStrings:tables"] = _azuriteConnectionString;
                }
                else
                {
                    inMemorySettings["AzureTableStorage:ConnectionString"] = "UseDevelopmentStorage=true";
                    inMemorySettings["ConnectionStrings:tables"] = "UseDevelopmentStorage=true";
                }

                config.AddInMemoryCollection(inMemorySettings);
            });

            builder.ConfigureServices(services =>
            {
                // Add test authentication that bypasses real auth
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", options => { });

                // Configure authorization to use the test scheme
                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new AuthorizationPolicyBuilder("Test")
                        .RequireAuthenticatedUser()
                        .Build();
                });

                // Mock external dependencies for testing
                var mockGitHubService = Substitute.For<IGitHubService>();
                var mockUserPreferencesService = Substitute.For<IUserPreferencesService>();
                var mockRepoDataService = Substitute.For<IRepositoryDataService>();

                // Default: return empty collections for all repository data queries
                mockRepoDataService.GetAllRepositoriesAsync(Arg.Any<UserId>())
                    .Returns(Task.FromResult(Enumerable.Empty<GitHubRepository>()));
                mockRepoDataService.GetRepositoryByIdAsync(Arg.Any<RepositoryId>())
                    .Returns(Task.FromResult<GitHubRepository?>(null));
                mockRepoDataService.GetCommitLineCountsByRepositoryIdAsync(Arg.Any<RepositoryId>())
                    .Returns(Task.FromResult(Enumerable.Empty<CommitLineCount>()));
                mockRepoDataService.CheckConnectionAsync()
                    .Returns(Task.CompletedTask);

                mockUserPreferencesService
                    .GetPreferencesAsync(Arg.Any<UserId>())
                    .Returns(callInfo =>
                    {
                        var userId = callInfo.Arg<UserId>();
                        return _storedPreferences with { UserId = userId };
                    });
                mockUserPreferencesService
                    .GetFileExtensionsAsync(Arg.Any<UserId>())
                    .Returns(callInfo => Task.FromResult(_storedPreferences.FileExtensions));
                mockUserPreferencesService
                    .SavePreferencesAsync(Arg.Any<UserPreferences>())
                    .Returns(callInfo =>
                    {
                        _storedPreferences = callInfo.Arg<UserPreferences>();
                        return Task.CompletedTask;
                    });

                // Replace services with mocks
                services.AddScoped(provider => mockGitHubService);

                if (_azuriteAvailable)
                {
                    // Azurite is running — replace the real TableServiceClient with one pointing to Azurite
                    var tableServiceClientDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TableServiceClient));
                    if (tableServiceClientDescriptor != null)
                    {
                        services.Remove(tableServiceClientDescriptor);
                    }
                    try
                    {
                        var azuriteClient = new TableServiceClient(_azuriteConnectionString);
                        services.AddSingleton(azuriteClient);
                    }
                    catch
                    {
                        // Azurite detected but connection failed — fall back to no-op
                        _azuriteAvailable = false;
                        services.AddSingleton(new TableServiceClient("AccountName=devstoreaccount1;AccountKey=dGVzdA==;DefaultEndpointsProtocol=https;BlobEndpoint=https://127.0.0.1:11000/devstoreaccount1;QueueEndpoint=https://127.0.0.1:11001/devstoreaccount1;TableEndpoint=https://127.0.0.1:11002/devstoreaccount1;"));
                    }
                }
                else
                {
                    // No Azurite — use mocks to prevent connection attempts
                    var tableServiceClientDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TableServiceClient));
                    if (tableServiceClientDescriptor != null)
                    {
                        services.Remove(tableServiceClientDescriptor);
                    }
                    services.AddSingleton(new TableServiceClient("AccountName=devstoreaccount1;AccountKey=dGVzdA==;DefaultEndpointsProtocol=https;BlobEndpoint=https://127.0.0.1:11000/devstoreaccount1;QueueEndpoint=https://127.0.0.1:11001/devstoreaccount1;TableEndpoint=https://127.0.0.1:11002/devstoreaccount1;"));

                    // Remove the health check that queries Table Storage (would fail without Azurite)
                    var allHealthChecks = services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck)).ToList();
                    foreach (var hc in allHealthChecks)
                    {
                        if (hc.ImplementationType?.Name == "AzureTableStorageHealthCheck" ||
                            hc.ImplementationType?.FullName?.Contains("AzureTableStorageHealthCheck") == true)
                        {
                            services.Remove(hc);
                        }
                    }

                    // Replace Table-backed services with mocks
                    services.AddScoped<IRepositoryDataService>(provider => mockRepoDataService);
                    services.AddScoped<IUserService>(provider =>
                    {
                        // The real service's Test-environment PAT fallback, which the caching
                        // tests need to reach IGitHubService at all.
                        var users = Substitute.For<IUserService>();
                        users.ResolveGitHubTokenAsync(Arg.Any<UserId>()).Returns("test-pat-value-must-never-be-echoed");
                        return users;
                    });
                    services.AddScoped<IUserPreferencesService>(provider => mockUserPreferencesService);
                }

                services.AddHostedService<NoOpHostedService>();
            });

            // No-op hosted service for tests
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<NoOpHostedService>();
            });

            builder.UseEnvironment("Test");
        }

        // No-op hosted service used to replace real background services during tests
        private class NoOpHostedService : Microsoft.Extensions.Hosting.BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
        }
    }
}
