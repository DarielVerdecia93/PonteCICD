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
builder.Services.AddSingleton<IisStateProbe>();
builder.Services.AddSingleton<Poller>();
builder.Services.AddHostedService<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Poller>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Startup self-check (A-8): warn, don't crash, when not elevated or IIS is unavailable; the service
// then runs in degraded mode (iisAvailable=false) and the panel surfaces it.
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    if (!IsElevated())
        log.LogWarning("El servicio no se ejecuta como administrador: IIS no podra crearse/controlarse. Reinstale el servicio con permisos elevados.");
    if (!app.Services.GetRequiredService<IisStateProbe>().PreflightOk())
        log.LogWarning("IIS no disponible (preflight): instale IIS (Web-Server, Web-Mgmt-Console) y reinicie. Modo degradado.");
}

var api = app.MapGroup("/api");

api.MapGet("/state", async (Mirror mirror, JobQueue queue, Poller poller, IisStateProbe probe, CancellationToken ct) =>
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
            e.SitePath, e.SiteName, e.AppPoolName, e.HostHeader,
            Url = $"http://localhost:{e.Port}/",
            Running = probe.Running(e.Name),
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
        IisAvailable = probe.IisAvailable,
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
    var name = (req.Name ?? "").Trim().ToLowerInvariant();
    if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,39}$"))
        return Results.BadRequest("Nombre no valido: minusculas, numeros y guiones (max. 40).");

    var cfg0 = store.Load();
    // Duplicate (copyFrom): seed from a template for the fields the request omits, but never copy the
    // IIS identity (SiteName/AppPoolName) nor the host-specific SitePath/HostHeader/Port.
    EnvConfig? template = null;
    if (!string.IsNullOrWhiteSpace(req.CopyFrom))
    {
        template = cfg0.Environments.FirstOrDefault(e => e.Name == req.CopyFrom);
        if (template is null) return Results.BadRequest($"El entorno origen '{req.CopyFrom}' no existe.");
    }

    var branch = req.Branch ?? template?.Branch;
    var source = req.Source ?? template?.Source ?? BranchSource.Remote;
    if (string.IsNullOrWhiteSpace(branch) || mirror.Find(branch, source) is null)
        return Results.BadRequest($"La rama '{branch}' no existe ({source}).");

    var sitePath = (req.SitePath ?? "").Trim();
    var (okPath, pathError) = EnvValidation.ValidateSitePath(sitePath, cfg0.Environments);
    if (!okPath) return Results.BadRequest(pathError);

    string? error = null;
    var cfg = store.Update(c =>
    {
        if (c.Environments.Any(e => e.Name == name)) { error = $"Ya existe el entorno '{name}'."; return; }
        var port = req.Port ?? Enumerable.Range(8090, 200).First(p => c.Environments.All(e => e.Port != p) && PortProbe.IsFree(p));
        if (c.Environments.Any(e => e.Port == port)) { error = $"El puerto {port} ya lo usa otro entorno."; return; }

        var siteName = CiStore.DefaultSiteName(name);
        if (c.Environments.Any(e => (string.IsNullOrWhiteSpace(e.SiteName) ? CiStore.DefaultSiteName(e.Name) : e.SiteName) == siteName))
        { error = $"El sitio IIS '{siteName}' ya lo usa otro entorno."; return; }
        if (c.Environments.Any(e => (string.IsNullOrWhiteSpace(e.AppPoolName) ? CiStore.DefaultSiteName(e.Name) : e.AppPoolName) == siteName))
        { error = $"El pool IIS '{siteName}' ya lo usa otro entorno."; return; }

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
            HostHeader = (req.HostHeader ?? "").Trim(),
        });
    });
    if (error is not null) return Results.Conflict(error);
    var env = cfg.Environments.First(e => e.Name == name);
    queue.Enqueue(name, JobKind.Deploy, "manual");
    return Results.Ok(env);
});

api.MapPut("/envs/{name}", (string name, EnvRequest req, Mirror mirror) =>
{
    // The IIS site/pool name is fixed once the env exists (Decision 7): reject any attempt to change it.
    if (req.SiteName is not null || req.AppPoolName is not null)
    {
        var current = store.Load().Environments.FirstOrDefault(e => e.Name == name);
        if (current is null) return Results.NotFound();
        var curSite = string.IsNullOrWhiteSpace(current.SiteName) ? CiStore.DefaultSiteName(current.Name) : current.SiteName;
        var curPool = string.IsNullOrWhiteSpace(current.AppPoolName) ? CiStore.DefaultSiteName(current.Name) : current.AppPoolName;
        if ((req.SiteName is not null && req.SiteName.Trim() != curSite) ||
            (req.AppPoolName is not null && req.AppPoolName.Trim() != curPool))
            return Results.BadRequest("O nome do site/pool do IIS nao pode ser alterado depois de criado.");
    }

    EnvConfig? env = null;
    string? error = null;
    store.Update(c =>
    {
        env = c.Environments.FirstOrDefault(e => e.Name == name);
        if (env is null) return;
        var branch = req.Branch ?? env.Branch;
        var source = req.Source ?? env.Source;
        if (mirror.Find(branch, source) is null) { error = $"La rama '{branch}' no existe ({source})."; return; }
        if (req.SitePath is not null)
        {
            var sitePath = req.SitePath.Trim();
            var (okPath, pathError) = EnvValidation.ValidateSitePath(sitePath, c.Environments.Where(e => e.Name != name));
            if (!okPath) { error = pathError; return; }
            env.SitePath = sitePath;
        }
        env.Branch = branch;
        env.Source = source;
        if (!string.IsNullOrWhiteSpace(req.Configuration)) env.Configuration = req.Configuration;
        if (req.AutoDeploy is not null) env.AutoDeploy = req.AutoDeploy.Value;
        if (req.HostHeader is not null) env.HostHeader = req.HostHeader.Trim();
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

static bool IsElevated()
{
    if (!OperatingSystem.IsWindows()) return false;
    try
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    catch (Exception)
    {
        return false;
    }
}

record EnvRequest(
    string? Name, string? Branch, string? Source, int? Port, string? Configuration, bool? AutoDeploy,
    string? SitePath, string? SiteName, string? AppPoolName, string? HostHeader, string? CopyFrom);
