namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// Wire shape for the anonymous <c>GET /api/diag</c>, rendered by the <c>/diag</c> page.
///
/// <para>Anonymous, so it carries only what is safe for anyone to read: a status, a fixed
/// plain-English purpose and a latency per dependency, and at most an exception TYPE name. Never a
/// health-check description, exception message, connection string, URI, path or id; the
/// integration tier asserts that (DiagNoSecretsTests).</para>
/// </summary>
public sealed class DiagnosticsResponse
{
    /// <summary>Healthy, Degraded or Unhealthy: the worst of the health checks.</summary>
    public string Status { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;

    /// <summary>The server build's YYMMDD stamp.</summary>
    public string Version { get; set; } = string.Empty;
    public DateTime CheckedAtUtc { get; set; }

    /// <summary>Data stores (Azure Table Storage).</summary>
    public List<DiagnosticsCheck> Storage { get; set; } = [];

    /// <summary>External dependencies: Key Vault, GitHub API, GitHub sign-in, telemetry export.</summary>
    public List<DiagnosticsCheck> Services { get; set; } = [];
}

/// <summary>One dependency's result.</summary>
public sealed class DiagnosticsCheck
{
    public string Name { get; set; } = string.Empty;

    /// <summary>One line: what the app uses it for.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Healthy, Degraded or Unhealthy.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>A short, fixed explanation of a non-healthy status (never the check's own text).</summary>
    public string? Note { get; set; }

    /// <summary>Probe duration; null for configuration-only rows that make no call.</summary>
    public double? LatencyMs { get; set; }

    /// <summary>The exception's type name when the probe threw. Never its message.</summary>
    public string? ErrorType { get; set; }
}
