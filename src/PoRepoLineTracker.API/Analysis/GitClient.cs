using System.Diagnostics;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.API.Analysis;

/// <summary>
/// Git client that uses the git CLI for network operations (clone/pull) to avoid
/// LibGit2Sharp native SIGABRT crashes in containerized Linux environments, while
/// retaining LibGit2Sharp for local read-only operations (traversing commits/blobs).
/// Consumed directly by <see cref="GitHubService"/> — the interface it used to sit behind
/// had one implementation, one injection site, and no test ever substituted it.
/// </summary>
public class GitClient
{
    private readonly ILogger<GitClient> _logger;

    /// <summary>
    /// Longest any one git invocation may run. There was no limit: a stalled clone (a dead
    /// connection git never notices) blocked its thread forever and held the per-repository
    /// analysis lock, so every later analysis of that repository was silently skipped until the
    /// process restarted.
    /// ponytail: one fixed ceiling for clone and fetch alike; make it per-operation or
    /// configurable if a legitimately huge repository ever needs longer.
    /// </summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(30);

    public GitClient(ILogger<GitClient> logger)
    {
        _logger = logger;
    }

    public string Clone(string repoUrl, string localPath, string? accessToken = null)
    {
        _logger.LogInformation("Cloning repository {RepoUrl} to {LocalPath} via git CLI", repoUrl, localPath);

        // Embed token in URL for HTTPS authentication (x-access-token is the standard GitHub approach).
        // Sanity-check the token shape: only GitHub tokens (ghp_/gho_/ghu_/ghs_/ghr_/github_pat_)
        // belong in a GitHub clone URL — anything else is silently rejected by libcurl with a
        // confusing "Port number" error, so surface a clear diagnostic before invoking git.
        if (!string.IsNullOrEmpty(accessToken) && !LooksLikeGitHubToken(accessToken))
        {
            _logger.LogWarning(
                "Access token for clone of {RepoUrl} does not look like a GitHub token (length={Length}, prefix='{Prefix}'). Git will likely reject it — check GitHub:PAT in Key Vault.",
                repoUrl, accessToken.Length, SafePrefix(accessToken));
        }

        string cloneUrl = BuildAuthUrl(repoUrl, accessToken);

        // Analysis reads exclusively from the git object store (commit/tree/blob traversal
        // via LibGit2Sharp — see GitHubService.ProcessTreeEntry), so the working tree is never
        // read. Skip the checkout: it avoids materializing deeply-nested files that blow past
        // Windows' 260-char MAX_PATH ("path too long"), and it clones faster with less disk.
        RunGitProcess("clone", ["clone", "--quiet", "--no-checkout", "--", cloneUrl, localPath], workingDirectory: null);

        // Scrub the credential out of the stored remote URL, immediately.
        //
        // `git clone https://x-access-token:<token>@github.com/...` PERSISTS that URL verbatim
        // as remote.origin.url in .git/config. The token is a live GitHub credential and it was
        // being written to disk in plaintext, once per cloned repository, surviving for as long
        // as the clone did. Pull's set-url/restore dance could not undo it either: it read the
        // stored URL as the "original" to restore, and the stored URL already had the token in
        // it, so the restore faithfully put the credential back.
        //
        // Nothing needs the credential to persist — every network operation re-supplies it via
        // BuildAuthUrl at the point of use.
        if (!string.IsNullOrEmpty(accessToken))
        {
            RunGitProcess("remote set-url (scrub credential)", ["remote", "set-url", "origin", repoUrl],
                workingDirectory: localPath);
        }

        _logger.LogInformation("Successfully cloned {RepoUrl} to {LocalPath}", repoUrl, localPath);
        return localPath;
    }

