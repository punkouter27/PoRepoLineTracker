namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// Wire shape for <c>GET /api/diagnostics</c>.
///
/// These were anonymous types on the API side and a private mirror of them inside
/// ExternalConnections.razor on the client side. Anonymous types cannot be described by a
/// <c>JsonSerializerContext</c> — the source generator has no name to emit metadata for — so
/// zero-reflection serialization requires a concrete contract. Declaring it once in Shared also
/// removes the hand-maintained client copy that had to be kept in step by eye.
/// </summary>
public sealed class DiagnosticsResponse
{
    public string Environment { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string OverallHealth { get; set; } = string.Empty;
    public ExternalConnectionsData ExternalConnections { get; set; } = new();
    public DiagnosticsSummary Summary { get; set; } = new();
}

/// <summary>External dependencies grouped by provider, as rendered by the /diagnostics page.</summary>
public sealed class ExternalConnectionsData
{
    public List<ExternalConnectionInfo> Azure { get; set; } = [];
    public List<ExternalConnectionInfo> GitHub { get; set; } = [];
    public List<ExternalConnectionInfo> OpenTelemetry { get; set; } = [];
}

/// <summary>A single external dependency and whether this deploy has it configured.</summary>
public sealed class ExternalConnectionInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
}

/// <summary>Roll-up counts shown above the per-provider grids.</summary>
public sealed class DiagnosticsSummary
{
    public int TotalConnections { get; set; }
    public int ConfiguredCount { get; set; }
    public string ApplicationPurpose { get; set; } = string.Empty;
}
