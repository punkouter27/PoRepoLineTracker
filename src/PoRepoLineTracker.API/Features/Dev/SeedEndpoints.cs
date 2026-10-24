using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace PoRepoLineTracker.API.Features;

/// <summary>
/// Synthetic repository history for the automated UI tier, Development only.
///
/// <para><b>Why this exists.</b> Twelve assertions in ChartAndShellUiTests — axis titles, tick
/// labels, distinct date formatting, the dark-theme chart surface — each opened with
/// <c>Skip.If(no chart rendered)</c>. Charts only render for a signed-in user with analysed
/// repositories, and the suite's fake user has none, so the whole group skipped itself on every
/// run. A suite that reports "45 passed, 12 skipped" reads as healthy while covering no chart at
/// all.</para>
///
/// <para><b>Why synthetic rather than a real analysis.</b> The genuine path clones a repository
/// from GitHub and walks its commits: minutes of wall-clock, a network dependency, and history
/// that changes under the test. The chart assertions need specific SHAPES — a handful of days to
/// catch duplicate axis labels, a full year to catch a crowded one — which real history cannot be
/// asked for. Writing commit rows directly gives both, deterministically, in well under a second.
/// It exercises the same storage, the same queries and the same rendering; only the commit walk
/// is skipped, and that has its own tests.</para>
///
/// <para><b>Guardrail.</b> Mapped only under <c>IsDevelopment()</c>, alongside the other dev
/// routes. This writes arbitrary data to a caller's account, so like FakeAuthHandler it must never
/// exist in a deployed environment.</para>
/// </summary>
internal static class SeedEndpoints
{
    /// <summary>
    /// Deterministic per (repository, day) so a re-seeded chart is byte-identical. A random walk
    /// would make a failing tick-label assertion irreproducible.
    /// </summary>
    private const int BaseLines = 1_000;
    private const int LinesPerDay = 37;

    internal static void MapSeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var seed = endpoints.MapGroup("/api/dev/seed")
            .WithTags("Dev")
            .RequireAuthorization();

        seed.MapPost("/repository", async (
            [FromBody] SeedRepositoryRequest request,
            HttpContext ctx,
            IRepositoryDataService repoDataService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var days = Math.Clamp(request.Days, 1, 400);
            var owner = string.IsNullOrWhiteSpace(request.Owner) ? "e2e" : request.Owner;
            var name = string.IsNullOrWhiteSpace(request.Name) ? "seeded" : request.Name;

            // Idempotent: re-running the suite must not stack duplicate repositories, and the
            // existing rows must go or the day-count assertions would see two overlapping series.
            var existing = await repoDataService.GetRepositoryByOwnerAndNameAsync(owner, name, userId);
            if (existing is not null)
            {
                await repoDataService.DeleteCommitLineCountsForRepositoryAsync(existing.Id);
                await repoDataService.DeleteRepositoryAsync(existing.Id);
            }

            var repository = new GitHubRepository
            {
                Id = RepositoryId.New(),
                UserId = userId,
                Owner = owner,
                Name = name,
                CloneUrl = GitHubRepository.CloneUrlFor(owner, name)
            };
            await repoDataService.AddRepositoryAsync(repository);

            // Midday UTC, not midnight: a commit exactly on a date boundary lands on either side
            // depending on the reader's rounding, which would make the day-count assertions flaky.
            var today = DateTime.UtcNow.Date.AddHours(12);
            var lastCommitDate = today;

            for (var dayOffset = days - 1; dayOffset >= 0; dayOffset--)
            {
                var commitDate = today.AddDays(-dayOffset);
                var elapsed = days - 1 - dayOffset;
                var totalLines = BaseLines + elapsed * LinesPerDay;

                await repoDataService.AddCommitLineCountAsync(new CommitLineCount
                {
                    Id = Guid.NewGuid(),
                    RepositoryId = repository.Id,
                    CommitSha = $"seed{elapsed:D6}",
                    CommitDate = commitDate,
                    TotalLines = totalLines,
                    LinesAdded = LinesPerDay,
                    LinesRemoved = 0,
                    // Two extensions so the composition chart and the language mix have something
                    // to split, and the donut renders more than a single arc.
                    LinesByFileType = new Dictionary<string, int>
                    {
                        [".cs"] = (int)(totalLines * 0.7),
                        [".razor"] = totalLines - (int)(totalLines * 0.7)
                    },
                    // Two authors so ContributorChart draws more than one series.
                    AuthorName = elapsed % 2 == 0 ? "Ada Lovelace" : "Grace Hopper",
                    AuthorEmail = elapsed % 2 == 0 ? "ada@example.com" : "grace@example.com"
                });

                lastCommitDate = commitDate;
            }

            // Without this the grid shows "Pending" and the page keeps a poll alive against a job
            // that does not exist.
            repository.LastAnalyzedCommitDate = lastCommitDate;
            await repoDataService.UpdateRepositoryAsync(repository);

            Log.Information("DEV: seeded {Owner}/{Name} with {Days} days of history for user {UserId}",
                owner, name, days, userId);

            return Results.Ok(new SeedRepositoryResponse
            {
                RepositoryId = repository.Id,
                Owner = owner,
                Name = name,
                Days = days
            });
        })
        .WithName("DevSeedRepository")
        .WithSummary("Writes synthetic commit history so the UI tier has charts to assert on (Development only)");
    }
}

/// <summary>How much synthetic history to write. Days is clamped to 1–400 by the endpoint.</summary>
public sealed class SeedRepositoryRequest
{
    public string Owner { get; set; } = "e2e";
    public string Name { get; set; } = "seeded";
    public int Days { get; set; } = 120;
}

public sealed class SeedRepositoryResponse
{
    public RepositoryId RepositoryId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Days { get; set; }
}
