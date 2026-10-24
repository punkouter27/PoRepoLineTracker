using System.Net;
using Microsoft.Extensions.Logging;
using PoRepoLineTracker.Shared.Models.Dtos;
using PoRepoLineTracker.Shared.Serialization;
using Radzen;

namespace PoRepoLineTracker.Client.Services;

/// <summary>
/// The optional assistant features: the digest's one-paragraph summary and the grid's "Ask".
///
/// <para>Both are decoration on pages that work without them, so nothing here throws to the UI:
/// every failure is a null, and a deployment with no API key answers "not available" once and the
/// controls are never rendered. Like <see cref="RepositoryCommandClient"/>, this owns its own
/// toasts so the grid does not grow a status-code ladder.</para>
/// </summary>
public sealed class AssistantClient(
    HttpClient http,
    NotificationService notificationService,
    ILogger<AssistantClient> logger)
{
    /// <summary>
    /// The task, not the bool: the banner and the grid mount on the same page and both ask, and
    /// caching only the result would let the second caller send its own request while the first
    /// was still in flight. A failed check stays "unavailable" until reload.
    /// </summary>
    private Task<bool>? _available;

    public Task<bool> IsAvailableAsync() => _available ??= FetchAvailableAsync();

    private async Task<bool> FetchAvailableAsync()
    {
        try
        {
            var status = await http.GetAppJsonAsync("/api/insights/assistant", AppJsonSerializerContext.Default.AiStatusDto);
            return status?.Available == true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Assistant status unavailable — assistant features not shown");
            return false;
        }
    }

    /// <summary>Null when there is nothing to show, for any reason (including a 429).</summary>
    public async Task<string?> GetDigestNarrativeAsync()
    {
        if (!await IsAvailableAsync()) return null;

        try
        {
            var narrative = await http.GetAppJsonAsync("/api/insights/digest/narrative", AppJsonSerializerContext.Default.DigestNarrativeDto);
            return string.IsNullOrWhiteSpace(narrative?.Text) ? null : narrative.Text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Digest narrative unavailable — banner shown without it");
            return null;
        }
    }

    /// <summary>The closing sentence of the Year in Review; null when there is nothing to show, for any reason.</summary>
    public async Task<string?> GetYearNarrativeAsync(int year, int utcOffsetMinutes)
    {
        if (!await IsAvailableAsync()) return null;

        try
        {
            var narrative = await http.GetAppJsonAsync($"/api/insights/year/{year}/narrative?offset={utcOffsetMinutes}", AppJsonSerializerContext.Default.DigestNarrativeDto);
            return string.IsNullOrWhiteSpace(narrative?.Text) ? null : narrative.Text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Year narrative unavailable — card shown without it");
            return null;
        }
    }

    /// <summary>
    /// Maps a question onto the grid's own filters. Null when it could not be answered, in which
    /// case the user has already been told why.
    /// </summary>
    public async Task<GridFilterDto?> AskAsync(string query)
    {
        try
        {
            using var response = await http.PostAppJsonAsync("/api/repositories/ask",
                new GridQueryRequest { Query = query }, AppJsonSerializerContext.Default.GridQueryRequest);

            if (response.IsSuccessStatusCode)
            {
                var filter = await response.Content.ReadAppJsonAsync(AppJsonSerializerContext.Default.GridFilterDto);
                if (filter is not null) return filter;
            }

            logger.LogWarning("Ask returned {Status}", response.StatusCode);
            Warn(response.StatusCode == HttpStatusCode.TooManyRequests);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ask failed");
            Warn(rateLimited: false);
        }

        return null;
    }

    private void Warn(bool rateLimited) =>
        notificationService.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Warning,
            Summary = rateLimited ? "Too many requests" : "Couldn't work that one out — try rephrasing",
            Detail = rateLimited ? "Try again in a minute." : null,
            Duration = 6000
        });
}
