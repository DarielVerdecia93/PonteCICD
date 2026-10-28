using System.Text.RegularExpressions;

namespace PonteCICD;

public sealed record ProjectRequest(
    string? RepoUrl, string? SolutionFile, string? ProjectFile, string? PackagesSource, string? LocalRepoPath,
    string? TokenUser, string? Token, bool? ClearToken);

public sealed record ProjectView(
    string RepoUrl, string SolutionFile, string ProjectFile, string PackagesSource, string LocalRepoPath, bool HasToken, string TokenUser);

/// <summary>Pending: the repository is still being read (big repository). Error: saved, but the repository could not be read.</summary>
public sealed record ProjectSaved(bool Pending, int Branches, string? Error);

/// <summary>Acesso ao repositorio (URL + token). Partilhado pela API e pelo painel Blazor.</summary>
public sealed class ProjectService(CiStore store, GitAuth gitAuth, Mirror mirror, Notifier notifier)
{
    public ProjectView Get()
    {
        var c = store.Load();
        return new(c.RepoUrl, c.SolutionFile, c.ProjectFile, c.PackagesSource, c.LocalRepoPath, gitAuth.HasToken, gitAuth.User);
    }

    public async Task<OpResult<ProjectSaved>> SaveAsync(ProjectRequest req)
    {
        var url = (req.RepoUrl ?? "").Trim();
        if (url.Length > 0 && !Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase))
            return Op.Invalid<ProjectSaved>("O URL do repositorio deve comecar por https:// (ou http://).");
        // Com token guardado no painel, o URL nao leva «utilizador:token@» (nem ${VAR}): entraria em conflito com a autenticacao.
        if (!string.IsNullOrWhiteSpace(req.Token) || (gitAuth.HasToken && req.ClearToken != true))
            url = Regex.Replace(url, @"^(https?://)[^/@]*@", "$1", RegexOptions.IgnoreCase);
        var previousUrl = store.Load().RepoUrl;
        store.Update(c =>
        {
            c.RepoUrl = url;
            // Opcionais (so no ci.json): o painel nao os envia, por isso so se alteram quando vierem no pedido.
            if (req.SolutionFile is not null) c.SolutionFile = req.SolutionFile.Trim();
            if (req.ProjectFile is not null) c.ProjectFile = req.ProjectFile.Trim();
            if (req.PackagesSource is not null) c.PackagesSource = req.PackagesSource.Trim();
            if (req.LocalRepoPath is not null) c.LocalRepoPath = req.LocalRepoPath.Trim();
        });
        if (req.ClearToken == true) gitAuth.Clear();
        else if (!string.IsNullOrWhiteSpace(req.Token)) gitAuth.Save(req.TokenUser, req.Token);
        // Outro repositorio: nao ficam objetos do anterior no espelho.
        if (!string.IsNullOrWhiteSpace(previousUrl) && !string.Equals(previousUrl.Trim(), url, StringComparison.OrdinalIgnoreCase))
            await mirror.ResetAsync();
        // Le o remoto ja com a nova configuracao e responde com o resultado (quantas branches, ou o erro real).
        // Se demorar mais de 60 s (repositorio grande) segue em segundo plano e o painel mostra o progresso.
        var sync = Task.Run(async () => { try { await mirror.SyncRemoteAsync(force: true, CancellationToken.None); } catch (Exception) { /* fica em lastError */ } });
        var finished = await Task.WhenAny(sync, Task.Delay(TimeSpan.FromSeconds(60)));
        notifier.Changed();
        if (finished != sync) return Op.Ok(new ProjectSaved(Pending: true, Branches: 0, Error: null));
        // Sem URL e token nao ha repositorio: o "erro" e so o aviso de falta de configuracao (a resposta ja diz branches = 0).
        var syncError = mirror.HasAccess ? mirror.LastError : null;
        return Op.Ok(new ProjectSaved(
            Pending: false,
            Branches: mirror.Branches.Count(b => b.Source == BranchSource.Remote),
            Error: syncError is null ? null : GitAuth.Friendly(syncError, req.Token)));
    }

    /// <summary>Tests a URL + token without saving them (git ls-remote). Value: how many branches it sees.</summary>
    public async Task<OpResult<int>> TestAsync(ProjectRequest req, CancellationToken ct)
    {
        var url = Mirror.ExpandEnvVars((string.IsNullOrWhiteSpace(req.RepoUrl) ? store.Load().RepoUrl : req.RepoUrl).Trim());
        if (url.Length == 0) return Op.Invalid<int>("Indique o URL do repositorio.");
        var secret = string.IsNullOrWhiteSpace(req.Token) ? null : new GitSecret(req.TokenUser ?? "", req.Token.Trim());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var (code, output) = await Git.RunAsync(["ls-remote", "--heads", url], null, cts.Token, secret, url);
            if (code != 0) return Op.Invalid<int>($"A ligacao falhou: {GitAuth.Friendly(output, secret?.Token)}");
            return Op.Ok(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        catch (OperationCanceledException)
        {
            return Op.Invalid<int>("Tempo esgotado (30 s) a ligar ao repositorio.");
        }
    }
}
