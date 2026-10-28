using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;
using PonteCICD;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "PonteCICD");
// Sem publicar (dotnet run / build), os ficheiros do Blazor e do MudBlazor estao na cache do NuGet e so se servem em
// Development; isto serve-os em qualquer ambiente. No publicado ja estao em wwwroot e nao faz nada.
builder.WebHost.UseStaticWebAssets();

// Por defecto la raiz del CI es la carpeta del repo Legados-CI (la que contiene engine\deploy.ps1 y ci.json).
// Se busca subiendo desde el exe, asi funciona igual con dotnet run (bin\Debug\...) y con el publicado (service\).
// Se puede sobrescribir con CiRoot en appsettings.json si los datos viven en otro sitio.
var root = builder.Configuration["CiRoot"];
if (string.IsNullOrWhiteSpace(root)) root = FindCiRoot(AppContext.BaseDirectory);
var store = new CiStore(root);
var gitAuth = new GitAuth(store);
var users = new UserStore(store);
// Modo linha de comandos (instalador / recuperar palavra-passe): grava o administrador e sai, sem arrancar o painel.
if (args.Contains("--set-admin")) Environment.Exit(AdminCli.Run(args, users));
// listenUrl admite varios URLs separados por ';'. Com um nome de host (p. ex. http://admin-pontecicd/) usa-se o http.sys,
// que partilha o porto 80 com o IIS por nome de host; so com localhost/IP usa-se o Kestrel (sem registo de URLs).
// "ListenUrl" (variavel de ambiente ou --ListenUrl=...) tem prioridade sobre o ci.json: serve para correr uma copia de
// desenvolvimento ao lado do servico instalado (ex.: --ListenUrl=http://localhost:5081) sem mexer no ci.json.
var listenSetting = builder.Configuration["ListenUrl"];
if (string.IsNullOrWhiteSpace(listenSetting)) listenSetting = store.Load().ListenUrl;
var listenUrls = listenSetting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
string? startupWarning = null;
// O http.sys so deixa registar nomes de host a administradores. Sem elevacao (p. ex. "dotnet run" num terminal normal)
// escuta so os enderecos locais, em vez de falhar ao arrancar.
if (listenUrls.Any(u => !IsLoopbackUrl(u)) && !IsElevated())
{
    var local = listenUrls.Where(IsLoopbackUrl).ToArray();
    if (local.Length == 0) local = ["http://localhost:5080"];
    startupWarning = $"Sem permissoes de administrador: o nome do painel ({string.Join("; ", listenUrls.Where(u => !IsLoopbackUrl(u)))}) nao foi registado; a escutar so em {string.Join("; ", local)}.";
    listenUrls = local;
}
if (listenUrls.Any(u => !IsLoopbackUrl(u)))
    builder.WebHost.UseHttpSys(o =>
    {
        foreach (var u in listenUrls) o.UrlPrefixes.Add(u.EndsWith('/') ? u : u + "/");
    });
else
    builder.WebHost.UseUrls(listenUrls);

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
builder.Services.AddSingleton<PanelService>();
builder.Services.AddSingleton<ProjectService>();
// Painel em Blazor: componentes interativos no servidor (SignalR), com MudBlazor. A sessao e a cookie de sempre
// (o login passa pela API, que a cria); o PanelAuthStateProvider volta a verifica-la com a pagina aberta.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, PanelAuthStateProvider>();
// Avisos durante 7 s, em baixo a esquerda: a direita tapariam os botoes dos dialogos.
builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomLeft;
    o.SnackbarConfiguration.VisibleStateDuration = 7000;
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Startup self-check (A-8): warn, don't crash, when not elevated or IIS is unavailable; the service
// then runs in degraded mode (iisAvailable=false) and the panel surfaces it.
{
    var log = app.Services.GetRequiredService<ILogger<Program>>();
    if (startupWarning is not null) log.LogWarning("{Warning}", startupWarning);
    if (!IsElevated())
        log.LogWarning("O servico nao e executado como administrador: o IIS nao podera ser criado/controlado. Reinstale o servico com permissoes elevadas.");
    if (!app.Services.GetRequiredService<IisStateProbe>().PreflightOk())
        log.LogWarning("IIS nao disponivel (preflight): instale o IIS (Web-Server, Web-Mgmt-Console) e reinicie. Modo degradado.");
}

