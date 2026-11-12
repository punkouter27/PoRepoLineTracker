using System.Text.Json;

namespace PoRepoLineTracker.Client.Services;

/// <summary>
/// Shared RFC 7807 ProblemDetails response parser using trim-safe JsonDocument parsing (Rule 14).
/// </summary>
internal static class ProblemDetailsParser
{
    public static async Task<string?> TryExtractDetailAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                return detail.GetString();
            }

            if (doc.RootElement.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
            {
                return title.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON payload; fall through to raw body below.
        }

        return body;
    }
}
