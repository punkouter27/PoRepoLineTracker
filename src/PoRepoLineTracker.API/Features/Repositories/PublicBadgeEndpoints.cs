using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace PoRepoLineTracker.API.Features.Repositories;

/// <summary>
/// The app's only anonymous data: README badges and a share page, for repositories whose owner
/// switched them on (<see cref="GitHubRepository.IsPublic"/>).
///
/// <para><b>What is exposed.</b> Owner/name, the current line total, the latest memoised
/// code-health grade and the newest commit's date. Nothing else is read, and a repository that
/// is missing or not public gets the same 404 either way, so the routes cannot be used to test
/// whether an id exists. The id is a GUID nobody can guess; it is shared by the owner.</para>
///
/// <para><b>Outside /api on purpose,</b> like /hubs: the routes are anonymous GETs for other
/// people's browsers and GitHub's image proxy. They must pass the production sign-in redirect and
/// the service worker's shell cache; both list /badge and /share for that reason.</para>
///
/// <para><b>Lives in the Repositories slice</b> because turning a repository private must evict
/// its cached card at once (the visibility endpoint here calls <see cref="EvictAsync"/>). From
/// another slice it would have had to share a cache key string across the slice boundary.</para>
/// </summary>
public static class PublicBadgeEndpoints
{
    private static string CacheKey(RepositoryId repositoryId) => $"public-card:{repositoryId}";

    /// <summary>Drops the cached card, so a repository made private stops being served immediately.</summary>
    internal static ValueTask EvictAsync(HybridCache cache, RepositoryId repositoryId) =>
        cache.RemoveAsync(CacheKey(repositoryId));

    /// <summary>What both routes render. Null fields mean "not measured yet".</summary>
    internal sealed record PublicCard(string Owner, string Name, int? TotalLines, int? HealthScore, string? HealthGrade, DateTime? LastCommitUtc);

