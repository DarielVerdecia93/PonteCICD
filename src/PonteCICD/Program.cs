using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using PonteCICD;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "PonteCICD");

// Por defecto la raiz del CI es la carpeta del repo Legados-CI (la que contiene engine\deploy.ps1 y ci.json).
// Se busca subiendo desde el exe, asi funciona igual con dotnet run (bin\Debug\...) y con el publicado (service\).
// Se puede sobrescribir con CiRoot en appsettings.json si los datos viven en otro sitio.
var root = builder.Configuration["CiRoot"];
if (string.IsNullOrWhiteSpace(root)) root = FindCiRoot(AppContext.BaseDirectory);
var store = new CiStore(root);
var gitAuth = new GitAuth(store);
var users = new UserStore(store);
builder.WebHost.UseUrls(store.Load().ListenUrl);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(gitAuth);
builder.Services.AddSingleton(users);
builder.Services.AddSingleton<LoginThrottle>();
// Chaves da cookie de sessao persistidas (cifradas com DPAPI da maquina): a sessao sobrevive a reinicios do servico.
builder.Services.AddDataProtection()
    .SetApplicationName("PonteCICD")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "secrets", "keys")))
    .ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddPanelAuth();
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
app.UseAuthentication();
app.UseAuthorization();

// Startup self-check (A-8): warn, don't crash, when not elevated or IIS is unavailable; the service
// then runs in degraded mode (iisAvailable=false) and the panel surfaces it.
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    if (!IsElevated())
        log.LogWarning("O servico nao e executado como administrador: o IIS nao podera ser criado/controlado. Reinstale o servico com permissoes elevadas.");
    if (!app.Services.GetRequiredService<IisStateProbe>().PreflightOk())
        log.LogWarning("IIS nao disponivel (preflight): instale o IIS (Web-Server, Web-Mgmt-Console) e reinicie. Modo degradado.");
}

// Toda a API exige sessao, exceto o login (ver AuthEndpoints).
var api = app.MapGroup("/api").RequireAuthorization();
api.MapAuth();

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
        IisError = probe.Error,
        ProjectConfigured = !string.IsNullOrWhiteSpace(cfg.RepoUrl),
        Environments = envs,
    };
});

api.MapPut("/settings", (SettingsRequest req, Notifier notifier) =>
{
    if (req.PollIntervalSec is null or < Poller.MinPollSec or > 3600)
        return Results.BadRequest($"O intervalo deve estar entre {Poller.MinPollSec} e 3600 segundos.");
    store.Update(c => c.PollIntervalSec = req.PollIntervalSec.Value);
    notifier.Changed();
    return Results.Ok();
});

api.MapGet("/project", () =>
{
    var c = store.Load();
    return new { c.RepoUrl, c.SolutionFile, c.ProjectFile, c.PackagesSource, c.LocalRepoPath, HasToken = gitAuth.HasToken, TokenUser = gitAuth.User };
});

api.MapPut("/project", (ProjectRequest req, Mirror mirror, Notifier notifier) =>
{
    var url = (req.RepoUrl ?? "").Trim();
    if (url.Length > 0 && !Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase))
        return Results.BadRequest("O URL do repositorio deve comecar por https:// (ou http://).");
    // Com token guardado no painel, o URL nao leva «utilizador:token@» (nem ${VAR}): entraria em conflito com a autenticacao.
    if (!string.IsNullOrWhiteSpace(req.Token) || (gitAuth.HasToken && req.ClearToken != true))
        url = Regex.Replace(url, @"^(https?://)[^/@]*@", "$1", RegexOptions.IgnoreCase);
    store.Update(c =>
    {
        c.RepoUrl = url;
        c.SolutionFile = (req.SolutionFile ?? "").Trim();
        c.ProjectFile = (req.ProjectFile ?? "").Trim();
        c.PackagesSource = (req.PackagesSource ?? "").Trim();
        c.LocalRepoPath = (req.LocalRepoPath ?? "").Trim();
    });
    if (req.ClearToken == true) gitAuth.Clear();
    else if (!string.IsNullOrWhiteSpace(req.Token)) gitAuth.Save(req.TokenUser, req.Token);
    // Verifica o remoto ja com a nova configuracao (o resultado aparece no painel).
    _ = Task.Run(async () => { try { await mirror.SyncRemoteAsync(force: true, CancellationToken.None); } catch (Exception) { /* fica em lastError */ } });
    notifier.Changed();
    return Results.Ok();
});

