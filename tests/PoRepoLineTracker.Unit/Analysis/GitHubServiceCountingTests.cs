using FluentAssertions;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
namespace PoRepoLineTracker.Unit;

/// <summary>
/// Line counting driven against a REAL git repository built on disk, because the behaviour under
/// test is about git object identity and nothing substituted can exercise that.
///
/// <para>The counter memoises results on tree and blob object ids so that replaying a repository's
/// history does not re-walk unchanged directories — a 4,000-commit repository previously
/// decompressed every blob of every commit. A memo that returns a stale or shared dictionary is
/// the obvious way for that to go wrong, and it would show up as line counts that drift between
/// commits rather than as a crash. These pin it.</para>
/// </summary>
public sealed class GitHubServiceCountingTests : IDisposable
{
    private readonly string _repoPath;
    private readonly GitHubService _sut;

    private static readonly List<string> CountedExtensions = [".cs", ".css"];

    public GitHubServiceCountingTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "porepo-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoPath);
        Git("init", "-q");

        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        _sut = new GitHubService(
            new HttpClient(),
            configuration,
            Substitute.For<ILogger<GitHubService>>(),
            SourceLineCounter.DefaultSet(),
            new GitClient(Substitute.For<ILogger<GitClient>>()),
            new FileIgnoreFilter(Substitute.For<ILogger<FileIgnoreFilter>>()));
    }

    /// <summary>Writes the given files (path → content) and commits them, returning the commit SHA.</summary>
    private string Commit(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_repoPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false",
            "commit", "-q", "--allow-empty", "-m", "c");
        return Git("rev-parse", "HEAD").Trim();
    }

    /// <summary>The git CLI, as the app itself uses it.</summary>
    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repoPath, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', args)} failed");
        return output;
    }

    private Task<Dictionary<string, int>> CountAsync(string sha) =>
        _sut.CountLinesInCommitAsync(_repoPath, sha, CountedExtensions);

    [Fact]
    public async Task CountsConfiguredExtensions_IgnoringCaseAndHandlingUnknownCommits()
    {
        var sha = Commit(
            ("src/a.cs", "var x = 1;\nvar y = 2;\n"),
            ("src/notes.txt", "not counted\nnot counted\n"));

        var counts = await CountAsync(sha);
        counts.Should().ContainKey(".cs").WhoseValue.Should().Be(2);
        counts.Should().NotContainKey(".txt");

        var caseInsensitiveCounts = await _sut.CountLinesInCommitAsync(_repoPath, sha, [".CS"]);
        caseInsensitiveCounts.Should().ContainKey(".cs").WhoseValue.Should().Be(2);

        var unknownCounts = await CountAsync(new string('a', 40));
        unknownCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task CommitStats_DiffOnlyTheCountedFileTypes_AndReportProgress()
    {
        Commit(("src/a.cs", "var x = 1;\nvar y = 2;\n"), ("package-lock.json", "{\n\"a\": 1,\n\"b\": 2,\n\"c\": 3\n}\n"));
        Commit(("src/deep/b.cs", "var z = 3;\n"), ("package-lock.json", "{}\n"));

        // Unfiltered, the lock file dominates: 2 + 5 lines in the first commit.
        var all = (await _sut.GetCommitStatsAsync(_repoPath)).ToList();
        all.Sum(c => c.LinesAdded).Should().BeGreaterThan(3);

        var reports = new List<(int Done, int Total)>();
        var csOnly = (await _sut.GetCommitStatsAsync(_repoPath, null, [".cs"], (done, total) => reports.Add((done, total)))).ToList();

        // Only the .cs lines, at any depth: 2 in the first commit, 1 in the second, none removed.
        csOnly.Should().HaveCount(2);
        csOnly.Sum(c => c.LinesAdded).Should().Be(3);
        csOnly.Sum(c => c.LinesRemoved).Should().Be(0);
        reports.Should().NotBeEmpty();
        reports[0].Should().Be((0, 2));
    }

    [Fact]
    public async Task UnchangedDirectoryAndEmptyCommit_ServedFromMemo()
    {
        var first = Commit(
            ("stable/a.cs", "var a = 1;\nvar b = 2;\nvar c = 3;\n"),
            ("moving/b.cs", "var d = 4;\n"));

        var second = Commit(("moving/b.cs", "var d = 4;\nvar e = 5;\n"));

        var firstCounts = await CountAsync(first);
        var secondCounts = await CountAsync(second);

        firstCounts[".cs"].Should().Be(4);
        secondCounts[".cs"].Should().Be(5);

        var emptyCommit = Commit();
        var emptyCounts = await CountAsync(emptyCommit);
        emptyCounts.Should().BeEquivalentTo(secondCounts);
    }

    [Fact]
    public async Task MemoIsolationAndInvalidation_GuardsInstanceAndReevaluatesOnChange()
    {
        var sha = Commit(("src/a.cs", "var x = 1;\n"), ("src/a.css", "body { color: red; }\n"));

        var first = await CountAsync(sha);
        first[".cs"] = 9_999;

        var second = await CountAsync(sha);
        second[".cs"].Should().Be(1);

        var both = await _sut.CountLinesInCommitAsync(_repoPath, sha, [".cs", ".css"]);
        var onlyCs = await _sut.CountLinesInCommitAsync(_repoPath, sha, [".cs"]);

        both.Should().ContainKey(".css");
        onlyCs.Should().NotContainKey(".css");
        onlyCs[".cs"].Should().Be(1);
    }

    [Fact]
    public async Task ReplayingHistory_ReadsAnUnchangedFileOnlyOnce()
    {
        var counter = new CountingLineCounter(".cs");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        using var sut = new GitHubService(
            new HttpClient(),
            configuration,
            Substitute.For<ILogger<GitHubService>>(),
            [counter, new SourceLineCounter("*")],
            new GitClient(Substitute.For<ILogger<GitClient>>()),
            new FileIgnoreFilter(Substitute.For<ILogger<FileIgnoreFilter>>()));

        var shas = new List<string>
        {
            Commit(("stable/a.cs", "var a = 1;\n"), ("stable/b.cs", "var b = 1;\n"), ("moving/c.cs", "var c = 1;\n")),
            Commit(("moving/c.cs", "var c = 2;\n")),
            Commit(("moving/c.cs", "var c = 3;\n"))
        };

        foreach (var s in shas)
        {
            await sut.CountLinesInCommitAsync(_repoPath, s, [".cs"]);
        }

        counter.ReadsOf("var a = 1;\n").Should().Be(1);
        counter.ReadsOf("var b = 1;\n").Should().Be(1);
        counter.TotalReads.Should().Be(5);
    }

    private sealed class CountingLineCounter(string extension) : SourceLineCounter(extension)
    {
        private readonly List<string> _read = [];

        public int TotalReads => _read.Count;

        public int ReadsOf(string content) => _read.Count(c => c == content);

        public override async Task<int> CountLinesAsync(Stream stream)
        {
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            _read.Add(content);
            return content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }
    }

    public void Dispose()
    {
        _sut.Dispose(); // ends its git cat-file process, which holds the pack files open on Windows
        try
        {
            // Git marks object files read-only, which blocks a plain recursive delete on Windows.
            foreach (var file in Directory.EnumerateFiles(_repoPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(_repoPath, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test run over.
        }
    }
}
