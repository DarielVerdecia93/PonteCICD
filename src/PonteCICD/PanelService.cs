using System.Text.RegularExpressions;

namespace PonteCICD;

/// <summary>What the panel shows of one environment (/api/state serializes it in camelCase).</summary>
public sealed record EnvState(
    string Name, string Branch, string Source, int Port, string Configuration, bool AutoDeploy,
    string SitePath, string SiteName, string AppPoolName, string HostHeader,
    string Url, string LocalUrl, bool Running, DeployInfo? Deployed, BranchInfo? Head, List<CommitInfo> Pending,
    DateTimeOffset? AutoDeployAt, Job? Job, List<BackupInfo> Backups);

public sealed record PanelState(
    DateTimeOffset? LastCheck, DateTimeOffset? LastChange, string? LastError, int PollIntervalSec, int QuietSec,
    string LocalRepoPath, bool IisAvailable, string? IisError, bool ProjectConfigured, List<EnvState> Environments);

public sealed record EnvRequest(
    string? Name, string? Branch, string? Source, int? Port, string? Configuration, bool? AutoDeploy,
    string? SitePath, string? SiteName, string? AppPoolName, string? HostHeader, string? CopyFrom);

public sealed record SettingsRequest(int? PollIntervalSec);

public sealed record PublishRulesRequest(List<string?>? All, Dictionary<string, List<string?>>? Environments);

public sealed record PublishRulesView(List<string> All, Dictionary<string, List<string>> Environments);

public enum OpError { None, NotFound, Invalid, Conflict }

/// <summary>Result of a panel operation: the value, or why it was refused (the API turns it into 404/400/409).</summary>
public sealed record OpResult<T>(T? Value, OpError Error = OpError.None, string? Message = null)
{
    public bool Ok => Error == OpError.None;
}

public static class Op
{
    public static OpResult<T> Ok<T>(T value) => new(value);
    public static OpResult<T> NotFound<T>() => new(default, OpError.NotFound);
    public static OpResult<T> Invalid<T>(string? message) => new(default, OpError.Invalid, message);
    public static OpResult<T> Conflict<T>(string? message) => new(default, OpError.Conflict, message);
}

/// <summary>
/// Estado dos ambientes e operacoes do painel. Partilhado pela API (painel antigo, /classico/) e pelo painel Blazor,
/// para que os dois apliquem as mesmas regras.
/// </summary>
public sealed class PanelService(CiStore store, Mirror mirror, JobQueue queue, Poller poller, IisStateProbe probe, Notifier notifier)
{
    public async Task<PanelState> GetStateAsync(CancellationToken ct)
    {
        var cfg = store.Load();
        var envs = new List<EnvState>();
        foreach (var e in cfg.Environments)
        {
            var head = mirror.Find(e.Branch, e.Source);
            var deployed = store.GetDeployInfo(e.Name);
            var pending = head is not null && deployed is not null && deployed.Commit != head.Commit
                ? await mirror.PendingAsync(deployed.Commit, head.Commit, ct)
                : [];
            envs.Add(new EnvState(
                e.Name, e.Branch, e.Source, e.Port, e.Configuration, e.AutoDeploy,
                e.SitePath, e.SiteName, e.AppPoolName, e.HostHeader,
                // Com nome DNS o acesso publico e http://<nome>/ (porto 80, sem porto visivel); LocalUrl e o do porto do ambiente.
                Url: string.IsNullOrWhiteSpace(e.HostHeader) ? $"http://localhost:{e.Port}/" : $"http://{e.HostHeader}/",
                LocalUrl: $"http://localhost:{e.Port}/",
                Running: probe.Running(e.Name),
                Deployed: deployed,
                Head: head,
                Pending: pending,
                AutoDeployAt: poller.AutoDeployAt(e.Name),
                Job: queue.Pending(e.Name),
                Backups: store.GetBackups(e.Name)));
        }
        return new PanelState(
            mirror.LastCheck,
            mirror.LastChange,
            mirror.LastError is null ? null : GitAuth.Friendly(mirror.LastError),
            cfg.PollIntervalSec,
            cfg.QuietSec,
            cfg.LocalRepoPath,
            probe.IisAvailable,
            probe.Error,
            GitAuth.HasCredentials(cfg),
            envs);
    }

