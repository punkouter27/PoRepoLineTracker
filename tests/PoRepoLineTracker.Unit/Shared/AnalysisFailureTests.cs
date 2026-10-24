using FluentAssertions;

namespace PoRepoLineTracker.Unit.Shared;

public class AnalysisFailureTests
{
    [Theory]
    [InlineData("git clone failed (exit 128): remote: Invalid username or token. fatal: Authentication failed for 'https://github.com/o/r.git/'")]
    [InlineData("git fetch failed (exit 128): fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("git clone failed (exit 128): fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 403")]
    public void Auth_failures_are_auth_and_never_retried(string error)
    {
        AnalysisFailure.IsAuth(error).Should().BeTrue();
        AnalysisFailure.IsTransient(error).Should().BeFalse();
    }

    [Theory]
    [InlineData("git clone failed (exit 128): fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com")]
    [InlineData("git fetch failed (exit 128): error: RPC failed; curl 56 Recv failure: Connection reset by peer")]
    [InlineData("git clone failed (exit 128): fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 502")]
    public void Network_failures_are_transient(string error)
    {
        AnalysisFailure.IsTransient(error).Should().BeTrue();
        AnalysisFailure.IsAuth(error).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("git clone failed (exit 128): remote: Repository not found.")]
    [InlineData("git clone did not finish within 30 minutes.")]
    public void Anything_else_is_neither(string? error)
    {
        AnalysisFailure.IsAuth(error).Should().BeFalse();
        AnalysisFailure.IsTransient(error).Should().BeFalse();
    }
}
