using System.Diagnostics;

namespace PonteCICD;

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

    IReadOnlyList<BranchInfo> _branches = [];

    /// <summary>Whether the repository access is configured (URL + token). Everything below depends on it.</summary>
    public bool HasAccess => GitAuth.HasCredentials(store.Load());

    /// <summary>The branches read from the repository, only while the access (URL + token) exists.</summary>
    public IReadOnlyList<BranchInfo> Branches => HasAccess ? _branches : [];
    public DateTimeOffset? LastCheck { get; private set; }
    public DateTimeOffset? LastChange { get; private set; }
    public string? LastError { get; private set; }

    public BranchInfo? Find(string branch, string source) =>
        Branches.FirstOrDefault(b => b.Name == branch && b.Source == source);

    public static string RefOf(string branch, string source) =>
        source == BranchSource.Local ? $"refs/local/{branch}" : $"refs/heads/{branch}";

    /// <summary>Expands ${VAR} placeholders from process environment variables, so the repo URL can
    /// carry a token stored in an env var (e.g. https://${AZURE_DEVOPS_PAT}@dev.azure.com/...) instead
    /// of in plaintext in ci.json. An undefined variable is left as-is.</summary>
    public static string ExpandEnvVars(string? text) =>
        string.IsNullOrEmpty(text) ? (text ?? "") :
        System.Text.RegularExpressions.Regex.Replace(text, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

    /// <summary>Checks the remote with ls-remote (cheap) and fetches only if a branch moved. Returns true if anything changed.</summary>
    public async Task<bool> SyncRemoteAsync(bool force, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var cfg = store.Load();
            // Sem URL e token nao se mantem nada do repositorio: nem as branches em memoria nem o espelho em disco.
            if (!GitAuth.HasCredentials(cfg))
            {
                WipeLocked();
                return Fail("Acesso ao repositorio nao configurado: indique o URL e o token em «Acesso ao repositorio» no painel.");
            }
            var repoUrl = ExpandEnvVars(cfg.RepoUrl);
            if (!Directory.Exists(store.MirrorDir))
            {
                var (code, output) = await Git.RunAsync(["clone", "--bare", "--quiet", repoUrl, store.MirrorDir], null, ct);
                if (code != 0) return Fail($"git clone falhou: {output.Trim()}");
                force = true;
            }
            else
            {
                // origin debe llevar la URL expandida (con token) para que ls-remote/fetch autentiquen.
                await Git.RunAsync(["remote", "set-url", "origin", repoUrl], store.MirrorDir, ct);
            }

            if (!force)
            {
                var (code, output) = await Git.RunAsync(["ls-remote", "--heads", "origin"], store.MirrorDir, ct);
                if (code != 0) return Fail($"Nao foi possivel consultar o remoto: {output.Trim()}");
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
            if (fetch.Code != 0) return Fail($"git fetch falhou: {fetch.Output.Trim()}");
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
        if (!HasAccess || string.IsNullOrWhiteSpace(localRepo) || !Directory.Exists(store.MirrorDir)) return false;

        await _lock.WaitAsync(ct);
        try
        {
            var (code, output) = await Git.RunAsync(["fetch", "--prune", "--quiet", localRepo, "+refs/heads/*:refs/local/*"], store.MirrorDir, ct);
            if (code != 0) return Fail($"Nao foi possivel ler o repo local: {output.Trim()}");
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

    /// <summary>Build configurations declared in the web project (csproj) of a branch, e.g. Desenvolvimento, Qualidade...</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DetectedProject?> _detected = new();

    /// <summary>Solution and web project of a branch, found in the repository itself (cached per commit).</summary>
    public async Task<DetectedProject?> DetectProjectAsync(string? branch, string? source, CancellationToken ct)
    {
        var b = branch is null ? Branches.FirstOrDefault() : Find(branch, source ?? BranchSource.Remote);
        if (b is null) return null;
        if (_detected.TryGetValue(b.Commit, out var cached)) return cached;
        var found = await ProjectDetector.DetectAsync(store.MirrorDir, b.Commit, ct);
        if (_detected.Count > 200) _detected.Clear();
        _detected[b.Commit] = found;
        return found;
    }

    public async Task<List<string>> ConfigurationsAsync(string? branch, string? source, CancellationToken ct)
    {
        var b = branch is null ? Branches.FirstOrDefault() : Find(branch, source ?? BranchSource.Remote);
        var projectFile = store.Load().ProjectFile.Replace('\\', '/');
        // Sem projeto configurado, procura-o no proprio branch (nao e preciso indicar nada nem ter o codigo descarregado).
        if (b is not null && string.IsNullOrWhiteSpace(projectFile))
            projectFile = (await DetectProjectAsync(b.Name, b.Source, ct))?.Project?.Replace('\\', '/') ?? "";
        if (b is null || string.IsNullOrWhiteSpace(projectFile)) return [];
        var (code, output) = await Git.RunAsync(["show", $"{b.Commit}:{projectFile}"], store.MirrorDir, ct);
        if (code != 0) return [];
        // <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Qualidade|AnyCPU' ">
        return System.Text.RegularExpressions.Regex
            .Matches(output, @"'\$\(Configuration\)\|\$\(Platform\)'\s*==\s*'([^'|]+)\|")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    async Task<bool> ReloadAsync(CancellationToken ct)
    {
        var (code, output) = await Git.RunAsync(
            ["for-each-ref", "refs/heads", "refs/local", "--sort=-committerdate", $"--format=%(refname)%09{CommitFormat}"],
            store.MirrorDir, ct);
        if (code != 0) return Fail($"Nao foi possivel ler os branches: {output.Trim()}");

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
        _branches = branches;
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
        var isNew = LastError != error;
        if (isNew) log.LogWarning("{Error}", error);
        LastError = error;
        if (isNew) notifier.Changed();
        return false;
    }

    /// <summary>Forgets everything read from the repository: in-memory branches and the on-disk mirror (it holds the whole history).</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { WipeLocked(); }
        finally { _lock.Release(); }
        notifier.Changed();
    }

    // Caller holds _lock.
    void WipeLocked()
    {
        _branches = [];
        _detected.Clear();
        lock (_pendingCache) _pendingCache.Clear();
        LastChange = null;
        var dir = store.MirrorDir;
        if (!Directory.Exists(dir)) return;
        try
        {
            // Git marks its object files read-only; Directory.Delete would fail on them.
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
            log.LogInformation("Espelho do repositorio apagado (sem URL/token configurados).");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nao foi possivel apagar o espelho do repositorio ({Dir}); as branches ja nao se mostram.", dir);
        }
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
    public static async Task<(int Code, string Output)> RunAsync(string[] args, string? gitDir, CancellationToken ct, GitSecret? secret = null, string? url = null)
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
        GitAuth.Apply(psi, url, secret);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await stdout) + (await stderr));
    }
}