    /// <summary>
    /// Queues deploy, rollback, start, stop, restart or purge for an environment. arg: commit to deploy (default: branch
    /// tip) or backup to restore (default: latest).
    /// </summary>
    public OpResult<Job> Enqueue(string env, JobKind kind, string? arg = null)
    {
        if (!store.Load().Environments.Any(e => e.Name == env)) return Op.NotFound<Job>();
        // Eliminar nao espera pela operacao em curso: fica na fila a seguir a ela.
        if (kind != JobKind.Purge)
        {
            // Implantar precisa de ler o repositorio: sem URL e token nao ha acesso (parar, iniciar e rollback nao precisam).
            if (kind == JobKind.Deploy && !mirror.HasAccess)
                return Op.Conflict<Job>("Acesso ao repositorio nao configurado: indique o URL e o token em «Acesso ao repositorio» antes de implantar.");
            if (queue.HasPending(env)) return Op.Conflict<Job>($"Já existe uma operação em curso para {env}.");
        }
        return Op.Ok(queue.Enqueue(env, kind, "manual", arg));
    }

    /// <summary>Turns auto-deploy on or off. False when the environment does not exist.</summary>
    public bool SetAutoDeploy(string env, bool on)
    {
        var found = false;
        store.Update(c =>
        {
            var e = c.Environments.FirstOrDefault(x => x.Name == env);
            if (e is null) return;
            e.AutoDeploy = on;
            found = true;
        });
        if (found) notifier.Changed();
        return found;
    }

    /// <summary>Creates an environment (from scratch, or duplicating CopyFrom) and queues its first deploy.</summary>
    public async Task<OpResult<EnvConfig>> CreateEnvAsync(EnvRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim().ToLowerInvariant();
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,39}$"))
            return Op.Invalid<EnvConfig>("Nome invalido: minusculas, numeros e hifens (max. 40).");

        var cfg0 = store.Load();
        // Duplicate (copyFrom): seed from a template for the fields the request omits, but never copy the
        // IIS identity (SiteName/AppPoolName) nor the host-specific SitePath/HostHeader/Port.
        EnvConfig? template = null;
        if (!string.IsNullOrWhiteSpace(req.CopyFrom))
        {
            template = cfg0.Environments.FirstOrDefault(e => e.Name == req.CopyFrom);
            if (template is null) return Op.Invalid<EnvConfig>($"O ambiente de origem '{req.CopyFrom}' não existe.");
        }

        var branch = req.Branch ?? template?.Branch;
        var source = req.Source ?? template?.Source ?? BranchSource.Remote;
        if (string.IsNullOrWhiteSpace(branch) || mirror.Find(branch, source) is null)
            return Op.Invalid<EnvConfig>($"O branch '{branch}' nao existe ({source}).");

        // Solucao e projeto web: se nao estao no ci.json procuram-se no proprio branch (nao e preciso indicar nada).
        if (string.IsNullOrWhiteSpace(cfg0.ProjectFile))
        {
            var found = await mirror.DetectProjectAsync(branch, source, ct);
            if (found?.Project is null)
                return Op.Invalid<EnvConfig>($"Nao foi encontrado nenhum projeto web (.csproj de ASP.NET) no branch '{branch}'. " +
                    "Se o projeto tem uma estrutura invulgar, indique 'projectFile' (e 'solutionFile') no ci.json.");
            store.Update(c =>
            {
                if (string.IsNullOrWhiteSpace(c.ProjectFile))
                {
                    c.ProjectFile = found.Project;
                    if (string.IsNullOrWhiteSpace(c.SolutionFile)) c.SolutionFile = found.Solution ?? "";
                }
            });
        }

