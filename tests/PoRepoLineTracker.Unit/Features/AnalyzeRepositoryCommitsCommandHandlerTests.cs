using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PoRepoLineTracker.Unit;

public class AnalyzeRepositoryCommitsCommandHandlerTests
{
    private readonly IGitHubService _gitHubService = Substitute.For<IGitHubService>();
    private readonly IRepositoryDataService _dataService = Substitute.For<IRepositoryDataService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserPreferencesService _prefsService = Substitute.For<IUserPreferencesService>();
    private readonly IAnalysisProgressService _progressService = Substitute.For<IAnalysisProgressService>();
    private readonly ILogger<AnalyzeRepositoryCommitsCommandHandler> _logger = Substitute.For<ILogger<AnalyzeRepositoryCommitsCommandHandler>>();
    private readonly AnalyzeRepositoryCommitsCommandHandler _sut;

    public AnalyzeRepositoryCommitsCommandHandlerTests()
    {
        _sut = new AnalyzeRepositoryCommitsCommandHandler(
            _gitHubService, _dataService,
            _userService, _prefsService, _progressService, _logger);
    }

    /// <summary>
    /// What storage already holds for the repository.
    ///
    /// <para>The handler loads this ONCE and answers "have I seen this SHA" from it, rather than
    /// asking storage per commit — so an already-analysed commit is expressed here, by being in
    /// the set, and not by stubbing a per-SHA existence check. Stubbing that check was what these
    /// tests used to do, and it hid the fact that the check cost a round-trip per commit.</para>
    /// </summary>
    private void GivenStoredCommits(RepositoryId repositoryId, params CommitLineCount[] commits) =>
        _dataService.GetCommitLineCountsByRepositoryIdAsync(repositoryId).Returns(commits.ToList());

    private static CommitLineCount StoredCommit(string sha, int linesAdded = 0, int linesRemoved = 0) => new()
    {
        Id = Guid.NewGuid(),
        CommitSha = sha,
        CommitDate = DateTime.UtcNow,
        LinesAdded = linesAdded,
        LinesRemoved = linesRemoved
    };

    [Fact]
    public async Task Handle_RepoNotFoundOrNew_ClonesAppropriately()
    {
        var notFoundId = RepositoryId.New();
        _dataService.GetRepositoryByIdAsync(notFoundId).Returns((GitHubRepository?)null);

        await _sut.Handle(new AnalyzeRepositoryCommitsCommand(notFoundId), CancellationToken.None);
        await _gitHubService.DidNotReceive().CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        var newRepoId = RepositoryId.New();
        var newRepo = new GitHubRepository
        {
            Id = newRepoId,
            Owner = "testowner",
            Name = "testrepo",
            CloneUrl = "https://github.com/testowner/testrepo.git",
            LocalPath = "" // Empty triggers clone
        };
        _dataService.GetRepositoryByIdAsync(newRepoId).Returns(newRepo);
        _gitHubService.CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns("cloned-path");
        _gitHubService.GetCommitStatsAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<Action<int, int>?>()).Returns(Enumerable.Empty<CommitStatsDto>());

        await _sut.Handle(new AnalyzeRepositoryCommitsCommand(newRepoId), CancellationToken.None);
        // The URL is derived from Owner/Name, never taken from the stored (once caller-supplied) field.
        await _gitHubService.Received(1).CloneRepositoryAsync("https://github.com/testowner/testrepo.git", $"repo_{newRepoId}", Arg.Any<string?>());

