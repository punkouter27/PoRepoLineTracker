using System.Diagnostics;
using System.Text;

namespace PoRepoLineTracker.API.Analysis;

/// <summary>One entry of a git tree: a file (blob), a directory (tree) or a submodule (commit).</summary>
public sealed record GitTreeEntry(string Name, string Sha, bool IsTree, bool IsBlob);

/// <summary>A tree read from the object store: its own id and its entries.</summary>
public sealed record GitTree(string Sha, IReadOnlyList<GitTreeEntry> Entries);

/// <summary>
/// Reads trees and blobs from a repository's object store through one long-lived
/// <c>git cat-file --batch</c> process.
///
/// <para><b>Why not LibGit2Sharp.</b> It was the last native dependency, and the one that crashes
/// with SIGABRT on App Service Linux (clone and fetch already moved to the CLI for that reason).
/// One process per reader keeps reads at a pipe round trip, not a process start, which matters
/// because history analysis reads a tree or blob thousands of times.</para>
///
/// <para>Not thread-safe: one caller at a time, like the LibGit2Sharp handle it replaces.</para>
/// </summary>
public sealed class GitObjectReader : IDisposable
{
    private readonly Process _process;
    private readonly Stream _output;
    private readonly StreamWriter _input;

    public GitObjectReader(string repositoryPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-c", "core.longpaths=true", "cat-file", "--batch" })
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git cat-file");
        _output = _process.StandardOutput.BaseStream;
        _input = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    /// <summary>A git repository, cloned with or without a working tree. A file check, not a process: it runs once per commit.</summary>
    public static bool IsRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git"))
        || (File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects")));

    /// <summary>
    /// The tree a revision names: a commit SHA resolves to its root tree (<c>sha^{tree}</c>),
    /// a tree SHA to itself. Null when the object does not exist.
    /// </summary>
    public GitTree? ReadTree(string revision)
    {
        var (sha, type, content) = Read($"{revision}^{{tree}}");
        if (content is null || type != "tree") return null;

        // Raw tree format: "<octal mode> <name>\0<binary id>", repeated. The id is 20 bytes for
        // SHA-1 repositories and 32 for SHA-256 ones; the header's hex id says which.
        var idLength = sha.Length / 2;
        var entries = new List<GitTreeEntry>();
        var i = 0;
        while (i < content.Length)
        {
            var space = Array.IndexOf(content, (byte)' ', i);
            var nul = Array.IndexOf(content, (byte)0, space);
            var mode = Encoding.ASCII.GetString(content, i, space - i);
            var name = Encoding.UTF8.GetString(content, space + 1, nul - space - 1);
            var id = Convert.ToHexStringLower(content, nul + 1, idLength);
            entries.Add(new GitTreeEntry(name, id, IsTree: mode == "40000", IsBlob: mode.StartsWith('1') && mode != "160000"));
            i = nul + 1 + idLength;
        }
        return new GitTree(sha, entries);
    }

    /// <summary>A blob's bytes, or null when it does not exist or is not a blob.</summary>
    public byte[]? ReadBlob(string sha)
    {
        var (_, type, content) = Read(sha);
        return type == "blob" ? content : null;
    }

    /// <summary>One request/response: "<id> <type> <size>\n<content>\n", or "<input> missing\n".</summary>
    private (string Sha, string Type, byte[]? Content) Read(string request)
    {
        _input.WriteLine(request);

        var header = ReadLine();
        var parts = header.Split(' ');
        if (parts.Length != 3 || !long.TryParse(parts[2], out var size))
            return (string.Empty, string.Empty, null); // "missing" or "ambiguous"

        var content = new byte[size];
        _output.ReadExactly(content);
        ReadLine(); // the newline that closes every object
        return (parts[0], parts[1], content);
    }

    private string ReadLine()
    {
        var bytes = new List<byte>(64);
        int b;
        while ((b = _output.ReadByte()) != '\n')
        {
            if (b < 0) throw new InvalidOperationException("git cat-file exited unexpectedly");
            bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public void Dispose()
    {
        try
        {
            _input.Dispose(); // EOF on stdin ends cat-file
            if (!_process.WaitForExit(TimeSpan.FromSeconds(5))) _process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        _process.Dispose();
    }
}
