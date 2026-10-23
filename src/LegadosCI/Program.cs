using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LegadosCI;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "LegadosCI");

var root = builder.Configuration["CiRoot"] ?? throw new InvalidOperationException("Falta CiRoot en appsettings.json.");
var store = new CiStore(root);
builder.WebHost.UseUrls(store.Load().ListenUrl);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<Mirror>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<Poller>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Poller>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/state", async (Mirror mirror, JobQueue queue, Poller poller, CancellationToken ct) =>
{
    var cfg = store.Load();
    var envs = new List<object>();
    foreach (var e in cfg.Environments)
    {
        var head = mirror.Find(e.Branch, e.Source);
        var deployed = store.GetDeployInfo(e.Name);
        var pending = head is not null && deployed is not null && deployed.Commit != head.Commit
            ? await mirror.PendingAsync(deployed.Commit, head.Commit, ct)
            : [];
        envs.Add(new
        {
            e.Name, e.Branch, e.Source, e.Port, e.Configuration, e.AutoDeploy,
            Url = $"http://localhost:{e.Port}/",
            Running = Poller.IsListening(e.Port),
            Deployed = deployed,
            Head = head,
            Pending = pending,
            AutoDeployAt = poller.AutoDeployAt(e.Name),
            Job = queue.Pending(e.Name),
            Backups = store.GetBackups(e.Name),
        });
    }
    return new
    {
        mirror.LastCheck,
        mirror.LastChange,
        mirror.LastError,
        cfg.PollIntervalSec,
        cfg.QuietSec,
        cfg.LocalRepoPath,
        Environments = envs,
    };
});

api.MapGet("/branches", (Mirror mirror) => mirror.Branches);

// Server-Sent Events: the panel refreshes as soon as something changes.
api.MapGet("/events", async (HttpContext ctx, Notifier notifier, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    while (!ct.IsCancellationRequested)
    {
        await ctx.Response.WriteAsync($"data: {notifier.Version}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        await notifier.WaitAsync(TimeSpan.FromSeconds(20), ct);
    }
});

api.MapPost("/fetch", async (Mirror mirror, CancellationToken ct) =>
{
    await mirror.SyncRemoteAsync(force: true, ct);
    await mirror.SyncLocalAsync(ct);
    return Results.Ok(new { mirror.LastCheck, mirror.LastError });
});

api.MapPost("/envs", (EnvRequest req, Mirror mirror, JobQueue queue) =>
{
    var source = req.Source ?? BranchSource.Remote;
    var name = (req.Name ?? "").Trim().ToLowerInvariant();
    if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,39}$"))
        return Results.BadRequest("Nombre no valido: minusculas, numeros y guiones (max. 40).");
    if (string.IsNullOrWhiteSpace(req.Branch) || mirror.Find(req.Branch, source) is null)
        return Results.BadRequest($"La rama '{req.Branch}' no existe ({source}).");

    string? error = null;
    var cfg = store.Update(c =>
    {
        if (c.Environments.Any(e => e.Name == name)) { error = $"Ya existe el entorno '{name}'."; return; }
        var port = req.Port ?? Enumerable.Range(8090, 200).First(p => c.Environments.All(e => e.Port != p) && !Poller.IsListening(p));
        if (c.Environments.Any(e => e.Port == port)) { error = $"El puerto {port} ya lo usa otro entorno."; return; }
        c.Environments.Add(new EnvConfig
        {
            Name = name,
            Branch = req.Branch,
            Source = source,
            Port = port,
            Configuration = string.IsNullOrWhiteSpace(req.Configuration) ? "Desenvolvimento" : req.Configuration,
            AutoDeploy = req.AutoDeploy ?? true,
        });
    });
    if (error is not null) return Results.Conflict(error);
    var env = cfg.Environments.First(e => e.Name == name);
    queue.Enqueue(name, JobKind.Deploy, "manual");
    return Results.Ok(env);
});

api.MapPut("/envs/{name}", (string name, EnvRequest req, Mirror mirror) =>
{
    EnvConfig? env = null;
    string? error = null;
    store.Update(c =>
    {
        env = c.Environments.FirstOrDefault(e => e.Name == name);
        if (env is null) return;
        var branch = req.Branch ?? env.Branch;
        var source = req.Source ?? env.Source;
        if (mirror.Find(branch, source) is null) { error = $"La rama '{branch}' no existe ({source})."; return; }
        env.Branch = branch;
        env.Source = source;
        if (!string.IsNullOrWhiteSpace(req.Configuration)) env.Configuration = req.Configuration;
        if (req.AutoDeploy is not null) env.AutoDeploy = req.AutoDeploy.Value;
    });
    if (env is null) return Results.NotFound();
    return error is null ? Results.Ok(env) : Results.BadRequest(error);
});

api.MapDelete("/envs/{name}", (string name, JobQueue queue) =>
    EnvExists(name) ? Results.Ok(queue.Enqueue(name, JobKind.Purge, "manual")) : Results.NotFound());

// arg: commit to deploy (default: branch tip) or backup to restore (default: latest).
api.MapPost("/envs/{name}/{action}", (string name, string action, string? arg, JobQueue queue) =>
{
    if (!EnvExists(name)) return Results.NotFound();
    if (!Enum.TryParse<JobKind>(action, ignoreCase: true, out var kind) || kind == JobKind.Purge)
        return Results.BadRequest($"Accion desconocida: {action}");
    if (queue.HasPending(name)) return Results.Conflict($"Ya hay una operacion en curso para {name}.");
    return Results.Ok(queue.Enqueue(name, kind, "manual", arg));
});

api.MapGet("/jobs", (string? env, JobQueue queue) => queue.Recent(env));

api.MapGet("/jobs/{id}/log", (string id, JobQueue queue) =>
{
    if (queue.Get(id) is null) return Results.NotFound();
    var path = queue.LogPath(id);
    if (!File.Exists(path)) return Results.Text("");
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    return Results.Text(reader.ReadToEnd());
});

app.Run();

bool EnvExists(string name) => store.Load().Environments.Any(e => e.Name == name);

record EnvRequest(string? Name, string? Branch, string? Source, int? Port, string? Configuration, bool? AutoDeploy);
