using PoRepoLineTracker.Shared.Domain;

namespace PoRepoLineTracker.Client.Models;

/// <summary>
/// Every route the app has, declared once.
/// <para>
/// The routes used to be string literals sprinkled through eleven files. The worst of them was
/// <c>/repositories/{id}?tab=health</c> in the portfolio health list: it had to match the tab
/// array in <c>RepositoryDetail</c> by hand, and renaming a tab broke the link with no error
/// anywhere. Declaring the routes here means a link and the page it points at cannot drift apart.
/// </para>
/// </summary>
public static class AppRoutes
{
    // ─── Pages ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The repository list — the app's front door, and the only page a new user needs.</summary>
    public const string Repositories = "/repositories";

    /// <summary>One repository's detail page. Use <see cref="ForRepository"/> for a real id.</summary>
    public const string RepositoryDetail = "/repositories/{id}";

    /// <summary>Portfolio-wide figures over the whole history.</summary>
    public const string Insights = "/insights";

    /// <summary>Maintainability across every repository.</summary>
    public const string CodeHealth = "/code-health";

    /// <summary>The yearly recap. Also answers <c>/year-in-review/{year}</c>.</summary>
    public const string YearInReview = "/year-in-review";

    /// <summary>File counting, GitHub access and account controls.</summary>
    public const string Settings = "/settings";

    /// <summary>An operator's page: queue state, health, logs. Deliberately not in the sidebar.</summary>
    public const string Diagnostics = "/diagnostics";

    /// <summary>Where the sign-in link and the auth middleware both send an anonymous visitor.</summary>
    public const string Login = "/login";

    // ─── Repository detail ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The repository detail page's tabs, in render order. The page validates <c>?tab=</c> against
    /// this list, so a link that names a tab and the page that renders it agree by construction.
    /// </summary>
    public static readonly string[] RepositoryTabs = ["activity", "composition", "health", "hotspots", "share"];

    /// <summary>The detail page of one repository.</summary>
    public static string ForRepository(RepositoryId id) => $"/repositories/{id.Value}";

    /// <summary>The detail page of one repository, on one of its tabs.</summary>
    public static string ForRepositoryTab(RepositoryId id, string tab) => $"/repositories/{id.Value}?tab={tab}";
}