        // A stored name that could redirect the clone is refused before git is ever invoked.
        var hostileId = RepositoryId.New();
        _dataService.GetRepositoryByIdAsync(hostileId).Returns(new GitHubRepository { Id = hostileId, Owner = "evil.example/x", Name = "repo" });
        var act = async () => await _sut.Handle(new AnalyzeRepositoryCommitsCommand(hostileId), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _gitHubService.Received(1).CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Handle_ExistingLocalPath_PullsWithTheResolvedToken()
    {
        var repoId = RepositoryId.New();
        var userId = UserId.New();
        var repo = new GitHubRepository
        {
            Id = repoId,
            Owner = "testowner",
            Name = "testrepo",
            CloneUrl = "https://github.com/testowner/testrepo.git",
            LocalPath = "/existing/path",
            UserId = userId
        };

        _dataService.GetRepositoryByIdAsync(repoId).Returns(repo);
        // Which token (the user's own, or the Dev/Test-only server PAT) is UserServiceTests' concern.
        _userService.ResolveGitHubTokenAsync(userId).Returns("ghp_test_token");
        _gitHubService.IsRepositoryValidAsync(Arg.Any<string>()).Returns(true);
        _gitHubService.PullRepositoryAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns("pulled");
        _gitHubService.GetCommitStatsAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<Action<int, int>?>()).Returns(Enumerable.Empty<CommitStatsDto>());
        _prefsService.GetPreferencesAsync(userId).Returns(new UserPreferences { UserId = userId, FileExtensions = [".cs", ".ts"] });

        await _sut.Handle(new AnalyzeRepositoryCommitsCommand(repoId), CancellationToken.None);
        await _gitHubService.Received(1).PullRepositoryAsync($"repo_{repoId}", "ghp_test_token");
    }

    [Fact]
    public async Task Handle_NetworkFailureOnClone_IsRetried_ButAnAuthFailureIsNot()
    {
        var sut = new AnalyzeRepositoryCommitsCommandHandler(
            _gitHubService, _dataService, _userService, _prefsService, _progressService, _logger)
        {
            NetworkRetryDelays = [TimeSpan.Zero, TimeSpan.Zero]
        };
        _gitHubService.GetCommitStatsAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<Action<int, int>?>()).Returns(Enumerable.Empty<CommitStatsDto>());

        // A dropped connection, then success: the run completes and records no error.
        var flakyId = RepositoryId.New();
        var flaky = new GitHubRepository { Id = flakyId, Owner = "testowner", Name = "flaky" };
        _dataService.GetRepositoryByIdAsync(flakyId).Returns(flaky);
        _gitHubService.CloneRepositoryAsync(Arg.Any<string>(), $"repo_{flakyId}", Arg.Any<string?>()).Returns(
            _ => Task.FromException<string>(new InvalidOperationException("git clone failed (exit 128): fatal: unable to access: Could not resolve host: github.com")),
            _ => Task.FromResult("cloned"));

        await sut.Handle(new AnalyzeRepositoryCommitsCommand(flakyId), CancellationToken.None);

        await _gitHubService.Received(2).CloneRepositoryAsync(Arg.Any<string>(), $"repo_{flakyId}", Arg.Any<string?>());
        flaky.LastAnalysisError.Should().BeNull();
        _progressService.Received(1).ReportComplete(flakyId);

        // A revoked token fails at once: waiting does not fix it, and the stored error says why.
        var revokedId = RepositoryId.New();
        _dataService.GetRepositoryByIdAsync(revokedId).Returns(new GitHubRepository { Id = revokedId, Owner = "testowner", Name = "revoked" });
        _gitHubService.CloneRepositoryAsync(Arg.Any<string>(), $"repo_{revokedId}", Arg.Any<string?>()).Returns(
            _ => Task.FromException<string>(new InvalidOperationException("git clone failed (exit 128): fatal: Authentication failed for 'https://github.com/testowner/revoked.git/'")));

