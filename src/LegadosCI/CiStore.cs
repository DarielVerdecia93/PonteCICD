using System.Text.Json;

namespace LegadosCI;

public sealed class CiConfig
{
    public string RepoUrl { get; set; } = "";
    public string SolutionFile { get; set; } = "";
    public string ProjectFile { get; set; } = "";
    public string PackagesSource { get; set; } = "";
    public string Msbuild { get; set; } = "";
    public List<string> ExcludeFromPublish { get; set; } = [];
    public List<string> IncludeInPublish { get; set; } = [];
    public List<string> Preserve { get; set; } = [];
    public int HealthTimeoutSec { get; set; } = 240;
    public int KeepBackups { get; set; } = 20;
    /// <summary>How many versioned releases to keep per environment (the engine prunes older ones).</summary>
    public int KeepReleases { get; set; } = 5;
    /// <summary>Developer's working repository: its branches can be deployed before pushing.</summary>
    public string LocalRepoPath { get; set; } = "";
    /// <summary>How often the remote is checked (git ls-remote).</summary>
    public int PollIntervalSec { get; set; } = 20;
    /// <summary>Auto-deploy waits this long without new commits, so a burst of commits builds once.</summary>
    public int QuietSec { get; set; } = 15;
    public string ListenUrl { get; set; } = "http://localhost:5080";
    /// <summary>Carpeta de compilacion del engine (vacio = raiz\work). Conviene una ruta corta: el proyecto web genera rutas de mas de 260 caracteres.</summary>
    public string WorkDir { get; set; } = "";
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
    /// <summary>Optional root for this env's files. Empty = default &lt;Root&gt;\envs\&lt;name&gt;.</summary>
    public string SitePath { get; set; } = "";
    /// <summary>IIS website name. Empty resolves to 'LegadosCI-&lt;name&gt;'; fixed after creation.</summary>
    public string SiteName { get; set; } = "";
    /// <summary>IIS application pool name. Empty resolves to 'LegadosCI-&lt;name&gt;'; fixed after creation.</summary>
    public string AppPoolName { get; set; } = "";
    /// <summary>Optional http host header for the IIS binding. Empty = bind on the port only.</summary>
    public string HostHeader { get; set; } = "";
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

    CiConfig Read()
    {
        // Primera ejecucion (o carpeta recien creada): arranca con una config por defecto
        // en lugar de reventar, y la deja escrita para que el usuario la edite.
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(Root);
            var fresh = new CiConfig();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(fresh, Json));
            return fresh;
        }
        return JsonSerializer.Deserialize<CiConfig>(File.ReadAllText(FilePath), Json)
            ?? throw new InvalidOperationException($"{FilePath} esta vacio.");
    }

    /// <summary>Legacy (pre-IIS) site folder, kept only as the fallback when there is no release.</summary>
    public string SiteDir(string env) => Path.Combine(Root, "envs", env, "site");

    /// <summary>Base folder of an environment: EnvConfig.SitePath when set, else the default
    /// C:\inetpub\wwwroot\deploys\&lt;name&gt; (new envs always store SitePath explicitly).</summary>
    public string EnvBase(EnvConfig env) =>
        string.IsNullOrWhiteSpace(env.SitePath) ? DefaultSitePath(env.Name) : env.SitePath;

    /// <summary>Default IIS site/pool name for an env when the stored value is empty.</summary>
    public static string DefaultSiteName(string env) => $"LegadosCI-{env}";

    /// <summary>
    /// Default base folder for a new environment, inside the IIS tree under a shared "deploys" folder:
    /// C:\inetpub\wwwroot\deploys\&lt;env&gt;. Used when the user does not supply a SitePath, so the value is
    /// stored explicitly in ci.json and both the service and the engine resolve the same path.
    /// </summary>
    public static string DefaultSitePath(string env)
    {
        var wwwroot = Environment.ExpandEnvironmentVariables(@"%SystemDrive%\inetpub\wwwroot");
        return Path.Combine(wwwroot, "deploys", env);
    }

    /// <summary>
    /// Folder that IIS currently serves for <paramref name="env"/>, following the current.txt contract
    /// shared with the engine: current.txt is ASCII, no BOM, a single line, no trailing newline, and is
    /// trimmed on read. When it names an existing releases\&lt;id&gt; folder that is the active release;
    /// otherwise (absent/empty/missing dir) we fall back to the legacy &lt;base&gt;\site folder.
    /// </summary>
    public string ActiveReleaseDir(string env)
    {
        var cfg = Load().Environments.FirstOrDefault(e => e.Name == env);
        var baseDir = cfg is null ? DefaultSitePath(env) : EnvBase(cfg);
        var legacy = Path.Combine(baseDir, "site");
        var current = Path.Combine(baseDir, "current.txt");
        if (!File.Exists(current)) return legacy;
        string id;
        try { id = File.ReadAllText(current).Trim(); }
        catch (Exception) { return legacy; }
        if (string.IsNullOrEmpty(id)) return legacy;
        var release = Path.Combine(baseDir, "releases", id);
        return Directory.Exists(release) ? release : legacy;
    }

    public DeployInfo? GetDeployInfo(string env)
    {
        var file = Path.Combine(ActiveReleaseDir(env), "deploy-info.json");
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
