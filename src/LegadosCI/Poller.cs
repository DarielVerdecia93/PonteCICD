using System.Collections.Concurrent;

namespace LegadosCI;

/// <summary>
/// Detects new commits and decides when to deploy:
///  - remote: git ls-remote every PollIntervalSec (fetches only when a branch moved);
///  - local:  the local repository is watched, so a commit is seen within a couple of seconds.
/// An environment with auto-deploy goes out once its branch has been quiet for QuietSec
/// (a burst of commits builds once, with the latest one). A commit that already failed is not
/// retried until a newer commit arrives or someone deploys by hand.
/// </summary>
public sealed class Poller(CiStore store, Mirror mirror, JobQueue queue, Notifier notifier, IisStateProbe probe, ILogger<Poller> log) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    // env -> (commit waiting to be deployed, when it was first seen)
    readonly ConcurrentDictionary<string, (string Commit, DateTimeOffset Since)> _waiting = new();
    readonly SemaphoreSlim _localChanged = new(0);

    /// <summary>When the auto-deploy of an environment will start, if one is waiting.</summary>
    public DateTimeOffset? AutoDeployAt(string env) =>
        _waiting.TryGetValue(env, out var w) ? w.Since.AddSeconds(store.Load().QuietSec) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await mirror.SyncRemoteAsync(force: true, stoppingToken);
        await mirror.SyncLocalAsync(stoppingToken);
        StartDeployedSites();
        using var watcher = WatchLocalRepository();

        var nextRemoteCheck = DateTimeOffset.Now;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.Now >= nextRemoteCheck)
                {
                    await mirror.SyncRemoteAsync(force: false, stoppingToken);
                    nextRemoteCheck = DateTimeOffset.Now.AddSeconds(Math.Max(10, store.Load().PollIntervalSec));
                }
                if (await _localChanged.WaitAsync(Tick, stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); // let git finish writing refs
                    while (_localChanged.CurrentCount > 0) await _localChanged.WaitAsync(stoppingToken);
                    await mirror.SyncLocalAsync(stoppingToken);
                }
                Evaluate();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Erro ao verificar branches");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    void Evaluate()
    {
        var cfg = store.Load();
        foreach (var env in cfg.Environments)
        {
            var head = env.AutoDeploy ? mirror.Find(env.Branch, env.Source) : null;
            var needsDeploy = head is not null
                              && !queue.HasPending(env.Name)
                              && store.GetDeployInfo(env.Name)?.Commit != head.Commit
                              && queue.LastDeploy(env.Name)?.Commit != head.Commit;
            if (!needsDeploy)
            {
                if (_waiting.TryRemove(env.Name, out _)) notifier.Changed();
                continue;
            }

            // The quiet period restarts whenever the branch moves again.
            if (!_waiting.TryGetValue(env.Name, out var w) || w.Commit != head!.Commit)
            {
                _waiting[env.Name] = (head!.Commit, DateTimeOffset.Now);
                notifier.Changed();
                continue;
            }
            if (DateTimeOffset.Now < w.Since.AddSeconds(cfg.QuietSec)) continue;

            log.LogInformation("{Branch} em {Commit}: deploy automatico de {Env}", env.Branch, head.Commit[..7], env.Name);
            _waiting.TryRemove(env.Name, out _);
            queue.Enqueue(env.Name, JobKind.Deploy, "auto"); // resolves the newest commit when it starts
        }
        foreach (var gone in _waiting.Keys.Where(k => cfg.Environments.All(e => e.Name != k)))
            _waiting.TryRemove(gone, out _);
    }

    FileSystemWatcher? WatchLocalRepository()
    {
        var repo = store.Load().LocalRepoPath;
        var gitDir = string.IsNullOrWhiteSpace(repo) ? null : Path.Combine(repo, ".git");
        if (gitDir is null || !Directory.Exists(gitDir)) return null;

        var watcher = new FileSystemWatcher(gitDir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        void OnChange(object _, FileSystemEventArgs e)
        {
            var rel = Path.GetRelativePath(gitDir, e.FullPath);
            if (rel.StartsWith(@"refs\heads", StringComparison.OrdinalIgnoreCase) || rel.Equals("packed-refs", StringComparison.OrdinalIgnoreCase))
                _localChanged.Release();
        }
        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Renamed += OnChange;
        watcher.Deleted += OnChange;
        watcher.EnableRaisingEvents = true;
        log.LogInformation("A vigiar branches do repo local {Repo}", repo);
        return watcher;
    }

    // On startup, bring back every deployed environment whose IIS site/pool is stopped.
    void StartDeployedSites()
    {
        if (!probe.IisAvailable)
        {
            log.LogWarning("IIS nao disponivel: os sites implantados nao sao iniciados no arranque.");
            return;
        }
        foreach (var env in store.Load().Environments)
        {
            if (store.GetDeployInfo(env.Name) is not null && !probe.Running(env.Name) && !queue.HasPending(env.Name))
                queue.Enqueue(env.Name, JobKind.Start, "startup");
        }
    }
}
