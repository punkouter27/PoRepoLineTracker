using System.Net;
using System.Net.Http.Json;
using PoRepoLineTracker.Shared.Domain;
using PoRepoLineTracker.Shared.Models;
using PoRepoLineTracker.Shared.Models.Dtos;
using PoRepoLineTracker.Shared.Serialization;

namespace PoRepoLineTracker.Client.Services;

/// <summary>
/// Typed API client replacing raw HttpClient calls across .razor components (Rule 14).
/// </summary>
public sealed class RepoApiClient(HttpClient http)
{
    public Task<List<GitHubRepository>?> GetRepositoriesAsync(CancellationToken ct = default) =>
        http.GetAppJsonAsync("/api/repositories", AppJsonSerializerContext.Default.ListGitHubRepository, ct);

    public Task<AnalysisProgressDto?> GetAnalysisProgressAsync(RepositoryId repositoryId, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/{repositoryId}/analysis-progress", AppJsonSerializerContext.Default.AnalysisProgressDto, ct);

    public Task<List<RepositoryLineCountHistoryDto>?> GetAllChartsAsync(int days, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/allcharts/{days}", AppJsonSerializerContext.Default.ListRepositoryLineCountHistoryDto, ct);

    public Task<List<DailyLineCountDto>?> GetLineHistoryAsync(RepositoryId repositoryId, int days, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/{repositoryId}/linehistory/{days}", AppJsonSerializerContext.Default.ListDailyLineCountDto, ct);

    public Task<List<FileExtensionPercentageDto>?> GetFileExtensionPercentagesAsync(RepositoryId repositoryId, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/{repositoryId}/file-extension-percentages", AppJsonSerializerContext.Default.ListFileExtensionPercentageDto, ct);

    public Task<List<PunchcardItemDto>?> GetPunchcardAsync(RepositoryId repositoryId, int days, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/{repositoryId}/punchcard/{days}", AppJsonSerializerContext.Default.ListPunchcardItemDto, ct);

    public Task<List<ContributorStatsDto>?> GetContributorsAsync(RepositoryId repositoryId, int days, int topN = 10, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/{repositoryId}/contributors/{days}?topN={topN}", AppJsonSerializerContext.Default.ListContributorStatsDto, ct);

    public Task<HotspotsDto?> GetHotspotsAsync(RepositoryId repositoryId, int days, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/repositories/{repositoryId}/hotspots/{days}", AppJsonSerializerContext.Default.HotspotsDto, ct);

    public Task<PortfolioInsightsDto?> GetPortfolioInsightsAsync(CancellationToken ct = default) =>
        http.GetAppJsonAsync("/api/insights/portfolio", AppJsonSerializerContext.Default.PortfolioInsightsDto, ct);

    public Task<YearInReviewDto?> GetYearInReviewAsync(int year, int offsetMinutes, CancellationToken ct = default) =>
        http.GetAppJsonAsync($"/api/insights/year/{year}?offset={offsetMinutes}", AppJsonSerializerContext.Default.YearInReviewDto, ct);

    public Task<DiagnosticsResponse?> GetDiagnosticsAsync(CancellationToken ct = default) =>
        http.GetAppJsonAsync("/api/diagnostics", AppJsonSerializerContext.Default.DiagnosticsResponse, ct);

    public Task<List<CodeHealthSummaryDto>?> GetCodeHealthSummaryAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync("api/code-health", AppJsonSerializerContext.Default.ListCodeHealthSummaryDto, ct);

    public Task<CodeHealthTrendDto?> GetCodeHealthTrendAsync(RepositoryId repositoryId, CancellationToken ct = default) =>
        http.GetFromJsonAsync($"api/code-health/{repositoryId}/trend", AppJsonSerializerContext.Default.CodeHealthTrendDto, ct);

    public async Task<(CodeHealthDto? Report, HttpStatusCode Status)> GetCodeHealthAsync(RepositoryId repositoryId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/api/code-health/{repositoryId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return (null, response.StatusCode);
        }

        return (await response.Content.ReadAppJsonAsync(AppJsonSerializerContext.Default.CodeHealthDto, ct), response.StatusCode);
    }

    public async Task<(List<GitHubUserRepositoryDto>? Repos, HttpStatusCode Status, string? ErrorDetail)> GetGitHubUserRepositoriesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/api/github/user-repositories", ct);
        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadAppJsonAsync(AppJsonSerializerContext.Default.ListGitHubUserRepositoryDto, ct), response.StatusCode, null);
        }

        return (null, response.StatusCode, await ProblemDetailsParser.TryExtractDetailAsync(response, ct));
    }

    public async Task<(BulkAddResult? Result, HttpStatusCode Status, string? ErrorDetail)> BulkAddRepositoriesAsync(
        IReadOnlyList<BulkRepositoryDto> repositories,
        CancellationToken ct = default)
    {
        var aggregate = new BulkAddResult();
        HttpStatusCode lastStatus = HttpStatusCode.OK;
        foreach (var chunk in repositories.Chunk(200))
        {
            using var response = await http.PostAppJsonAsync("/api/repositories/bulk", chunk.ToList(),
                AppJsonSerializerContext.Default.ListBulkRepositoryDto, ct);
            lastStatus = response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return (null, lastStatus, await ProblemDetailsParser.TryExtractDetailAsync(response, ct));
            }

            var part = await response.Content.ReadAppJsonAsync(AppJsonSerializerContext.Default.BulkAddResult, ct);
            aggregate.Added.AddRange(part?.Added ?? []);
            aggregate.AlreadyTracked.AddRange(part?.AlreadyTracked ?? []);
        }

        return (aggregate, lastStatus, null);
    }

    public async Task<(AccountDeletedDto? Result, HttpStatusCode Status)> DeleteAccountAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync("/api/account", ct);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests)
        {
            return (null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAppJsonAsync(AppJsonSerializerContext.Default.AccountDeletedDto, ct), response.StatusCode);
    }
}
