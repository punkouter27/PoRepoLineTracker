using FluentAssertions;

namespace PoRepoLineTracker.Unit.Shared;

public class AnalysisFailureTests
{
    /// <summary>
    /// Auth failures are never retried (the token will not fix itself), network failures are, and
    /// anything else (a missing repository, a timeout) is neither. One error is never both.
    /// </summary>
    [Theory]
    [InlineData("git clone failed (exit 128): remote: Invalid username or token. fatal: Authentication failed for 'https://github.com/o/r.git/'", true, false)]
    [InlineData("git fetch failed (exit 128): fatal: could not read Username for 'https://github.com': terminal prompts disabled", true, false)]
    [InlineData("git clone failed (exit 128): fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 403", true, false)]
    [InlineData("git clone failed (exit 128): fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", false, true)]
    [InlineData("git fetch failed (exit 128): error: RPC failed; curl 56 Recv failure: Connection reset by peer", false, true)]
    [InlineData("git clone failed (exit 128): fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 502", false, true)]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("git clone failed (exit 128): remote: Repository not found.", false, false)]
    [InlineData("git clone did not finish within 30 minutes.", false, false)]
    public void Classifies_auth_transient_or_neither(string? error, bool isAuth, bool isTransient)
    {
        AnalysisFailure.IsAuth(error).Should().Be(isAuth);
        AnalysisFailure.IsTransient(error).Should().Be(isTransient);
    }
}
