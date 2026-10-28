using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PonteCICD;

public sealed record UserInfo(string Name, bool IsDefaultPassword);

/// <summary>
/// Panel users, in secrets\users.json (next to the encrypted git token). Passwords are never stored:
/// only a PBKDF2-SHA256 hash with a per-user salt. There is NO default account: the installer (or
/// "PonteCICD.exe --set-admin", or the panel's first-run setup page) creates the administrator.
/// </summary>
public sealed class UserStore
{
    public const int MinPasswordLength = 8;
    static readonly System.Text.RegularExpressions.Regex UserNameRx = new(@"^[A-Za-z0-9._-]{3,32}$");
    const int Iterations = 210_000;

    sealed class UserRecord
    {
        public string Name { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Iterations { get; set; } = UserStore.Iterations;
        public bool IsDefault { get; set; }
        /// <summary>Changes with every password change; sessions carrying an older stamp are rejected.</summary>
        public string Stamp { get; set; } = "";
    }

    sealed class UsersFile
    {
        public List<UserRecord> Users { get; set; } = [];
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string _path;
    readonly object _lock = new();
    UsersFile _data;

    public UserStore(CiStore store)
    {
        _path = Path.Combine(store.Root, "secrets", "users.json");
        _data = Load();
    }

    /// <summary>False until an administrator exists (fresh install, or users.json was removed/corrupted).</summary>
    public bool HasUsers
    {
        get { lock (_lock) return _data.Users.Count > 0; }
    }

    /// <summary>Checks a new account: user name 3-32 chars (letters, digits . _ -) and a password of at least 8.</summary>
    public static bool ValidateCredentials(string? name, string? password, out string error)
    {
        error = "";
        if (name is null || !UserNameRx.IsMatch(name.Trim()))
            error = "O nome de utilizador deve ter 3 a 32 caracteres (letras, numeros, ponto, hifen ou sublinhado).";
        else if ((password ?? "").Length < MinPasswordLength)
            error = $"A palavra-passe deve ter pelo menos {MinPasswordLength} caracteres.";
        return error.Length == 0;
    }

    /// <summary>
    /// Makes <paramref name="name"/> the one and only administrator, replacing any existing account
    /// (installer, reset command). With <paramref name="onlyIfEmpty"/> it refuses when a user already exists
    /// (first-run setup page), checked under the same lock so two requests cannot both win.
    /// </summary>
    public bool SetAdmin(string? name, string? password, bool onlyIfEmpty, out string error)
    {
        if (!ValidateCredentials(name, password, out error)) return false;
        lock (_lock)
        {
            if (onlyIfEmpty && _data.Users.Count > 0) { error = "Ja existe um administrador."; return false; }
            _data = new UsersFile { Users = [NewRecord(name!.Trim(), password!, isDefault: false)] };
            Save();
        }
        return true;
    }

    public UserInfo? Get(string name)
    {
        lock (_lock)
        {
            var u = Find(name);
            return u is null ? null : new UserInfo(u.Name, u.IsDefault);
        }
    }

    public string? StampOf(string name)
    {
        lock (_lock) return Find(name)?.Stamp;
    }

    /// <summary>Checks a password. Does the same work when the user doesn't exist, so timing doesn't reveal valid names.</summary>
    public bool Verify(string name, string password, out UserInfo? info)
    {
        UserRecord? u;
        lock (_lock) u = Find(name);
        var salt = u is null ? new byte[16] : Convert.FromBase64String(u.Salt);
        var computed = Derive(password, salt, u?.Iterations ?? Iterations);
        var ok = u is not null && CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(u.Hash));
        info = ok ? new UserInfo(u!.Name, u.IsDefault) : null;
        return ok;
    }

    public bool ChangePassword(string name, string current, string next, out string error)
    {
        error = "";
        if (!Verify(name, current, out _)) { error = "A palavra-passe atual esta incorreta."; return false; }
        if (next.Length < MinPasswordLength) { error = $"A nova palavra-passe deve ter pelo menos {MinPasswordLength} caracteres."; return false; }
        if (next == current) { error = "A nova palavra-passe deve ser diferente da atual."; return false; }
        lock (_lock)
        {
            var i = _data.Users.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { error = "Utilizador desconhecido."; return false; }
            _data.Users[i] = NewRecord(_data.Users[i].Name, next, isDefault: false);
            Save();
        }
        return true;
    }

    UserRecord? Find(string name) =>
        _data.Users.FirstOrDefault(u => u.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    static UserRecord NewRecord(string name, string password, bool isDefault)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new UserRecord
        {
            Name = name,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Derive(password, salt, Iterations)),
            Iterations = Iterations,
            IsDefault = isDefault,
            Stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        };
    }

    static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    UsersFile Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<UsersFile>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (Exception)
        {
            // corrupted file: no users, so the panel offers the first-run setup (from this computer)
        }
        return new();
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Slows down password guessing: 5 failures for a user lock that user for a minute.</summary>
public sealed class LoginThrottle
{
    const int MaxFailures = 5;
    static readonly TimeSpan LockFor = TimeSpan.FromMinutes(1);
    readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> _state = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string user, out int secondsLeft)
    {
        secondsLeft = 0;
        if (!_state.TryGetValue(user, out var s) || s.Until <= DateTimeOffset.Now) return false;
        secondsLeft = (int)Math.Ceiling((s.Until - DateTimeOffset.Now).TotalSeconds);
        return true;
    }

