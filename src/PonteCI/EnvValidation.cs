namespace PonteCI;

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

    static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    static bool IsInside(string child, string parent) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
