using System.Text.Json;
using System.Text.Json.Serialization;
using PoRepoLineTracker.Shared.Domain;
using PoRepoLineTracker.Shared.Models;
using PoRepoLineTracker.Shared.Models.Dtos;

namespace PoRepoLineTracker.Shared.Serialization;

/// <summary>
/// Source-generated metadata for every type that crosses the wire (zero-reflection
/// serialization across the API and the WASM client).
///
/// <para><b>Why it lives in Shared.</b> Both ends of every call deserialize the same contract, so
/// generating the metadata once in the leaf assembly means the API and the client are provably
/// using the same shape — and the WASM client never has to carry the reflection-based
/// serializer, which is what lets the published output be trimmed (see the .Client csproj).</para>
///
/// <para><b>Options.</b> <see cref="JsonSerializerDefaults.Web"/> matches what both ASP.NET Core
/// minimal APIs and Blazor's <c>HttpClientJsonExtensions</c> use by default: camelCase names,
/// case-insensitive reads, and numbers readable from strings. Declaring it here rather than
/// relying on the ambient default keeps the two ends aligned even if a host default changes.</para>
///
/// <para><b>Adding a type.</b> Add a <c>[JsonSerializable]</c> line for the exact root type you
/// (de)serialize — the generator walks properties from there, but a <c>List&lt;T&gt;</c> root is
/// a different root than <c>T</c>, so both are listed where both are used. A missing entry does
/// not fail the build; it fails at runtime with "metadata for type ... was not provided", so
/// prefer adding the root eagerly.</para>
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]

// ─── Domain entities returned directly ──────────────────────────────────────────────────────
[JsonSerializable(typeof(GitHubRepository))]
[JsonSerializable(typeof(List<GitHubRepository>))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(UserPreferences))]
[JsonSerializable(typeof(CommitLineCount))]
[JsonSerializable(typeof(List<CommitLineCount>))]

// ─── Repository / chart DTOs ────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(GitHubRepositoryDto))]
[JsonSerializable(typeof(List<GitHubRepositoryDto>))]
[JsonSerializable(typeof(GitHubUserRepositoryDto))]
[JsonSerializable(typeof(List<GitHubUserRepositoryDto>))]
[JsonSerializable(typeof(CommitOutlierDto))]
[JsonSerializable(typeof(AiStatusDto))]
[JsonSerializable(typeof(DigestNarrativeDto))]
[JsonSerializable(typeof(GridQueryRequest))]
[JsonSerializable(typeof(GridFilterDto))]
[JsonSerializable(typeof(BulkRepositoryDto))]
[JsonSerializable(typeof(List<BulkRepositoryDto>))]
[JsonSerializable(typeof(IEnumerable<BulkRepositoryDto>))]
[JsonSerializable(typeof(BulkAddResult))]
[JsonSerializable(typeof(DailyLineCountDto))]
[JsonSerializable(typeof(List<DailyLineCountDto>))]
[JsonSerializable(typeof(RepositoryLineCountHistoryDto))]
[JsonSerializable(typeof(List<RepositoryLineCountHistoryDto>))]
[JsonSerializable(typeof(FileExtensionPercentageDto))]
[JsonSerializable(typeof(List<FileExtensionPercentageDto>))]
[JsonSerializable(typeof(CommitStatsDto))]
[JsonSerializable(typeof(List<CommitStatsDto>))]

// ─── Portfolio insights ─────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(PortfolioInsightsDto))]
[JsonSerializable(typeof(RepositoryMovementDto))]
[JsonSerializable(typeof(LanguageShareDto))]
[JsonSerializable(typeof(ActivityDayDto))]
[JsonSerializable(typeof(PortfolioTrendPointDto))]
[JsonSerializable(typeof(LanguageDriftDto))]

// ─── Since-you-were-away digest ─────────────────────────────────────────────────────────────
[JsonSerializable(typeof(WeeklyDigestDto))]
[JsonSerializable(typeof(DigestRepoDto))]

// ─── Code health report ─────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(CodeHealthDto))]
[JsonSerializable(typeof(CodeHealthFactorDto))]
[JsonSerializable(typeof(CodeHealthFileDto))]
[JsonSerializable(typeof(CodeHealthLanguageDto))]
[JsonSerializable(typeof(CodeMetricsReportDto))]
[JsonSerializable(typeof(CodeMetricsMemberDto))]
[JsonSerializable(typeof(CodeHealthSummaryDto))]
[JsonSerializable(typeof(List<CodeHealthSummaryDto>))]
[JsonSerializable(typeof(CodeHealthTrendDto))]
[JsonSerializable(typeof(CodeHealthTrendPointDto))]
[JsonSerializable(typeof(List<CodeHealthTrendDto>))]

// ─── Contributor DTOs ────────────────────────────────────────────────────────────────────────
[JsonSerializable(typeof(ContributorStatsDto))]
[JsonSerializable(typeof(List<ContributorStatsDto>))]
[JsonSerializable(typeof(DailyContributorStatsDto))]
[JsonSerializable(typeof(List<DailyContributorStatsDto>))]

// ─── Progress, auth, diagnostics, upload, antiforgery ───────────────────────────────────────
[JsonSerializable(typeof(AnalysisProgressDto))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(DiagnosticsResponse))]
[JsonSerializable(typeof(AntiforgeryTokenResponse))]
[JsonSerializable(typeof(PunchcardItemDto))]
[JsonSerializable(typeof(List<PunchcardItemDto>))]
[JsonSerializable(typeof(PortfolioExportRowDto))]
[JsonSerializable(typeof(List<PortfolioExportRowDto>))]
[JsonSerializable(typeof(AccountDeletedDto))]
[JsonSerializable(typeof(RepositoryVisibilityDto))]
[JsonSerializable(typeof(YearInReviewDto))]
[JsonSerializable(typeof(HotspotsDto))]
[JsonSerializable(typeof(HotspotFileDto))]

// ─── Primitive collections persisted as JSON columns in Azure Table Storage ─────────────────
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public sealed partial class AppJsonSerializerContext : JsonSerializerContext;
