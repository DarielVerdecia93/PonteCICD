using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PonteCICD;

/// <summary>Credential (PAT) used to talk to the Git remote over HTTPS.</summary>
public sealed record GitSecret(string User, string Token);

/// <summary>
/// Stores the Git token entered in the panel and injects it into every git process the service (and the
/// PowerShell engine, which inherits the environment) starts. The token is encrypted with DPAPI
/// (machine scope, so it works for LocalSystem or any account) in secrets\git-credential.bin, is never
/// written to ci.json or put in a URL, and is sent as an HTTP header (GIT_CONFIG_* env vars, git >= 2.31)
/// scoped to the repository host. Works for Azure DevOps and GitHub (Basic auth with a PAT).
/// </summary>
public sealed class GitAuth
{
    const string DefaultUser = "pontecicd";

    readonly CiStore _store;
    readonly object _lock = new();
    GitSecret? _secret;

    /// <summary>Single instance, so the static Git helper and the job runner can reach it.</summary>
    public static GitAuth? Instance { get; private set; }

    public GitAuth(CiStore store)
    {
        _store = store;
        _secret = Load();
        Instance = this;
    }

    string FilePath => Path.Combine(_store.Root, "secrets", "git-credential.bin");

    public bool HasToken { get { lock (_lock) return _secret is not null; } }
    public string User { get { lock (_lock) return _secret?.User ?? ""; } }

    public void Save(string? user, string token)
    {
        var secret = new GitSecret(string.IsNullOrWhiteSpace(user) ? "" : user.Trim(), token.Trim());
        var plain = JsonSerializer.SerializeToUtf8Bytes(secret);
        var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, cipher);
        lock (_lock) _secret = secret;
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        lock (_lock) _secret = null;
    }

    GitSecret? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<GitSecret>(plain);
        }
        catch (Exception)
        {
            return null; // corrupted or from another machine: the user just enters the token again
        }
    }

    /// <summary>Adds the auth header to a git (or engine) process. <paramref name="url"/>/<paramref name="secret"/> override the stored ones (connection test).</summary>
    public static void Apply(ProcessStartInfo psi, string? url = null, GitSecret? secret = null)
    {
        var auth = Instance;
        secret ??= auth?.Current();
        if (secret is null) return;
        url ??= Mirror.ExpandEnvVars(auth?._store.Load().RepoUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;

        var scope = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/";
        var user = string.IsNullOrWhiteSpace(secret.User) ? DefaultUser : secret.User;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret.Token}"));
        psi.Environment["GIT_CONFIG_COUNT"] = "1";
        psi.Environment["GIT_CONFIG_KEY_0"] = $"http.{scope}.extraheader";
        psi.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: Basic {basic}";
    }

    GitSecret? Current() { lock (_lock) return _secret; }

    /// <summary>Removes the token (and any user:token@ in URLs) from git output before showing it.</summary>
    public static string Redact(string text, string? token)
    {
        if (!string.IsNullOrEmpty(token)) text = text.Replace(token, "***");
        return System.Text.RegularExpressions.Regex.Replace(text, @"(https?://)[^/\s@]+@", "$1***@");
    }
}
