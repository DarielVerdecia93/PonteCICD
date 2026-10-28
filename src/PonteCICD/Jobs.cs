using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PonteCICD;

public enum JobKind { Deploy, Rollback, Start, Stop, Restart, Purge }

public enum JobStatus { Queued, Running, Succeeded, Failed }

public sealed class Job
{
    public required string Id { get; init; }
    public required string Env { get; init; }
    public required JobKind Kind { get; init; }
    /// <summary>Commit to deploy (null = branch tip when the job starts), or backup name to restore.</summary>
    public string? Arg { get; set; }
    /// <summary>manual | auto | startup</summary>
    public required string Trigger { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Branch { get; set; }
    public string? Commit { get; set; }
    public string? Subject { get; set; }
    /// <summary>Current step while running (the engine's last "==>" line).</summary>
    public string? Step { get; set; }
    public string? Summary { get; set; }
}

/// <summary>
/// Jobs run one at a time (builds and IIS Express swaps share the work folder).
/// Each job is persisted as runs/&lt;id&gt;.json plus its output in runs/&lt;id&gt;.log.
/// </summary>
public sealed class JobQueue
{
    const int KeepInMemory = 300;

    readonly CiStore _store;
    readonly Notifier _notifier;
    readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>();
    readonly ConcurrentDictionary<string, Job> _jobs = new();

    public JobQueue(CiStore store, Notifier notifier)
    {
        _store = store;
        _notifier = notifier;
        Directory.CreateDirectory(store.RunsDir);
        foreach (var file in Directory.GetFiles(store.RunsDir, "*.json").OrderDescending().Take(KeepInMemory))
        {
            var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(file), CiStore.Json);
            if (job is null) continue;
            // A job left Queued/Running belongs to a previous process that stopped mid-way.
            if (job.Status is JobStatus.Queued or JobStatus.Running)
            {
                job.Status = JobStatus.Failed;
                job.Step = null;
                job.Summary = "Interrompido: o servico foi parado.";
                Save(job);
            }
            _jobs[job.Id] = job;
        }
    }

    public ChannelReader<Job> Reader => _channel.Reader;

    public IEnumerable<Job> Recent(string? env = null, int take = 50) =>
        _jobs.Values.Where(j => env is null || j.Env == env).OrderByDescending(j => j.QueuedAt).Take(take);

    public Job? Get(string id) => _jobs.GetValueOrDefault(id);

    public Job? Pending(string env) =>
        _jobs.Values.Where(j => j.Env == env && j.Status is JobStatus.Queued or JobStatus.Running).MinBy(j => j.QueuedAt);

    public bool HasPending(string env) => Pending(env) is not null;

    public Job? LastDeploy(string env) =>
        _jobs.Values.Where(j => j.Env == env && j.Kind == JobKind.Deploy).MaxBy(j => j.QueuedAt);

    public Job Enqueue(string env, JobKind kind, string trigger, string? arg = null)
    {
        var job = new Job
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Env = env,
            Kind = kind,
            Trigger = trigger,
            Arg = string.IsNullOrWhiteSpace(arg) ? null : arg.Trim(),
        };
        _jobs[job.Id] = job;
        Save(job);
        _channel.Writer.TryWrite(job);
        return job;
    }

    public void Save(Job job)
    {
        File.WriteAllText(Path.Combine(_store.RunsDir, job.Id + ".json"), JsonSerializer.Serialize(job, CiStore.Json));
        _notifier.Changed();
    }

    public string LogPath(string id) => Path.Combine(_store.RunsDir, id + ".log");
}

public sealed class JobRunner(CiStore store, JobQueue queue, Mirror mirror, ILogger<JobRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.Now;
            queue.Save(job);
            await using var output = new StreamWriter(queue.LogPath(job.Id), append: false, Encoding.UTF8) { AutoFlush = true };
            try
            {
                var (exitCode, lastLines) = await RunAsync(job, output, stoppingToken);
                job.Status = exitCode == 0 ? JobStatus.Succeeded : JobStatus.Failed;
                job.Summary = Summarize(lastLines, job.Status);
                if (job.Status == JobStatus.Succeeded && job.Kind == JobKind.Purge)
                    store.Update(cfg => cfg.Environments.RemoveAll(e => e.Name == job.Env));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.Status = JobStatus.Failed;
                job.Summary = ex.Message;
                await output.WriteLineAsync("ERROR: " + ex);
                log.LogError(ex, "Job {Id} falhou", job.Id);
            }
            job.Step = null;
            job.FinishedAt = DateTimeOffset.Now;
            queue.Save(job);
        }
    }

    async Task<(int ExitCode, List<string> LastLines)> RunAsync(Job job, StreamWriter output, CancellationToken ct)
    {
        string script;
        var args = new List<string> { "-Environment", job.Env };
        switch (job.Kind)
        {
            case JobKind.Deploy:
                await ResolveDeployTargetAsync(job, output, ct);
                script = "deploy.ps1";
                args.AddRange(["-Ref", job.Commit!, "-NoFetch"]);
                break;
            case JobKind.Rollback:
                script = "rollback.ps1";
                if (job.Arg is not null) args.AddRange(["-Backup", job.Arg]);
                break;
            default:
                script = "site.ps1";
                args.AddRange(["-Action", job.Kind.ToString().ToLowerInvariant()]);
                break;
        }

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = store.EngineDir,
        };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(store.EngineDir, script)])
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        GitAuth.Apply(psi);   // o motor (PowerShell) herda o token para o git

        await output.WriteLineAsync($"> {script} {string.Join(' ', args)}");
        var last = new List<string>();
        var sync = new object();
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (sync)
            {
                output.WriteLine(line);
                if (string.IsNullOrWhiteSpace(line)) return;
                last.Add(line);
                if (last.Count > 30) last.RemoveAt(0);
                if (line.StartsWith("==>"))
                {
                    job.Step = line[3..].Trim();
                    queue.Save(job);
                }
            }
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw;
        }
        p.WaitForExit(); // drains the async output readers
        return (p.ExitCode, last);
    }

    // Without an explicit commit, deploy whatever the branch points to *now*: if more commits arrived
    // while the job was queued, they go out in this same build.
    async Task ResolveDeployTargetAsync(Job job, StreamWriter output, CancellationToken ct)
    {
        var env = store.Load().Environments.FirstOrDefault(e => e.Name == job.Env)
                  ?? throw new InvalidOperationException($"O ambiente {job.Env} ja nao existe.");
        job.Branch = env.Branch;
        if (job.Arg is null)
        {
            await output.WriteLineAsync($"==> A procurar o ultimo commit de {env.Branch} ({env.Source})");
            if (env.Source == BranchSource.Local) await mirror.SyncLocalAsync(ct);
            else await mirror.SyncRemoteAsync(force: false, ct);
            var head = mirror.Find(env.Branch, env.Source)
                       ?? throw new InvalidOperationException($"O branch {env.Branch} ({env.Source}) ja nao existe.");
            job.Commit = head.Commit;
            job.Subject = head.Subject;
        }
        else
        {
            job.Commit = job.Arg;
        }
        queue.Save(job);
    }

    static string Summarize(List<string> lines, JobStatus status)
    {
        if (status == JobStatus.Succeeded)
            return lines.LastOrDefault(l => l.StartsWith("==>"))?[3..].Trim() ?? "OK";
        // The engine scripts report fatal errors as "ERROR: <message>" (see their trap).
        return lines.LastOrDefault(l => l.StartsWith("ERROR: "))?[7..] ?? lines.LastOrDefault() ?? "Falhou (ver log)";
    }
}
