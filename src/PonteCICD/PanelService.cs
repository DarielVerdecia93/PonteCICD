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

/// <summary>Result of queuing an environment action: the job, or why it was refused.</summary>
public sealed record EnqueueResult(Job? Job, bool NotFound = false, string? Conflict = null);

/// <summary>
/// Estado dos ambientes e acoes sobre eles. Partilhado pela API (/api/state, /api/envs/...) e pelo painel Blazor,
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
    public EnqueueResult Enqueue(string env, JobKind kind, string? arg = null)
    {
        if (!store.Load().Environments.Any(e => e.Name == env)) return new(null, NotFound: true);
        // Eliminar nao espera pela operacao em curso: fica na fila a seguir a ela.
        if (kind != JobKind.Purge)
        {
            // Implantar precisa de ler o repositorio: sem URL e token nao ha acesso (parar, iniciar e rollback nao precisam).
            if (kind == JobKind.Deploy && !mirror.HasAccess)
                return new(null, Conflict: "Acesso ao repositorio nao configurado: indique o URL e o token em «Acesso ao repositorio» antes de implantar.");
            if (queue.HasPending(env)) return new(null, Conflict: $"Já existe uma operação em curso para {env}.");
        }
        return new(queue.Enqueue(env, kind, "manual", arg));
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
}
