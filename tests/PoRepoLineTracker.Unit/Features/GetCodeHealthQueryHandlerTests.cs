using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoRepoLineTracker.Api.Features.CodeHealth;
using PoRepoLineTracker.Api.Storage;

namespace PoRepoLineTracker.Unit.Features;

/// <summary>
/// The wiring between the two analysers, which is where the split can silently go wrong.
///
/// <para>C# is parsed by <c>RoslynMetricsAnalyzer</c> and everything else is estimated by
/// <c>CodeMetricsAnalyzer</c>. Both produce plausible numbers on their own, so a routing mistake —
/// C# sent to the heuristic, or counted twice — does not throw and does not look wrong. These
/// assert which analyser saw what.</para>
/// </summary>
public class GetCodeHealthQueryHandlerTests
{
    private static readonly RepositoryId RepoId = RepositoryId.New();
    private static readonly UserId OwnerId = UserId.New();

    private readonly IRepositoryDataService _repositories = Substitute.For<IRepositoryDataService>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IUserPreferencesService _preferences = Substitute.For<IUserPreferencesService>();

    /// <summary>
    /// An empty memo on purpose: these tests are about which analyser sees which file, so every one
    /// must reach the compute path. A populated memo would short-circuit the handler and they would
    /// pass without exercising anything.
    /// </summary>
    private readonly ICodeHealthSnapshotStore _snapshots = Substitute.For<ICodeHealthSnapshotStore>();

    private GetCodeHealthQueryHandler CreateHandler(params SourceFile[] files)
    {
        _repositories.GetRepositoryByIdAsync(RepoId).Returns(new GitHubRepository
        {
            Id = RepoId,
            UserId = OwnerId,
            Owner = "acme",
            Name = "widgets"
        });

        _repositories.GetCommitLineCountsByRepositoryIdAsync(RepoId).Returns(new List<CommitLineCount>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RepositoryId = RepoId,
                CommitSha = "abcdef1234567890",
                CommitDate = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
                TotalLines = 100
            }
        });

        _preferences.GetFileExtensionsAsync(OwnerId).Returns(UserPreferences.DefaultFileExtensions);
        _gitHub.ResolveRepositoryPath(Arg.Any<string>()).Returns("/repo");
        _gitHub.IsRepositoryValidAsync("/repo").Returns(true);
        _gitHub.EnumerateSourceFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>())
            .Returns(files);

        _snapshots.GetByRepositoryAsync(Arg.Any<RepositoryId>())
            .Returns(new Dictionary<string, CodeHealthSnapshotEntity>());

        return new GetCodeHealthQueryHandler(
            _repositories, _gitHub, _preferences, _snapshots, NullLogger<GetCodeHealthQueryHandler>.Instance);
    }

    private const string CSharpSource = """
        namespace Acme;

        public class Widget
        {
            public int Score(int a, int b)
            {
                if (a > b) return a;
                return b;
            }
        }
        """;

    [Fact]
    public async Task Handle_LanguageRouting_RoutesCSharpToParserAndNonCSharpAndRazorToHeuristic()
    {
        // 1. C# only
        var csHandler = CreateHandler(new SourceFile("src/Widget.cs", ".cs", CSharpSource));
        var csReport = await csHandler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        csReport.Should().NotBeNull();
        csReport!.Metrics.Should().NotBeNull();
        csReport.Metrics!.MembersMeasured.Should().Be(1);
        csReport.Metrics.MaintainabilityIndex.Should().NotBeNull();
        csReport.Metrics.CyclomaticComplexity.Should().Be(2);
        csReport.FilesAnalyzed.Should().Be(0);
        csReport.HasData.Should().BeTrue();

        // 2. Non-C# only
        var pyHandler = CreateHandler(new SourceFile("app/main.py", ".py",
            "def add(a, b):\n    if a > b:\n        return a\n    return b\n"));
        var pyReport = await pyHandler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        pyReport!.Metrics.Should().BeNull();
        pyReport.FilesAnalyzed.Should().Be(1);
        pyReport.HasData.Should().BeTrue();

        // 3. Mixed
        var mixedHandler = CreateHandler(
            new SourceFile("src/Widget.cs", ".cs", CSharpSource),
            new SourceFile("app/main.py", ".py", "def add(a, b):\n    return a + b\n"),
            new SourceFile("web/app.ts", ".ts", "export const add = (a: number, b: number) => a + b;\n"));
        var mixedReport = await mixedHandler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        mixedReport!.Metrics!.FilesAnalyzed.Should().Be(1);
        mixedReport.FilesAnalyzed.Should().Be(2);

        // 4. Razor files route to heuristic
        var razorHandler = CreateHandler(new SourceFile("Pages/Index.razor", ".razor",
            "@page \"/\"\n<h1>Hello</h1>\n@code { int X => 1; }\n"));
        var razorReport = await razorHandler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        razorReport!.Metrics.Should().BeNull();
        razorReport.FilesAnalyzed.Should().Be(1);
    }

    [Fact]
    public async Task Handle_MemoStore_ServesCachedOrFallsBackOnCorruptData()
    {
        var handler = CreateHandler(new SourceFile("src/Widget.cs", ".cs", CSharpSource));

        // First pass computes and writes
        var first = await handler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        var stored = _snapshots.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICodeHealthSnapshotStore.SaveAsync))
            .Select(c => (CodeHealthSnapshotEntity)c.GetArguments()[0]!)
            .Single();

        stored.RowKey.Should().Be("abcdef1234567890");
        stored.ReportJson.Should().NotBeEmpty();

        // Cached read
        _gitHub.ClearReceivedCalls();
        _snapshots.GetByRepositoryAsync(Arg.Any<RepositoryId>())
            .Returns(new Dictionary<string, CodeHealthSnapshotEntity> { [stored.RowKey] = stored });

        var second = await handler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        second!.OverallScore.Should().Be(first!.OverallScore);
        _gitHub.DidNotReceive().EnumerateSourceFiles(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>());

        // Corrupt row fallback
        _snapshots.GetByRepositoryAsync(Arg.Any<RepositoryId>())
            .Returns(new Dictionary<string, CodeHealthSnapshotEntity>
            {
                ["abcdef1234567890"] = new() { RowKey = "abcdef1234567890", ReportJson = "{ not json" }
            });

        var fallbackReport = await handler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);
        fallbackReport.Should().NotBeNull();
        fallbackReport!.Metrics!.MembersMeasured.Should().Be(1);
    }

    [Fact]
    public async Task Handle_NoAnalysedCommits_ReturnsNull()
    {
        _repositories.GetRepositoryByIdAsync(RepoId).Returns(new GitHubRepository
        {
            Id = RepoId,
            UserId = OwnerId,
            Owner = "acme",
            Name = "widgets"
        });
        _repositories.GetCommitLineCountsByRepositoryIdAsync(RepoId).Returns([]);

        _snapshots.GetByRepositoryAsync(Arg.Any<RepositoryId>())
            .Returns(new Dictionary<string, CodeHealthSnapshotEntity>());

        var handler = new GetCodeHealthQueryHandler(
            _repositories, _gitHub, _preferences, _snapshots, NullLogger<GetCodeHealthQueryHandler>.Instance);

        var report = await handler.Handle(new GetCodeHealthQuery(RepoId), CancellationToken.None);

        report.Should().BeNull();
    }
}