        // Sin ruta explicita se usa la convencion por defecto C:\inetpub\wwwroot\deploys\<env> (dentro del
        // arbol de IIS), y se guarda en ci.json para que servicio y engine resuelvan la misma carpeta.
        var sitePath = (req.SitePath ?? "").Trim();
        if (sitePath.Length == 0) sitePath = CiStore.DefaultSitePath(name);
        var (okPath, pathError) = EnvValidation.ValidateSitePath(sitePath, cfg0.Environments);
        if (!okPath) return Op.Invalid<EnvConfig>(pathError);

        var (okHost, hostHeader, hostError) = EnvValidation.ValidateHostName(req.HostHeader);
        if (!okHost) return Op.Invalid<EnvConfig>(hostError);

        string? error = null;
        var cfg = store.Update(c =>
        {
            if (c.Environments.Any(e => e.Name == name)) { error = $"Ja existe o ambiente '{name}'."; return; }
            var (okUnique, uniqueError) = EnvValidation.ValidateHostUnique(hostHeader, c.Environments);
            if (!okUnique) { error = uniqueError; return; }
            var port = req.Port ?? Enumerable.Range(8090, 200).First(p => c.Environments.All(e => e.Port != p) && PortProbe.IsFree(p));
            if (c.Environments.Any(e => e.Port == port)) { error = $"A porta {port} já está a ser usada por outro ambiente."; return; }

            var siteName = CiStore.DefaultSiteName(name);
            if (c.Environments.Any(e => (string.IsNullOrWhiteSpace(e.SiteName) ? CiStore.DefaultSiteName(e.Name) : e.SiteName) == siteName))
            { error = $"O site IIS '{siteName}' já está a ser usado por outro ambiente."; return; }
            if (c.Environments.Any(e => (string.IsNullOrWhiteSpace(e.AppPoolName) ? CiStore.DefaultSiteName(e.Name) : e.AppPoolName) == siteName))
            { error = $"O pool IIS '{siteName}' já está a ser usado por outro ambiente."; return; }

            c.Environments.Add(new EnvConfig
            {
                Name = name,
                Branch = branch,
                Source = source,
                Port = port,
                Configuration = !string.IsNullOrWhiteSpace(req.Configuration) ? req.Configuration
                    : !string.IsNullOrWhiteSpace(template?.Configuration) ? template!.Configuration
                    : "Desenvolvimento",
                HealthPath = !string.IsNullOrWhiteSpace(template?.HealthPath) ? template!.HealthPath : "/",
                AutoDeploy = req.AutoDeploy ?? template?.AutoDeploy ?? true,
                SitePath = sitePath,
                HostHeader = hostHeader,
                ExcludeFromPublish = [.. template?.ExcludeFromPublish ?? []],
            });
        });
        if (error is not null) return Op.Conflict<EnvConfig>(error);
        if (template is not null)
        {
            // The environment's own config transform (connection strings, etc.) travels with the copy.
            var src = Path.Combine(store.Root, "envs", template.Name, "Web.local.config");
            var dstDir = Path.Combine(store.Root, "envs", name);
            if (File.Exists(src))
            {
                Directory.CreateDirectory(dstDir);
                File.Copy(src, Path.Combine(dstDir, "Web.local.config"), overwrite: true);
            }
        }
        var env = cfg.Environments.First(e => e.Name == name);
        queue.Enqueue(name, JobKind.Deploy, "manual");
        return Op.Ok(env);
    }

    /// <summary>Changes branch, configuration, folder, port, host name or auto-deploy of an environment (fields left null stay).</summary>
    public OpResult<EnvConfig> UpdateEnv(string name, EnvRequest req)
    {
        // The IIS site/pool name is fixed once the env exists (Decision 7): reject any attempt to change it.
        if (req.SiteName is not null || req.AppPoolName is not null)
        {
            var current = store.Load().Environments.FirstOrDefault(e => e.Name == name);
            if (current is null) return Op.NotFound<EnvConfig>();
            var curSite = string.IsNullOrWhiteSpace(current.SiteName) ? CiStore.DefaultSiteName(current.Name) : current.SiteName;
            var curPool = string.IsNullOrWhiteSpace(current.AppPoolName) ? CiStore.DefaultSiteName(current.Name) : current.AppPoolName;
            if ((req.SiteName is not null && req.SiteName.Trim() != curSite) ||
                (req.AppPoolName is not null && req.AppPoolName.Trim() != curPool))
                return Op.Invalid<EnvConfig>("O nome do site/pool do IIS não pode ser alterado depois de criado.");
        }

        EnvConfig? env = null;
        string? error = null;
        store.Update(c =>
        {
            env = c.Environments.FirstOrDefault(e => e.Name == name);
            if (env is null) return;
            var branch = req.Branch ?? env.Branch;
            var source = req.Source ?? env.Source;
            if (mirror.Find(branch, source) is null) { error = $"O branch '{branch}' nao existe ({source})."; return; }
            if (req.SitePath is not null)
            {
                var sitePath = req.SitePath.Trim();
                var (okPath, pathError) = EnvValidation.ValidateSitePath(sitePath, c.Environments.Where(e => e.Name != name));
                if (!okPath) { error = pathError; return; }
                env.SitePath = sitePath;
            }
            if (req.Port is not null && req.Port.Value != env.Port)
            {
                var port = req.Port.Value;
                if (port is < 1 or > 65535) { error = $"Porta invalida: {port}."; return; }
                if (c.Environments.Any(e => e.Name != name && e.Port == port))
                { error = $"A porta {port} já está a ser usada por outro ambiente."; return; }
                env.Port = port;
            }
            env.Branch = branch;
            env.Source = source;
            if (!string.IsNullOrWhiteSpace(req.Configuration)) env.Configuration = req.Configuration;
            if (req.AutoDeploy is not null) env.AutoDeploy = req.AutoDeploy.Value;
            if (req.HostHeader is not null)
            {
                var (okHost, hostHeader, hostError) = EnvValidation.ValidateHostName(req.HostHeader);
                if (!okHost) { error = hostError; return; }
                var (okUnique, uniqueError) = EnvValidation.ValidateHostUnique(hostHeader, c.Environments.Where(e => e.Name != name));
                if (!okUnique) { error = uniqueError; return; }
                env.HostHeader = hostHeader;
            }
        });
        if (env is null) return Op.NotFound<EnvConfig>();
        if (error is not null) return Op.Invalid<EnvConfig>(error);
        notifier.Changed();
        return Op.Ok(env);
    }

    /// <summary>How often the remote is checked. Returns the error, or null when saved.</summary>
    public string? SetPollInterval(int? seconds)
    {
        if (seconds is null or < Poller.MinPollSec or > 3600)
            return $"O intervalo deve estar entre {Poller.MinPollSec} e 3600 segundos.";
        store.Update(c => c.PollIntervalSec = seconds.Value);
        notifier.Changed();
        return null;
    }

    /// <summary>Regras de exclusao da publicacao: as globais (todos os ambientes) e as de cada ambiente.</summary>
    public PublishRulesView GetPublishRules()
    {
        var c = store.Load();
        return new(c.ExcludeFromPublish, c.Environments.ToDictionary(e => e.Name, e => e.ExcludeFromPublish));
    }

    /// <summary>Saves the publish rules (they apply on the next deploy). Returns the error, or null when saved.</summary>
    public string? SavePublishRules(PublishRulesRequest req)
    {
        var (ok, all, error) = PublishRules.Validate(req.All);
        if (!ok) return error;
        var perEnv = new Dictionary<string, List<string>>();
        foreach (var (name, rules) in req.Environments ?? new Dictionary<string, List<string?>>())
        {
            var (okEnv, list, envError) = PublishRules.Validate(rules);
            if (!okEnv) return $"Ambiente {name}: {envError}";
            perEnv[name] = list;
        }
        store.Update(c =>
        {
            c.ExcludeFromPublish = all;
            foreach (var e in c.Environments)
                if (perEnv.TryGetValue(e.Name, out var list)) e.ExcludeFromPublish = list;
        });
        notifier.Changed();
        return null;
    }
}