    private static bool LooksLikeGitHubToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        // Classic PATs: ghp_/gho_/ghu_/ghs_/ghr_  (length 36-40+ in practice)
        // Fine-grained PATs: github_pat_  (length 82+)
        if (token.StartsWith("ghp_", StringComparison.Ordinal) ||
            token.StartsWith("gho_", StringComparison.Ordinal) ||
            token.StartsWith("ghu_", StringComparison.Ordinal) ||
            token.StartsWith("ghs_", StringComparison.Ordinal) ||
            token.StartsWith("ghr_", StringComparison.Ordinal) ||
            token.StartsWith("github_pat_", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static string SafePrefix(string token)
        => token.Length <= 8 ? token : token.Substring(0, 4) + "…" + token.Substring(token.Length - 4);

    /// <summary>
    /// Brings the local clone up to date WITHOUT materialising a working tree.
    /// </summary>
    /// <remarks>
    /// This was <c>git pull</c>, which silently undid the <c>--no-checkout</c> that
    /// <see cref="Clone"/> is careful to pass: a pull merges into the working tree, so the
    /// first refresh of any repository checked every file out and reintroduced exactly the
    /// MAX_PATH failure the no-checkout clone exists to avoid. Reported against
    /// <c>PoSeeReview</c>, which carries Kudu trace files whose names alone approach 150
    /// characters:
    /// <c>path too long: '…/app-logs/LogFiles/kudu/trace/2025-11-12T20-53-08_…xml'</c>.
    ///
    /// <para>Analysis reads exclusively from the object store (see
    /// <c>GitHubService.ProcessTreeEntry</c>), and the commit walk starts at
    /// <c>repo.Head</c> — so all that has to happen here is: fetch the objects, then move the
    /// local branch ref to match the remote. `git fetch` cannot update the currently checked-out
    /// branch itself, hence the explicit <c>update-ref</c>.</para>
    /// </remarks>
    public void Pull(string localPath, string? accessToken = null)
    {
        _logger.LogInformation("Fetching repository at {LocalPath} via git CLI", localPath);

        if (!string.IsNullOrEmpty(accessToken))
        {
            // Embed the token in the remote URL for the fetch, then restore it.
            RunGitProcess("remote get-url", ["remote", "get-url", "origin"], workingDirectory: localPath,
                captureOutput: true, out string remoteUrl);

            // Strip any credential the stored URL already carries before using it as the
            // "original". Clones made before the scrub in Clone() have a live token baked into
            // remote.origin.url, and restoring that verbatim — which is what this used to do —
            // wrote the credential straight back to disk every time. Scrubbing here means an
            // existing poisoned clone is cleaned by its next refresh.
            var cleanUrl = StripCredentials(remoteUrl.Trim());

            string authUrl = BuildAuthUrl(cleanUrl, accessToken);
            RunGitProcess("remote set-url (auth)", ["remote", "set-url", "origin", authUrl], workingDirectory: localPath);
            try
            {
                FetchAndAdvanceHead(localPath);
            }
            finally
            {
                RunGitProcess("remote set-url (restore, credential-free)", ["remote", "set-url", "origin", cleanUrl],
                    workingDirectory: localPath);
            }
        }
        else
        {
            FetchAndAdvanceHead(localPath);
        }

        _logger.LogInformation("Successfully fetched repository at {LocalPath}", localPath);
    }

    private void FetchAndAdvanceHead(string localPath)
    {
        RunGitProcess("fetch", ["fetch", "--quiet", "--prune", "origin"], workingDirectory: localPath);

        // Which branch HEAD points at. A --no-checkout clone still has a symbolic HEAD, so this
        // is the branch the commit walk will read.
        RunGitProcess("symbolic-ref", ["symbolic-ref", "--short", "HEAD"], workingDirectory: localPath,
            captureOutput: true, out string branchOutput);

        var branch = branchOutput.Trim();
        if (string.IsNullOrEmpty(branch))
        {
            // Detached HEAD — nothing to advance; the fetched objects are already reachable
            // from the remote-tracking refs and the next analysis will re-resolve HEAD.
            _logger.LogWarning("Repository at {LocalPath} has a detached HEAD; leaving it where it is.", localPath);
            return;
        }

        // Fast-forward the local branch to the fetched remote-tracking ref. `update-ref` is a
        // pure ref write: it touches no file in the working tree, which is the whole point.
        RunGitProcess("update-ref", ["update-ref", $"refs/heads/{branch}", $"refs/remotes/origin/{branch}"],
            workingDirectory: localPath);
    }

    /// <summary>
    /// Per-file change counts since a date, for the hotspot view: how many commits touched each
    /// path, and how many lines they added plus removed.
    ///
    /// <para>The git CLI rather than a LibGit2Sharp tree diff per commit: `git log --numstat` is
    /// one process that git has optimised for exactly this, where diffing every commit pair in
    /// managed code costs seconds per hundred commits. Read-only, so it needs no credential and
    /// is safe beside a running fetch.</para>
    ///
    /// <para><c>--no-renames</c> keeps every path a plain path (no <c>old => new</c> syntax to
    /// unpick); a rename then counts as a delete of one path and an add of another, and only the
    /// new path is still at HEAD to be shown. Merges print no numstat by default, matching the
    /// rule that a merge's lines were already counted on its branch.</para>
    /// </summary>
    public Dictionary<string, FileChurn> FileChurnSince(string repositoryPath, DateTime sinceUtc)
    {
        RunGitProcess("log --numstat", [
                "log", "--numstat", "--no-renames", "--format=%x1e",
                $"--since={sinceUtc:yyyy-MM-ddTHH:mm:ssZ}", "HEAD"
            ],
            workingDirectory: repositoryPath, captureOutput: true, out var output);
        return ParseNumstat(output);
    }

    /// <summary>
    /// Parses <see cref="FileChurnSince"/>'s output: a record-separator line opens each commit,
    /// then one <c>added TAB removed TAB path</c> line per file it touched (<c>-</c> for a binary
    /// file's counts).
    /// </summary>
    internal static Dictionary<string, FileChurn> ParseNumstat(string output)
    {
        var churn = new Dictionary<string, FileChurn>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.TrimEnd('\r').Split('\t', 3);
            if (parts.Length != 3 || parts[2].Length == 0) continue;

            int.TryParse(parts[0], out var added);
            int.TryParse(parts[1], out var removed);
            churn[parts[2]] = churn.TryGetValue(parts[2], out var existing)
                ? new FileChurn(existing.Commits + 1, existing.LinesChanged + added + removed)
                : new FileChurn(1, added + removed);
        }
        return churn;
    }

    public Repository OpenRepository(string path)
    {
        return new Repository(path);
    }

    /// <summary>The 7-character abbreviated SHA every report shows (or the whole value if shorter).</summary>
    internal static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    // ── Private helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Removes any <c>user:password@</c> userinfo from an HTTP(S) URL, leaving the bare
    /// repository URL. Returns the input unchanged if it is not a parseable absolute URL.
    /// </summary>
    internal static string StripCredentials(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (string.IsNullOrEmpty(uri.UserInfo)) return url;

        return $"{uri.Scheme}://{uri.Authority}{uri.PathAndQuery}";
    }

    private static string BuildAuthUrl(string repoUrl, string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
            return repoUrl;

        var uri = new Uri(repoUrl);

        // URL-encode the token so any reserved characters (':', '/', '+', '=', etc.)
        // cannot confuse libcurl's URL parser into misreading part of it as `host:port`.
        var encodedToken = Uri.EscapeDataString(accessToken);
        return $"{uri.Scheme}://x-access-token:{encodedToken}@{uri.Host}{uri.PathAndQuery}";
    }

    private void RunGitProcess(string operationLabel, string[] arguments, string? workingDirectory)
        => RunGitProcess(operationLabel, arguments, workingDirectory, captureOutput: false, out _);

    private void RunGitProcess(string operationLabel, string[] arguments, string? workingDirectory,
        bool captureOutput, out string output)
    {
        _logger.LogDebug("Running git {Operation} (args: {ArgCount})", operationLabel, arguments.Length);

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Belt and braces with the no-checkout clone. Even with no working tree, git writes
        // paths of its own under .git/ (packed refs, lock files, and — for a repository whose
        // own file names are long — index and object paths), and on Windows those are subject
        // to the 260-character MAX_PATH unless long paths are enabled. This is passed per
        // invocation rather than relying on the machine's global git config, because the
        // deployment target is not a machine anyone configures by hand.
        //
        // `-c` must precede the subcommand, so it is prepended rather than appended.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.longpaths=true");

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        // Prevent git from hanging waiting for a terminal credential prompt
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for git {operationLabel}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            _logger.LogError(ex, "git executable not found in PATH. Cannot perform git {Operation}. " +
                "Ensure git is installed in the container.", operationLabel);
            throw new InvalidOperationException(
                "git is not installed or not in PATH. " +
                $"Cannot perform git {operationLabel}. Install git in the deployment environment.", ex);
        }

        using (process)
        {
            // Read both streams concurrently to avoid deadlock on full buffers
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                // The whole tree: git spawns helpers (git-remote-https) that hold the connection.
                process.Kill(entireProcessTree: true);
                _logger.LogError("git {Operation} exceeded {Timeout} and was killed.", operationLabel, GitTimeout);
                throw new TimeoutException($"git {operationLabel} did not finish within {GitTimeout.TotalMinutes:0} minutes.");
            }

            output = stdoutTask.Result;
            var stderr = stderrTask.Result;

            if (process.ExitCode != 0)
            {
                // Sanitize any embedded credentials before logging
                var safeStderr = RedactCredentials(stderr);
                _logger.LogError("git {Operation} failed. ExitCode={ExitCode}. Stderr: {Stderr}",
                    operationLabel, process.ExitCode, safeStderr);
                throw new InvalidOperationException(
                    $"git {operationLabel} failed (exit {process.ExitCode}): {safeStderr}");
            }

            _logger.LogDebug("git {Operation} succeeded. ExitCode=0", operationLabel);
        }
    }

    /// <summary>Redacts any https://user:password@ patterns to prevent leaking access tokens in logs.</summary>
    private static string RedactCredentials(string text)
        => Regex.Replace(text, @"https?://[^:@\s]+:[^@\s]+@", "https://[REDACTED]@",
            RegexOptions.IgnoreCase);
}
