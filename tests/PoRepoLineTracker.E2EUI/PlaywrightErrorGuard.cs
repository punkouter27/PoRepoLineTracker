using System.Net;
using FluentAssertions;
using Microsoft.Playwright;

namespace PoRepoLineTracker.E2EUI;

/// <summary>
/// Attaches Playwright console.error, pageerror, crash, requestfailed, HTTP &gt;= 400, and Blazor
/// unhandled-error UI listeners to a page and fails the test on unexpected errors (Rule 18).
/// </summary>
internal sealed class PlaywrightErrorGuard : IAsyncDisposable
{
    private readonly List<string> _errors = [];
    private readonly List<(int Status, string UrlContains)> _allowedStatuses = [];
    private readonly IPage _page;

    private PlaywrightErrorGuard(IPage page)
    {
        _page = page;
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
            {
                if (msg.Text.Contains("SSL certificate error", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (_allowedStatuses.Count > 0 && msg.Text.Contains("the server responded with a status of", StringComparison.Ordinal))
                {
                    return;
                }
                _errors.Add($"[console.error] {msg.Text}");
            }
        };
        page.PageError += (_, error) => _errors.Add($"[pageerror] {error}");
        page.Crash += (_, _) => _errors.Add("[crash] Browser page crashed.");
        page.RequestFailed += (_, req) =>
        {
            if (req.Failure is { Length: > 0 } reason && !reason.Contains("ERR_ABORTED", StringComparison.Ordinal))
            {
                _errors.Add($"[requestfailed] {req.Method} {req.Url}: {reason}");
            }
        };
        page.Response += (_, res) =>
        {
            if (res.Status >= 400 && !IsAllowedStatus(res.Status, res.Url))
            {
                _errors.Add($"[http {res.Status}] {res.Request.Method} {res.Url}");
            }
        };
    }

    public static PlaywrightErrorGuard Watch(IPage page) => new(page);

    public void AllowHttp(HttpStatusCode status, string urlContains) =>
        _allowedStatuses.Add(((int)status, urlContains));

    private bool IsAllowedStatus(int status, string url) =>
        _allowedStatuses.Any(rule => rule.Status == status && url.Contains(rule.UrlContains, StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        if (!_page.IsClosed)
        {
            var blazorError = _page.Locator("#blazor-error-ui");
            if (await blazorError.CountAsync() > 0 && await blazorError.IsVisibleAsync())
            {
                _errors.Add("[blazor-error-ui] Blazor unhandled error banner is visible.");
            }
        }

        _errors.Should().BeEmpty("E2EUI must complete with zero browser console/network/Blazor errors");
    }
}