    public static void MapPublicBadgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public)
            .WithTags("Public");

        group.MapGet("/badge/{repositoryId:guid}/{kind}.svg", async (Guid repositoryId, string kind, HttpContext ctx, IServiceProvider services) =>
        {
            var card = await LoadAsync(new RepositoryId(repositoryId), services);
            var (label, value, color) = kind switch
            {
                "lines" => ("lines of code", card?.TotalLines is { } lines ? FormatLines(lines) : "unknown", "#007ec6"),
                "health" => ("code health", card?.HealthScore is { } score ? $"{score} {card.HealthGrade}" : "unknown", GradeColor(card?.HealthGrade)),
                _ => (null, null, null)
            };
            if (label is null) return Results.NotFound();

            if (card is null)
            {
                // Still an image, so a README showing a badge whose repository was made private
                // renders a grey "unavailable" instead of a broken-image icon. Not cached, so
                // making it public again shows at the next fetch.
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text(Badge(label, "unavailable", "#9f9f9f"), "image/svg+xml", Encoding.UTF8, StatusCodes.Status404NotFound);
            }

            // GitHub's camo proxy honours this; ten minutes keeps a README fresh without
            // re-reading storage on every page view.
            ctx.Response.Headers.CacheControl = "public, max-age=600";
            return Results.Text(Badge(label, value!, color!), "image/svg+xml", Encoding.UTF8);
        })
        .WithName("GetRepositoryBadge");

        group.MapGet("/share/{repositoryId:guid}", async (Guid repositoryId, HttpContext ctx, IServiceProvider services) =>
        {
            var card = await LoadAsync(new RepositoryId(repositoryId), services);
            if (card is null) return Results.NotFound();

            ctx.Response.Headers.CacheControl = "public, max-age=300";
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Content(SharePage(card, origin, repositoryId), "text/html", Encoding.UTF8);
        })
        .WithName("GetRepositorySharePage");
    }

    /// <summary>
    /// ponytail: a miss reads the repository by id, which is a cross-partition scan of the
    /// (portfolio-sized) repository table. The per-address rate limit bounds what an anonymous
    /// caller can make that cost; key the row by id if the table ever grows past hundreds.
    /// </summary>
    private static async Task<PublicCard?> LoadAsync(RepositoryId repositoryId, IServiceProvider services)
    {
        var cache = services.GetRequiredService<HybridCache>();
        return await cache.GetOrCreateAsync(CacheKey(repositoryId), async _ =>
        {
            var data = services.GetRequiredService<IRepositoryDataService>();
            var repository = await data.GetRepositoryByIdAsync(repositoryId);
            if (repository is not { IsPublic: true }) return null;

            var commits = (await data.GetCommitLineCountsByRepositoryIdAsync(repositoryId)).ToList();
            var newest = commits.MaxBy(c => c.CommitDate);

            // The memo only: computing a health report walks the whole clone, which is not
            // something an anonymous request gets to trigger. The owner's own Code Health tab
            // fills the memo; until then the badge says "unknown".
            int? score = null;
            string? grade = null;
            if (newest is not null)
            {
                var memo = await services.GetRequiredService<ICodeHealthSnapshotStore>().GetByRepositoryAsync(repositoryId);
                if (memo.TryGetValue(newest.CommitSha, out var snapshot)
                    && CodeHealthSnapshotSerializer.TryRead(snapshot.ReportJson, out var report)
                    && report is { HasData: true })
                {
                    score = report.OverallScore;
                    grade = report.OverallGrade;
                }
            }

            return new PublicCard(repository.Owner, repository.Name,
                newest is null ? null : RepositoryTotals.LatestTotalLines(commits),
                score, grade, newest?.CommitDate);
        },
        new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(10) });
    }

    internal static string FormatLines(int lines) => lines switch
    {
        >= 1_000_000 => (lines / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (lines / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => lines.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>The shields.io grade palette, so the badge sits naturally beside the ones it will be next to.</summary>
    private static string GradeColor(string? grade) => grade switch
    {
        "A" => "#4c1",
        "B" => "#97ca00",
        "C" => "#dfb317",
        "D" => "#fe7d37",
        "F" => "#e05d44",
        _ => "#9f9f9f"
    };

    /// <summary>
    /// A flat two-part badge in the shields.io layout. Widths are estimated at 7px per character
    /// of Verdana 11px: no font metrics are available server-side, and an estimate a few pixels
    /// wide only adds padding.
    /// </summary>
    internal static string Badge(string label, string value, string color)
    {
        var labelText = WebUtility.HtmlEncode(label);
        var valueText = WebUtility.HtmlEncode(value);
        var labelWidth = label.Length * 7 + 12;
        var valueWidth = value.Length * 7 + 12;
        var width = labelWidth + valueWidth;
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="20" role="img" aria-label="{labelText}: {valueText}">
            <title>{labelText}: {valueText}</title>
            <linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient>
            <clipPath id="r"><rect width="{width}" height="20" rx="3" fill="#fff"/></clipPath>
            <g clip-path="url(#r)"><rect width="{labelWidth}" height="20" fill="#555"/><rect x="{labelWidth}" width="{valueWidth}" height="20" fill="{color}"/><rect width="{width}" height="20" fill="url(#s)"/></g>
            <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
            <text x="{labelWidth / 2}" y="15" fill="#010101" fill-opacity=".3">{labelText}</text><text x="{labelWidth / 2}" y="14">{labelText}</text>
            <text x="{labelWidth + valueWidth / 2}" y="15" fill="#010101" fill-opacity=".3">{valueText}</text><text x="{labelWidth + valueWidth / 2}" y="14">{valueText}</text>
            </g>
            </svg>
            """;
    }

    /// <summary>
    /// A self-contained page: no Blazor boot, no app script, so it is fast for a visitor and works
    /// in link-preview crawlers. Every value is HTML-encoded; owner and name are validated GitHub
    /// names already, but this page is the one place they reach an anonymous reader.
    /// </summary>
    private static string SharePage(PublicCard card, string origin, Guid repositoryId)
    {
        var fullName = WebUtility.HtmlEncode($"{card.Owner}/{card.Name}");
        var repoUrl = WebUtility.HtmlEncode($"https://github.com/{card.Owner}/{card.Name}");
        var lines = card.TotalLines?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        var health = card.HealthScore is { } score ? $"{score} ({WebUtility.HtmlEncode(card.HealthGrade)})" : "—";
        var lastCommit = card.LastCommitUtc?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
        var summary = WebUtility.HtmlEncode($"{lines} lines of code" + (card.HealthScore is { } s ? $" · code health {s} ({card.HealthGrade})" : ""));
        var pageUrl = WebUtility.HtmlEncode($"{origin}/share/{repositoryId}");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta name="color-scheme" content="light dark" />
            <title>{{fullName}} · PoRepoLineTracker</title>
            <meta name="description" content="{{summary}}" />
            <meta property="og:type" content="website" />
            <meta property="og:title" content="{{fullName}}" />
            <meta property="og:description" content="{{summary}}" />
            <meta property="og:url" content="{{pageUrl}}" />
            <meta name="twitter:card" content="summary" />
            <link rel="icon" type="image/svg+xml" href="/favicon.svg" />
            <style>
              :root { --bg: #f6f7f9; --card: #fff; --text: #1b1f24; --muted: #59636e; --accent: #4f46e5; --line: #e2e5e9; }
              @media (prefers-color-scheme: dark) { :root { --bg: #0f1115; --card: #181b21; --text: #e6e8eb; --muted: #9aa3ad; --accent: #8b85ff; --line: #2a2f37; } }
              * { box-sizing: border-box; }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 16px; background: var(--bg); color: var(--text);
                     font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
              main { width: 100%; max-width: 32rem; background: var(--card); border: 1px solid var(--line); border-radius: 16px; padding: 28px; }
              h1 { margin: 0 0 4px; font-size: 1.4rem; overflow-wrap: anywhere; }
              .sub { margin: 0 0 24px; color: var(--muted); font-size: .9rem; }
              dl { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 0 0 24px; }
              dt { color: var(--muted); font-size: .75rem; text-transform: uppercase; letter-spacing: .04em; }
              dd { margin: 2px 0 0; font-size: 1.25rem; font-weight: 700; font-variant-numeric: tabular-nums; }
              a { color: var(--accent); }
              footer { margin-top: 20px; color: var(--muted); font-size: .8rem; }
              @media (max-width: 420px) { dl { grid-template-columns: 1fr 1fr; } }
            </style>
            </head>
            <body>
            <main>
              <h1>{{fullName}}</h1>
              <p class="sub">Repository stats, shared by its owner</p>
              <dl>
                <div><dt>Lines of code</dt><dd>{{lines}}</dd></div>
                <div><dt>Code health</dt><dd>{{health}}</dd></div>
                <div><dt>Last commit</dt><dd>{{lastCommit}}</dd></div>
              </dl>
              <a href="{{repoUrl}}" rel="noopener noreferrer">View on GitHub →</a>
              <footer>Lines exclude blanks, comments, generated and vendored files. Code health is an estimate from simple proxies, not a review. Tracked with <a href="/">PoRepoLineTracker</a>.</footer>
            </main>
            </body>
            </html>
            """;
    }
}
