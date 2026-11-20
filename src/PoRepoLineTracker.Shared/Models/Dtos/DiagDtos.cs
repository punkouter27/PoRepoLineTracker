namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// Wire shape of the anonymous <c>GET /api/diag</c> (the Po /diag contract), rendered by the <c>/diag</c> page.
///
/// <para>Anonymous, so it carries only what is safe for anyone to read: a status, fixed plain-English text and a
/// latency per dependency, and at most an exception TYPE name. Never a health-check description, exception message,
/// connection string, URI, path or id; the integration tier asserts that.</para>
/// </summary>
/// <param name="Status">Healthy, Degraded or Unhealthy: the worst of the checks.</param>
/// <param name="Version">The server build's YYMMDD stamp (assembly metadata <c>BuildVersion</c>).</param>
/// <param name="CheckedMs">How long the server took to run every check.</param>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAt,
    double CheckedMs,
    DiagCheckDto[] Checks);

/// <summary>One dependency's result.</summary>
/// <param name="Name">Stable machine name, e.g. <c>azure-table-storage</c>.</param>
/// <param name="Group"><c>api</c>, <c>data</c> or <c>service</c>: the /diag section it is listed under.</param>
/// <param name="Label">What the page shows, e.g. "Azure Table Storage".</param>
/// <param name="Status">Healthy, Degraded or Unhealthy.</param>
/// <param name="Purpose">One line: what the app uses it for.</param>
/// <param name="Detail">How it was checked, e.g. "Answered a query" or "Configuration check, no call made".</param>
/// <param name="LatencyMs">Probe duration; 0 for configuration-only rows that make no call.</param>
/// <param name="Problem">A short, fixed explanation of a non-healthy status (never the check's own text).</param>
public sealed record DiagCheckDto(
    string Name,
    string Group,
    string Label,
    string Status,
    string Purpose,
    string Detail,
    double LatencyMs,
    string? Problem);
