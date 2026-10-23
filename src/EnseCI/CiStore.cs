using System.Text.Json;

namespace EnseCI;

public sealed class CiConfig
{
    public string RepoUrl { get; set; } = "";
    public string SolutionFile { get; set; } = "";
    public string ProjectFile { get; set; } = "";
    public string PackagesSource { get; set; } = "";
    public string Msbuild { get; set; } = "";
    public string IisExpress { get; set; } = "";
    public List<string> ExcludeFromPublish { get; set; } = [];
    public List<string> IncludeInPublish { get; set; } = [];
    public List<string> Preserve { get; set; } = [];
    public int HealthTimeoutSec { get; set; } = 240;
    public int KeepBackups { get; set; } = 20;
    /// <summary>Developer's working repository: its branches can be deployed before pushing.</summary>
    public string LocalRepoPath { get; set; } = "";
    /// <summary>How often the remote is checked (git ls-remote).</summary>
    public int PollIntervalSec { get; set; } = 20;
    /// <summary>Auto-deploy waits this long without new commits, so a burst of commits builds once.</summary>
    public int QuietSec { get; set; } = 15;
    public string ListenUrl { get; set; } = "http://localhost:5080";
    public List<EnvConfig> Environments { get; set; } = [];
}

public sealed class EnvConfig
{
    public string Name { get; set; } = "";
    public string Branch { get; set; } = "";
    /// <summary>remote (origin) or local (the developer's repository).</summary>
    public string Source { get; set; } = BranchSource.Remote;
    public int Port { get; set; }
    public string Configuration { get; set; } = "Desenvolvimento";
    public string HealthPath { get; set; } = "/";
    public bool AutoDeploy { get; set; } = true;
}

/// <summary>Version info the engine writes into each deployed site (deploy-info.json).</summary>
public sealed record DeployInfo(string Commit, string ShortCommit, string? Subject, string? Author, string? Branch, string? Configuration, string? DeployedAt);

public sealed record BackupInfo(string Name, string Commit, string Reason, DateTime Date, double SizeMb);

/// <summary>ci.json is shared with the PowerShell engine: the service is its only writer.</summary>
public sealed class CiStore(string root)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly object _lock = new();

    public string Root { get; } = root;
    public string EngineDir => Path.Combine(Root, "engine");
    public string MirrorDir => Path.Combine(Root, "mirror.git");
    public string RunsDir => Path.Combine(Root, "runs");
    string FilePath => Path.Combine(Root, "ci.json");

    public CiConfig Load()
    {
        lock (_lock) return Read();
    }

    public CiConfig Update(Action<CiConfig> change)
    {
        lock (_lock)
        {
            var cfg = Read();
            change(cfg);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
            File.Move(tmp, FilePath, overwrite: true);
            return cfg;
        }
    }

    CiConfig Read() => JsonSerializer.Deserialize<CiConfig>(File.ReadAllText(FilePath), Json)
        ?? throw new InvalidOperationException($"{FilePath} esta vacio.");

    public string SiteDir(string env) => Path.Combine(Root, "envs", env, "site");

    public DeployInfo? GetDeployInfo(string env)
    {
        var file = Path.Combine(SiteDir(env), "deploy-info.json");
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<DeployInfo>(File.ReadAllText(file), Json); }
        catch (Exception) { return null; }
    }

    public List<BackupInfo> GetBackups(string env)
    {
        var dir = Path.Combine(Root, "envs", env, "backups");
        if (!Directory.Exists(dir)) return [];
        var pattern = new System.Text.RegularExpressions.Regex(@"_(?<date>\d{8}-\d{6})_(?<commit>[0-9a-f]{7,40}|unknown)_(?<reason>[a-z]+)\.zip$");
        return new DirectoryInfo(dir).GetFiles("*.zip")
            .Select(f => (f, m: pattern.Match(f.Name)))
            .Where(x => x.m.Success)
            .Select(x => new BackupInfo(
                x.f.Name,
                x.m.Groups["commit"].Value,
                x.m.Groups["reason"].Value,
                DateTime.ParseExact(x.m.Groups["date"].Value, "yyyyMMdd-HHmmss", null),
                Math.Round(x.f.Length / 1048576.0, 1)))
            .OrderByDescending(b => b.Date)
            .ToList();
    }
}
