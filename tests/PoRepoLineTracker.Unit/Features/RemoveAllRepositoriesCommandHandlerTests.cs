using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace PoRepoLineTracker.Unit;

/// <summary>
/// The most destructive handler in the codebase: it drops every repository row for a user AND
/// deletes their clones from disk.
///
/// <para>The property that matters most is the blast radius. This handler used to delete the
/// configured clone ROOT recursively — a directory every user of the deployment shares — so one
/// user's "remove all" destroyed everyone's clones. It must touch only the caller's own
/// repositories. And the cleanup must stay best-effort: a locked Git file is routine, and letting
/// it propagate would fail a request whose storage work has already succeeded.</para>
/// </summary>
public sealed class RemoveAllRepositoriesCommandHandlerTests
{
    private readonly IRepositoryDataService _dataService = Substitute.For<IRepositoryDataService>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly ICodeHealthSnapshotStore _snapshots = Substitute.For<ICodeHealthSnapshotStore>();
    private readonly UserId _userId = UserId.New();
    private readonly GitHubRepository _first = new() { Id = RepositoryId.New(), Owner = "acme", Name = "api" };
    private readonly GitHubRepository _second = new() { Id = RepositoryId.New(), Owner = "acme", Name = "web" };

    public RemoveAllRepositoriesCommandHandlerTests()
    {
        _dataService.RemoveAllRepositoriesAsync(_userId).Returns([_first, _second]);
    }

    private Task WhenRemovingAll() =>
        new RemoveAllRepositoriesCommandHandler(
                _dataService, _gitHub, _snapshots,
                new RepositoryWebhooks(Substitute.For<IHttpClientFactory>(), Substitute.For<IUserService>(),
                    Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>(), Substitute.For<ILogger<RepositoryWebhooks>>()),
                Substitute.For<ILogger<RemoveAllRepositoriesCommandHandler>>())
            .Handle(new RemoveAllRepositoriesCommand(_userId), CancellationToken.None);

    [Fact]
    public async Task RemovesTheUsersRows_AndOnlyTheirOwnClonesAndSnapshots()
    {
        await WhenRemovingAll();

        await _dataService.Received(1).RemoveAllRepositoriesAsync(_userId);
        await _dataService.DidNotReceive().RemoveAllRepositoriesAsync(Arg.Is<UserId>(id => id != _userId));

        // Exactly the two clone directories derived from this user's repository ids — nothing
        // broader, and in particular never the shared root.
        await _gitHub.Received(1).DeleteLocalRepositoryAsync($"repo_{_first.Id}");
        await _gitHub.Received(1).DeleteLocalRepositoryAsync($"repo_{_second.Id}");
        await _gitHub.Received(2).DeleteLocalRepositoryAsync(Arg.Any<string>());
        await _snapshots.Received(1).DeleteForRepositoryAsync(_first.Id);
        await _snapshots.Received(1).DeleteForRepositoryAsync(_second.Id);
    }

    [Fact]
    public async Task WhenStorageFails_TheFailurePropagates_AndNothingOnDiskIsTouched()
    {
        _dataService.RemoveAllRepositoriesAsync(_userId).ThrowsAsync(new InvalidOperationException("Table unavailable"));

        var act = WhenRemovingAll;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Table unavailable");
        await _gitHub.DidNotReceive().DeleteLocalRepositoryAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task LocalCleanupFailure_IsSwallowed_AndTheRemainingRepositoriesAreStillCleaned()
    {
        _gitHub.DeleteLocalRepositoryAsync($"repo_{_first.Id}").ThrowsAsync(new IOException("pack file locked"));

        var act = WhenRemovingAll;

        await act.Should().NotThrowAsync();
        await _gitHub.Received(1).DeleteLocalRepositoryAsync($"repo_{_second.Id}");
    }
}
