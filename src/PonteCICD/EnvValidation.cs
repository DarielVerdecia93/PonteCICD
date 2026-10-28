namespace PonteCICD;

/// <summary>
/// Pure validation helpers for the environment API, kept free of the web host so they can be unit tested.
/// </summary>
public static class EnvValidation
{
    /// <summary>
    /// SitePath is optional. When present it must be a fully-qualified Windows path, must not contain a
    /// '..' segment, and must not sit inside another environment's base folder (nor contain it).
    /// </summary>
    public static (bool ok, string? error) ValidateSitePath(string? path, IEnumerable<EnvConfig> others)
    {
        if (string.IsNullOrWhiteSpace(path)) return (true, null);
        path = path.Trim();

        if (!Path.IsPathFullyQualified(path))
            return (false, "A pasta do site deve ser um caminho absoluto (ex.: C:\\sites\\app).");

        var segments = path.Split('\\', '/');
        if (segments.Any(s => s == ".."))
            return (false, "A pasta do site não pode conter '..'.");

        var normalized = Normalize(path);
        foreach (var other in others)
        {
            if (string.IsNullOrWhiteSpace(other.SitePath)) continue;
            var otherNorm = Normalize(other.SitePath);
            if (normalized.Equals(otherNorm, StringComparison.OrdinalIgnoreCase)
                || IsInside(normalized, otherNorm)
                || IsInside(otherNorm, normalized))
                return (false, $"A pasta do site colide com a do ambiente '{other.Name}'.");
        }
        return (true, null);
    }

    /// <summary>Port must be in the usable range and not already used by another environment.</summary>
    public static (bool ok, string? error) ValidatePort(int port, IEnumerable<EnvConfig> others)
    {
        if (port < 1 || port > 65535)
            return (false, "A porta deve estar entre 1 e 65535.");
        if (others.Any(e => e.Port == port))
            return (false, $"A porta {port} já está em uso por outro ambiente.");
        return (true, null);
    }

    static readonly System.Text.RegularExpressions.Regex HostNameRx = new(
        @"^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Optional DNS name for the IIS binding (host header), e.g. "sitio.teste.br". Only the name: no scheme, port or path.
    /// Returns the normalized (trimmed, lower-case) value; empty means "no host header".
    /// </summary>
    public static (bool ok, string value, string? error) ValidateHostName(string? host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (h.Length == 0) return (true, "", null);
        if (!HostNameRx.IsMatch(h))
            return (false, h, "O nome DNS e invalido: use so letras, numeros, hifens e pontos (ex.: sitio.teste.br), sem http://, porta nem barras.");
        return (true, h, null);
    }

    /// <summary>The DNS name goes on port 80 of IIS, so two environments cannot share it.</summary>
    public static (bool ok, string? error) ValidateHostUnique(string host, IEnumerable<EnvConfig> others)
    {
        if (host.Length == 0) return (true, null);
        if (host is "localhost") return (false, "O nome DNS nao pode ser 'localhost'.");
        var clash = others.FirstOrDefault(e => string.Equals(e.HostHeader, host, StringComparison.OrdinalIgnoreCase));
        return clash is null ? (true, null) : (false, $"O nome DNS '{host}' ja esta a ser usado pelo ambiente '{clash.Name}'.");
    }

    static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    static bool IsInside(string child, string parent) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