// Toda a API exige sessao, exceto o login (ver AuthEndpoints).
var api = app.MapGroup("/api").RequireAuthorization();
api.MapAuth();

api.MapGet("/state", (PanelService panel, CancellationToken ct) => panel.GetStateAsync(ct));

api.MapPut("/settings", (SettingsRequest req, PanelService panel) =>
    panel.SetPollInterval(req.PollIntervalSec) is { } error ? Results.BadRequest(error) : Results.Ok());

// Regras de exclusao da publicacao: as globais (todos os ambientes) e as de cada ambiente. Aplicam-se no deploy seguinte.
api.MapGet("/publish-rules", (PanelService panel) => panel.GetPublishRules());

api.MapPut("/publish-rules", (PublishRulesRequest req, PanelService panel) =>
    panel.SavePublishRules(req) is { } error ? Results.BadRequest(error) : Results.Ok());

api.MapGet("/project", (ProjectService project) => project.Get());

api.MapPut("/project", async (ProjectRequest req, ProjectService project) => ToHttp(await project.SaveAsync(req)));

api.MapPost("/project/test", async (ProjectRequest req, ProjectService project, CancellationToken ct) =>
{
    var r = await project.TestAsync(req, ct);
    return r.Ok ? Results.Ok(new { branches = r.Value }) : Results.BadRequest(r.Message);
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
    return Results.Ok(new { mirror.LastCheck, LastError = mirror.LastError is null ? null : GitAuth.Friendly(mirror.LastError) });
});

api.MapPost("/envs", async (EnvRequest req, PanelService panel, CancellationToken ct) => ToHttp(await panel.CreateEnvAsync(req, ct)));

api.MapPut("/envs/{name}", (string name, EnvRequest req, PanelService panel) => ToHttp(panel.UpdateEnv(name, req)));

api.MapDelete("/envs/{name}", (string name, PanelService panel) => ToHttp(panel.Enqueue(name, JobKind.Purge)));

// arg: commit to deploy (default: branch tip) or backup to restore (default: latest).
api.MapPost("/envs/{name}/{action}", (string name, string action, string? arg, PanelService panel) =>
{
    if (!EnvExists(name)) return Results.NotFound();
    if (!Enum.TryParse<JobKind>(action, ignoreCase: true, out var kind) || kind == JobKind.Purge)
        return Results.BadRequest($"Acao desconhecida: {action}");
    return ToHttp(panel.Enqueue(name, kind, arg));
});

api.MapGet("/jobs", (string? env, JobQueue queue) => queue.Recent(env));

api.MapGet("/jobs/{id}/log", (string id, JobQueue queue) =>
    queue.Get(id) is null ? Results.NotFound() : Results.Text(queue.ReadLog(id)));

// Painel (Blazor): / e o resto das paginas exigem sessao; sem ela vao para /login. O painel antigo fica em /classico/.
app.MapStaticAssets();
app.MapRazorComponents<PonteCICD.Components.App>().AddInteractiveServerRenderMode();

try
{
    app.Run();
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or Microsoft.AspNetCore.Server.HttpSys.HttpSysException or IOException)
{
    // Causa habitual: outra instancia (o servico PonteCICD instalado) ja ocupa o endereco.
    // Escreve na consola: nesta altura o host ja fechou o logger.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ERRO: nao foi possivel escutar em {string.Join("; ", listenUrls)}: {ex.Message}");
    Console.Error.WriteLine("Se o servico PonteCICD esta a correr, pare-o (Stop-Service PonteCICD) ou use outro endereco:");
    Console.Error.WriteLine("    dotnet run -- --ListenUrl=http://localhost:5081");
    throw;
}

bool EnvExists(string name) => store.Load().Environments.Any(e => e.Name == name);

static IResult ToHttp<T>(OpResult<T> r) => r.Error switch
{
    OpError.NotFound => Results.NotFound(),
    OpError.Invalid => Results.BadRequest(r.Message),
    OpError.Conflict => Results.Conflict(r.Message),
    _ => Results.Ok(r.Value),
};

static bool IsLoopbackUrl(string url) =>
    Uri.TryCreate(url.Replace("+", "wildcard").Replace("*", "wildcard"), UriKind.Absolute, out var uri)
    && uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

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
