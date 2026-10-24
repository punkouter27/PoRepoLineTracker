namespace PoRepoLineTracker.Shared.Domain;

/// <summary>
/// Reads an analysis error (git's stderr, as <c>GitClient</c> wraps it) as one of the two kinds
/// of failure the app acts on differently.
///
/// <para><b>Why one home.</b> The server retries a transient clone/pull failure, and the grid
/// shows an auth failure as "sign in again" instead of a raw stderr line. Both read the same
/// stored <see cref="GitHubRepository.LastAnalysisError"/>; two copies of these lists would
/// eventually disagree and a revoked token would be retried as a network blip.</para>
///
/// <para>ponytail: substring matching on git/libcurl wording, which is stable but English-only
/// and not a contract. An unrecognised message is neither kind, so it fails once and is shown
/// as-is, which is today's behaviour.</para>
/// </summary>
public static class AnalysisFailure
{
    /// <summary>
    /// The token is missing, revoked or lacks access. A private repository without a usable
    /// credential reaches the terminal-prompt path, which GIT_TERMINAL_PROMPT=0 turns into
    /// "could not read Username".
    /// </summary>
    private static readonly string[] AuthMarkers =
    [
        "Authentication failed",
        "Invalid username or",
        "could not read Username",
        "returned error: 401",
        "returned error: 403",
        "Permission to"
    ];

    /// <summary>
    /// The network or GitHub hiccupped; the same request a minute later usually works. Not the
    /// 30-minute timeout: a clone that stalled that long will stall again, and a retry holds the
    /// repository's lock for another half hour.
    /// </summary>
    private static readonly string[] TransientMarkers =
    [
        "Could not resolve host",
        "Failed to connect",
        "Connection timed out",
        "Connection reset",
        "early EOF",
        "RPC failed",
        "remote end hung up",
        "returned error: 5",
        "Operation timed out",
        "TLS connection was non-properly terminated"
    ];

    public static bool IsAuth(string? error) => Matches(error, AuthMarkers);

    public static bool IsTransient(string? error) => !IsAuth(error) && Matches(error, TransientMarkers);

    private static bool Matches(string? error, string[] markers) =>
        !string.IsNullOrEmpty(error) && markers.Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase));
}