        var act = () => sut.Handle(new AnalyzeRepositoryCommitsCommand(revokedId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _gitHubService.Received(1).CloneRepositoryAsync(Arg.Any<string>(), $"repo_{revokedId}", Arg.Any<string?>());
    }

    [Fact]
    public async Task Handle_ClearExistingData_DeletesCommitsAndResetsDate()
    {
        var repoId = RepositoryId.New();
        var repo = new GitHubRepository
        {
            Id = repoId,
            Owner = "o",
            Name = "n",
            CloneUrl = "url",
            LocalPath = "/path",
            LastAnalyzedCommitDate = DateTime.UtcNow
        };

        _dataService.GetRepositoryByIdAsync(repoId).Returns(repo);
        _gitHubService.IsRepositoryValidAsync(Arg.Any<string>()).Returns(true);
        _gitHubService.PullRepositoryAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns("ok");
        _gitHubService.GetCommitStatsAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<Action<int, int>?>()).Returns(Enumerable.Empty<CommitStatsDto>());

        await _sut.Handle(new AnalyzeRepositoryCommitsCommand(repoId, ClearExistingData: true), CancellationToken.None);

        await _dataService.Received(1).DeleteCommitLineCountsForRepositoryAsync(repoId);
        // The partition was just emptied — reading it back would be a wasted query.
        await _dataService.DidNotReceive().GetCommitLineCountsByRepositoryIdAsync(repoId);
        await _dataService.Received().UpdateRepositoryAsync(Arg.Is<GitHubRepository>(r => r.LastAnalyzedCommitDate == null));

        // ...but not when the repository cannot be fetched: the stored history must survive a
        // revoked token or a repository that is gone from GitHub.
        _dataService.ClearReceivedCalls();
        _gitHubService.PullRepositoryAsync(Arg.Any<string>(), Arg.Any<string?>()).ThrowsAsync(new InvalidOperationException("auth"));
        _gitHubService.CloneRepositoryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).ThrowsAsync(new InvalidOperationException("auth"));

        var act = async () => await _sut.Handle(new AnalyzeRepositoryCommitsCommand(repoId, ClearExistingData: true), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await _dataService.DidNotReceive().DeleteCommitLineCountsForRepositoryAsync(repoId);
        await _dataService.Received().UpdateRepositoryAsync(Arg.Is<GitHubRepository>(r => r.LastAnalysisError == "auth"));
    }

    [Fact]
    public async Task Handle_Commits_ProcessesNewSkipsExistingAndContinuesAfterFailures()
    {
        var repoId = RepositoryId.New();
        var repo = new GitHubRepository
        {
            Id = repoId,
            Owner = "o",
            Name = "n",
            CloneUrl = "url",
            LocalPath = "/path"
        };
        var commitStats = new List<CommitStatsDto>
        {
            new() { Sha = "new-sha", CommitDate = DateTime.UtcNow, LinesAdded = 50, LinesRemoved = 10 },
            new() { Sha = "existing-sha", CommitDate = DateTime.UtcNow, LinesAdded = 10, LinesRemoved = 5 },
            new() { Sha = "fail-sha", CommitDate = DateTime.UtcNow, LinesAdded = 0, LinesRemoved = 0 }
        };

        _dataService.GetRepositoryByIdAsync(repoId).Returns(repo);
        _gitHubService.IsRepositoryValidAsync(Arg.Any<string>()).Returns(true);
        _gitHubService.PullRepositoryAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns("ok");
        _gitHubService.GetCommitStatsAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<Action<int, int>?>()).Returns(commitStats);
        GivenStoredCommits(repoId, StoredCommit("existing-sha", linesAdded: 10, linesRemoved: 5));

        _gitHubService.CountLinesInCommitAsync(Arg.Any<string>(), "new-sha", Arg.Any<IEnumerable<string>>())
            .Returns(new Dictionary<string, int> { { ".cs", 100 }, { ".js", 50 } });
        _gitHubService.CountLinesInCommitAsync(Arg.Any<string>(), "fail-sha", Arg.Any<IEnumerable<string>>())
            .ThrowsAsync(new Exception("boom"));

        await _sut.Handle(new AnalyzeRepositoryCommitsCommand(repoId), CancellationToken.None);

        await _dataService.Received(1).AddCommitLineCountAsync(Arg.Is<CommitLineCount>(c =>
            c.CommitSha == "new-sha" && c.TotalLines == 150));
        await _gitHubService.DidNotReceive().CountLinesInCommitAsync(Arg.Any<string>(), "existing-sha", Arg.Any<IEnumerable<string>>());

        // One row write per run: the date and the attempt stamp go together.
        await _dataService.Received(1).UpdateRepositoryAsync(Arg.Is<GitHubRepository>(r =>
            r.LastAnalyzedCommitDate != null && r.LastAnalysisAttemptUtc != null && r.LastAnalysisError == null));
    }
}
