using System.Diagnostics;

namespace EnseCI;

public static class BranchSource
{
    public const string Remote = "remote";
    public const string Local = "local";
}

public sealed record BranchInfo(string Name, string Source, string Commit, string Subject, string Author, DateTimeOffset Date);

public sealed record CommitInfo(string Commit, string Subject, string Author, DateTimeOffset Date);

/// <summary>
/// Bare repository with every branch the CI can deploy:
///   refs/heads/*  branches of the remote (origin)
///   refs/local/*  branches of the developer's local repository (commits not pushed yet)
/// Only this class writes to it, and never concurrently.
/// </summary>
public sealed class Mirror(CiStore store, Notifier notifier, ILogger<Mirror> log)
{
    const string CommitFormat = "%(objectname)%09%(committerdate:iso-strict)%09%(authorname)%09%(subject)";

    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Dictionary<string, List<CommitInfo>> _pendingCache = [];

    public IReadOnlyList<BranchInfo> Branches { get; private set; } = [];
    public DateTimeOffset? LastCheck { get; private set; }
    public DateTimeOffset? LastChange { get; private set; }
    public string? LastError { get; private set; }

    public BranchInfo? Find(string branch, string source) =>
        Branches.FirstOrDefault(b => b.Name == branch && b.Source == source);

    public static string RefOf(string branch, string source) =>
        source == BranchSource.Local ? $"refs/local/{branch}" : $"refs/heads/{branch}";

    /// <summary>Checks the remote with ls-remote (cheap) and fetches only if a branch moved. Returns true if anything changed.</summary>
    public async Task<bool> SyncRemoteAsync(bool force, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = store.Load();
            if (!Directory.Exists(store.MirrorDir))
            {
                var (code, output) = await Git.RunAsync(["clone", "--bare", "--quiet", cfg.RepoUrl, store.MirrorDir], null, ct);
                if (code != 0) return Fail($"git clone fallo: {output.Trim()}");
                force = true;
            }

            if (!force)
            {
                var (code, output) = await Git.RunAsync(["ls-remote", "--heads", "origin"], store.MirrorDir, ct);
                if (code != 0) return Fail($"No se pudo consultar el remoto: {output.Trim()}");
                var remote = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.TrimEnd('\r').Split('\t'))
                    .Where(p => p.Length == 2 && p[1].StartsWith("refs/heads/"))
                    .ToDictionary(p => p[1]["refs/heads/".Length..], p => p[0]);
                var known = Branches.Where(b => b.Source == BranchSource.Remote).ToDictionary(b => b.Name, b => b.Commit);
                if (remote.Count == known.Count && remote.All(kv => known.GetValueOrDefault(kv.Key) == kv.Value))
                {
                    Ok(changed: false);
                    return false;
                }
            }

            var fetch = await Git.RunAsync(["fetch", "--prune", "--quiet", "origin", "+refs/heads/*:refs/heads/*"], store.MirrorDir, ct);
            if (fetch.Code != 0) return Fail($"git fetch fallo: {fetch.Output.Trim()}");
            return await ReloadAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Copies the branches of the local repository into refs/local/*. Returns true if anything changed.</summary>
    public async Task<bool> SyncLocalAsync(CancellationToken ct)
    {
        var localRepo = store.Load().LocalRepoPath;
        if (string.IsNullOrWhiteSpace(localRepo) || !Directory.Exists(store.MirrorDir)) return false;

        await _lock.WaitAsync(ct);
        try
        {
            var (code, output) = await Git.RunAsync(["fetch", "--prune", "--quiet", localRepo, "+refs/heads/*:refs/local/*"], store.MirrorDir, ct);
            if (code != 0) return Fail($"No se pudo leer el repo local: {output.Trim()}");
            return await ReloadAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Commits in <paramref name="to"/> that are not in <paramref name="from"/> (newest first).</summary>
    public async Task<List<CommitInfo>> PendingAsync(string from, string to, CancellationToken ct)
    {
        var key = from + ".." + to;
        lock (_pendingCache)
            if (_pendingCache.TryGetValue(key, out var cached)) return cached;

        var (code, output) = await Git.RunAsync(
            ["log", "-n", "30", "--format=%H%x09%cI%x09%an%x09%s", key], store.MirrorDir, ct);
        var list = code == 0 ? ParseCommits(output) : [];
        lock (_pendingCache)
        {
            if (_pendingCache.Count > 200) _pendingCache.Clear();
            _pendingCache[key] = list;
        }
        return list;
    }

    async Task<bool> ReloadAsync(CancellationToken ct)
    {
        var (code, output) = await Git.RunAsync(
            ["for-each-ref", "refs/heads", "refs/local", "--sort=-committerdate", $"--format=%(refname)%09{CommitFormat}"],
            store.MirrorDir, ct);
        if (code != 0) return Fail($"No se pudieron leer las ramas: {output.Trim()}");

        var branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 5))
            .Where(p => p.Length == 5)
            .Select(p =>
            {
                var local = p[0].StartsWith("refs/local/");
                var name = p[0][(local ? "refs/local/" : "refs/heads/").Length..];
                return new BranchInfo(name, local ? BranchSource.Local : BranchSource.Remote, p[1], p[4], p[3], DateTimeOffset.Parse(p[2]));
            })
            .ToList();

        var changed = branches.Count != Branches.Count
                      || branches.Any(b => Find(b.Name, b.Source)?.Commit != b.Commit);
        Branches = branches;
        Ok(changed);
        return changed;
    }

    void Ok(bool changed)
    {
        LastCheck = DateTimeOffset.Now;
        if (changed) LastChange = LastCheck;
        var hadError = LastError is not null;
        LastError = null;
        if (changed || hadError) notifier.Changed();
    }

    bool Fail(string error)
    {
        if (LastError != error) log.LogWarning("{Error}", error);
        LastError = error;
        notifier.Changed();
        return false;
    }

    static List<CommitInfo> ParseCommits(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t', 4))
            .Where(p => p.Length == 4)
            .Select(p => new CommitInfo(p[0], p[3], p[2], DateTimeOffset.Parse(p[1])))
            .ToList();
}

public static class Git
{
    public static async Task<(int Code, string Output)> RunAsync(string[] args, string? gitDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (gitDir is not null) { psi.ArgumentList.Add("--git-dir"); psi.ArgumentList.Add(gitDir); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await stdout) + (await stderr));
    }
}
