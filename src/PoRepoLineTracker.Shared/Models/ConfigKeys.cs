namespace PoRepoLineTracker.Shared.Models;

/// <summary>
/// Every configuration key the application reads, in one place (zero magic strings).
///
/// Before this existed the same key was spelled out at up to four call sites, so a rename meant
/// finding all of them and a typo meant a silently null value rather than a build error. Grouped
/// by configuration section to mirror appsettings.json, Key Vault (<c>PoRepoLineTracker--</c>
/// prefix), and App Service application settings.
/// </summary>
public static class ConfigKeys
{
    /// <summary>Azure Key Vault.</summary>
    public static class KeyVault
    {
        public const string Uri = "KeyVault:Uri";
    }

    /// <summary>Azure Table Storage connection and table names.</summary>
    public static class AzureTableStorage
    {
        public const string ConnectionString = "AzureTableStorage:ConnectionString";
        public const string ServiceUrl = "AzureTableStorage:ServiceUrl";
        public const string RepositoryTableName = "AzureTableStorage:RepositoryTableName";
        public const string CommitLineCountTableName = "AzureTableStorage:CommitLineCountTableName";
        public const string UserTableName = "AzureTableStorage:UserTableName";
        public const string UserPreferencesTableName = "AzureTableStorage:UserPreferencesTableName";

        /// <summary>Per-commit code-health memo. See CodeHealthSnapshotEntity for why it is keyed on the SHA.</summary>
        public const string CodeHealthSnapshotTableName = "AzureTableStorage:CodeHealthSnapshotTableName";

        /// <summary>Aspire/.NET-style connection string, used as a fallback for the above.</summary>
        public const string TablesConnectionString = "ConnectionStrings:tables";
    }

    /// <summary>GitHub OAuth credentials and API access.</summary>
    public static class GitHub
    {
        public const string ClientId = "GitHub:ClientId";
        public const string ClientSecret = "GitHub:ClientSecret";
        public const string CallbackPath = "GitHub:CallbackPath";

        /// <summary>
        /// Local-dev-only OAuth App, registered with a localhost callback. Preferred over
        /// <see cref="ClientId"/>/<see cref="ClientSecret"/> in Development so a developer's
        /// machine never needs the production app's credentials.
        /// </summary>
        public const string DevClientId = "GitHub:Dev:ClientId";
        public const string DevClientSecret = "GitHub:Dev:ClientSecret";

        /// <summary>Server-side Personal Access Token, used when the caller has no GitHub token.</summary>
        public const string Pat = "GitHub:PAT";
        public const string LocalReposPath = "GitHub:LocalReposPath";
    }

    // The Microsoft / Entra ID key group was removed with the Microsoft OAuth provider — a
    // recorded deviation, see SPEC.md §9. GitHub is the only provider.

    /// <summary>Telemetry export targets.</summary>
    public static class Telemetry
    {
        public const string AppInsightsConnectionString = "APPLICATIONINSIGHTS_CONNECTION_STRING";
        public const string AppInsightsConnectionStringSection = "ApplicationInsights:ConnectionString";
        public const string AppInsightsInstrumentationKey = "APPINSIGHTS_INSTRUMENTATIONKEY";
        public const string AppInsightsInstrumentationKeySection = "ApplicationInsights:InstrumentationKey";
        public const string OtlpEndpoint = "OpenTelemetry:OtlpEndpoint";
        public const string EnableConsoleExporters = "EnableConsoleExporters";
        public const string EnableAspNetCoreMeters = "ApplicationInsights:EnableAspNetCoreMeters";
    }

    /// <summary>Cookie and transport hardening.</summary>
    public static class Security
    {
        /// <summary>
        /// Whether this host is reached over HTTPS, and so whether cookies may carry <c>Secure</c>
        /// and the <c>__Host-</c> prefix. Defaults to true everywhere except Development.
        ///
        /// <para>Set it to <c>false</c> for any host that genuinely serves plain HTTP — the http
        /// launch profile, or a test server. A <c>__Host-</c> cookie without <c>Secure</c> is
        /// rejected outright, and one WITH <c>Secure</c> is never returned over http, so getting
        /// this wrong makes every state-changing request fail antiforgery while reads look fine.</para>
        /// </summary>
        public const string RequireSecureCookies = "Security:RequireSecureCookies";
    }
}