    public void Fail(string user) => _state.AddOrUpdate(user,
        _ => (1, DateTimeOffset.MinValue),
        (_, s) => s.Fails + 1 >= MaxFailures ? (0, DateTimeOffset.Now + LockFor) : (s.Fails + 1, s.Until));

    public void Succeed(string user) => _state.TryRemove(user, out _);
}

public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? Current, string? New);

public static class AuthEndpoints
{
    const string StampClaim = "stamp";

    public static void AddPanelAuth(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "pontecicd.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                // API: respond 401/403 instead of redirecting to a login page.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
                // A password change invalidates every other session.
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var users = ctx.HttpContext.RequestServices.GetRequiredService<UserStore>();
                    var name = ctx.Principal?.Identity?.Name;
                    var stamp = ctx.Principal?.FindFirstValue(StampClaim);
                    if (name is null || stamp is null || users.StampOf(name) != stamp)
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        services.AddAuthorization();
    }

    static ClaimsPrincipal PrincipalFor(UserStore users, string name)
    {
        var id = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(StampClaim, users.StampOf(name) ?? "")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(id);
    }

    public static void MapAuth(this RouteGroupBuilder api)
    {
        // Does the panel need its first-run setup (no administrator yet)?
        api.MapGet("/auth/status", (UserStore users) => Results.Ok(new { setupRequired = !users.HasUsers })).AllowAnonymous();

        // First-run setup: only while there is no administrator, and only from this computer.
        api.MapPost("/auth/setup", async (LoginRequest req, HttpContext ctx, UserStore users) =>
        {
            if (users.HasUsers) return Results.Conflict("Ja existe um administrador: inicie sessao.");
            if (ctx.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip))
                return Results.Text("A configuracao inicial so pode ser feita a partir deste computador.", "text/plain", statusCode: 403);
            if (!users.SetAdmin(req.Username, req.Password, onlyIfEmpty: true, out var error))
                return Results.BadRequest(error);
            var name = req.Username!.Trim();
            await ctx.SignInAsync(PrincipalFor(users, name));
            return Results.Ok(users.Get(name));
        }).AllowAnonymous();

        api.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, UserStore users, LoginThrottle throttle) =>
        {
            var name = (req.Username ?? "").Trim();
            if (throttle.IsLocked(name, out var wait))
                return Results.Text($"Demasiadas tentativas. Tente novamente dentro de {wait} s.", "text/plain", statusCode: 429);
            if (!users.Verify(name, req.Password ?? "", out var info))
            {
                throttle.Fail(name);
                return Results.Text("Utilizador ou palavra-passe incorretos.", "text/plain", statusCode: 401);
            }
            throttle.Succeed(name);
            await ctx.SignInAsync(PrincipalFor(users, info!.Name));
            return Results.Ok(info);
        }).AllowAnonymous();

        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync();
            return Results.Ok();
        });

        api.MapGet("/auth/me", (HttpContext ctx, UserStore users) =>
            users.Get(ctx.User.Identity?.Name ?? "") is { } info ? Results.Ok(info) : Results.Unauthorized());

        api.MapPut("/auth/password", async (ChangePasswordRequest req, HttpContext ctx, UserStore users) =>
        {
            var name = ctx.User.Identity?.Name ?? "";
            if (!users.ChangePassword(name, req.Current ?? "", req.New ?? "", out var error))
                return Results.BadRequest(error);
            // Keep this session alive with the new stamp; every other session is rejected.
            await ctx.SignInAsync(PrincipalFor(users, name));
            return Results.Ok(users.Get(name));
        });
    }
}

/// <summary>
/// "PonteCICD.exe --set-admin &lt;user&gt; [--password-file &lt;file&gt;]": creates/replaces the administrator without
/// the panel. Used by the installer and to recover a forgotten password. Without --password-file it asks for the
/// password in the console (not echoed).
/// </summary>
public static class AdminCli
{
    public static int Run(string[] args, UserStore users)
    {
        var name = ValueAfter(args, "--set-admin");
        var file = ValueAfter(args, "--password-file");
        string? password;
        if (file is not null)
        {
            try { password = File.ReadAllText(file).TrimEnd('\r', '\n'); }
            catch (Exception ex) { Console.Error.WriteLine($"ERRO: nao foi possivel ler o ficheiro da palavra-passe: {ex.Message}"); return 2; }
        }
        else
        {
            if (name is null) { Console.Error.WriteLine("Uso: PonteCICD.exe --set-admin <utilizador> [--password-file <ficheiro>]"); return 2; }
            password = ReadHidden("Nova palavra-passe: ");
            if (password != ReadHidden("Repita a palavra-passe: ")) { Console.Error.WriteLine("ERRO: as palavras-passe nao coincidem."); return 1; }
        }
        if (!users.SetAdmin(name, password, onlyIfEmpty: false, out var error)) { Console.Error.WriteLine($"ERRO: {error}"); return 1; }
        Console.WriteLine($"Administrador '{name!.Trim()}' guardado. Reinicie o servico PonteCICD para aplicar.");
        return 0;
    }

    static string? ValueAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