api.MapPost("/project/test", async (ProjectRequest req, CancellationToken ct) =>
{
    var url = Mirror.ExpandEnvVars((string.IsNullOrWhiteSpace(req.RepoUrl) ? store.Load().RepoUrl : req.RepoUrl).Trim());
    if (url.Length == 0) return Results.BadRequest("Indique o URL do repositorio.");
    var secret = string.IsNullOrWhiteSpace(req.Token) ? null : new GitSecret(req.TokenUser ?? "", req.Token.Trim());
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(30));
    try
    {
        var (code, output) = await Git.RunAsync(["ls-remote", "--heads", url], null, cts.Token, secret, url);
        var clean = GitAuth.Redact(output, secret?.Token).Trim();
        if (code != 0)
        {
            // git sem token valido acaba em "could not read Username": traduz para algo acionavel.
            var hint = clean.Contains("terminal prompts disabled") || clean.Contains("Authentication failed") || clean.Contains("403") || clean.Contains("401")
                ? "Token em falta, invalido ou sem permissao de leitura neste repositorio (ou o URL esta errado)."
                : (clean.Length > 400 ? clean[..400] : clean);
            return Results.BadRequest($"A ligacao falhou: {hint}");
        }
        return Results.Ok(new { branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length });
    }
    catch (OperationCanceledException)
    {
        return Results.BadRequest("Tempo esgotado (30 s) a ligar ao repositorio.");
    }
});

api.MapGet("/branches", (Mirror mirror) => mirror.Branches);

api.MapGet("/configurations", (string? branch, string? source, Mirror mirror, CancellationToken ct) =>
    mirror.ConfigurationsAsync(branch, source, ct));

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
        return Results.BadRequest("Nome invalido: minusculas, numeros e hifens (max. 40).");

    var cfg0 = store.Load();
    // Duplicate (copyFrom): seed from a template for the fields the request omits, but never copy the
    // IIS identity (SiteName/AppPoolName) nor the host-specific SitePath/HostHeader/Port.
    EnvConfig? template = null;
    if (!string.IsNullOrWhiteSpace(req.CopyFrom))
    {
        template = cfg0.Environments.FirstOrDefault(e => e.Name == req.CopyFrom);
        if (template is null) return Results.BadRequest($"O ambiente de origem '{req.CopyFrom}' não existe.");
    }

    var branch = req.Branch ?? template?.Branch;
    var source = req.Source ?? template?.Source ?? BranchSource.Remote;
    if (string.IsNullOrWhiteSpace(branch) || mirror.Find(branch, source) is null)
        return Results.BadRequest($"O branch '{branch}' nao existe ({source}).");

    // Sin ruta explicita se usa la convencion por defecto C:\inetpub\wwwroot\deploys\<env> (dentro del
    // arbol de IIS), y se guarda en ci.json para que servicio y engine resuelvan la misma carpeta.
    var sitePath = (req.SitePath ?? "").Trim();
    if (sitePath.Length == 0) sitePath = CiStore.DefaultSitePath(name);
    var (okPath, pathError) = EnvValidation.ValidateSitePath(sitePath, cfg0.Environments);
    if (!okPath) return Results.BadRequest(pathError);

    string? error = null;
    var cfg = store.Update(c =>
    {
        if (c.Environments.Any(e => e.Name == name)) { error = $"Ja existe o ambiente '{name}'."; return; }
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
            HostHeader = (req.HostHeader ?? "").Trim(),
        });
    });
    if (error is not null) return Results.Conflict(error);
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
            return Results.BadRequest("O nome do site/pool do IIS não pode ser alterado depois de criado.");
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
        return Results.BadRequest($"Acao desconhecida: {action}");
    if (queue.HasPending(name)) return Results.Conflict($"Já existe uma operação em curso para {name}.");
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

static string FindCiRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "engine", "deploy.ps1")))
            return dir.FullName;
    return start;
}

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

record SettingsRequest(int? PollIntervalSec);

record ProjectRequest(
    string? RepoUrl, string? SolutionFile, string? ProjectFile, string? PackagesSource, string? LocalRepoPath,
    string? TokenUser, string? Token, bool? ClearToken);

record EnvRequest(
    string? Name, string? Branch, string? Source, int? Port, string? Configuration, bool? AutoDeploy,
    string? SitePath, string? SiteName, string? AppPoolName, string? HostHeader, string? CopyFrom);
