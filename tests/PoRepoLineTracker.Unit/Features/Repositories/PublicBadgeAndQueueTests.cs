using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace PoRepoLineTracker.Unit.Features.Repositories;

public class PublicBadgeAndQueueTests
{
    [Theory]
    [InlineData(999, "999")]
    [InlineData(48_213, "48.2k")]
    [InlineData(1_250_000, "1.3M")]
    public void FormatLines_abbreviates_like_shields(int lines, string expected) =>
        PublicBadgeEndpoints.FormatLines(lines).Should().Be(expected);

    [Fact]
    public void Badge_encodes_its_text()
    {
        var svg = PublicBadgeEndpoints.Badge("a<b", "\"x\"&", "#4c1");

        svg.Should().Contain("a&lt;b").And.Contain("&quot;x&quot;&amp;").And.NotContain("a<b");
        svg.Should().StartWith("<svg xmlns=\"http://www.w3.org/2000/svg\"");
    }

    [Fact]
    public async Task Queue_refuses_a_second_job_for_a_repository_already_queued()
    {
        var queue = new AnalysisQueue(Substitute.For<IServiceScopeFactory>(), NullLogger<AnalysisQueue>.Instance);
        var data = Substitute.For<IRepositoryDataService>();
        var repository = new GitHubRepository { Id = RepositoryId.New(), Owner = "o", Name = "r" };

        (await queue.EnqueueReanalysisAsync(repository, data)).Should().BeTrue();
        queue.IsPending(repository.Id).Should().BeTrue();

        // The stamp a restart resumes from was written before the job was queued.
        await data.Received(1).UpdateRepositoryAsync(Arg.Is<GitHubRepository>(r => r.AnalysisQueuedUtc != null && r.QueuedFullReanalysis));

        (await queue.EnqueueReanalysisAsync(repository, data)).Should().BeFalse();
        queue.Enqueue(repository.Id).Should().BeFalse();
        queue.Enqueue(RepositoryId.New()).Should().BeTrue();
    }
}
