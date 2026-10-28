using System.Globalization;
using MudBlazor;

namespace PonteCICD.Components;

/// <summary>Textos e formatos do painel (os mesmos do painel classico).</summary>
public static class Ui
{
    static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static string Short(string? commit) =>
        string.IsNullOrEmpty(commit) ? "—" : commit.Length > 7 ? commit[..7] : commit;

    public static string Ago(DateTimeOffset? d)
    {
        if (d is null) return "—";
        var s = (int)Math.Round((DateTimeOffset.Now - d.Value).TotalSeconds);
        return s switch
        {
            < 0 => $"em {-s}s",
            < 60 => $"há {s}s",
            < 3600 => $"há {s / 60} min",
            < 86400 => $"há {s / 3600} h",
            _ => $"há {s / 86400} d",
        };
    }

    /// <summary>deploy-info.json guarda a data como texto (ISO 8601).</summary>
    public static string Ago(string? iso) => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, out var d) ? Ago(d) : "—";

    public static string Date(DateTimeOffset? d) => d is null ? "—" : d.Value.ToLocalTime().ToString("g", Pt);

    public static string Secs(int s) => s % 60 == 0 && s >= 60 ? $"{s / 60} min" : $"{s}s";

    public static string Action(JobKind kind) => kind switch
    {
        JobKind.Deploy => "Deploy",
        JobKind.Rollback => "Rollback",
        JobKind.Start => "Iniciar",
        JobKind.Stop => "Parar",
        JobKind.Restart => "Reiniciar",
        JobKind.Purge => "Eliminar",
        _ => kind.ToString(),
    };

    public static string Status(JobStatus status) => status switch
    {
        JobStatus.Queued => "Na fila",
        JobStatus.Running => "A executar",
        JobStatus.Succeeded => "Concluído",
        JobStatus.Failed => "Falhou",
        _ => status.ToString(),
    };

    public static Color StatusColor(JobStatus status) => status switch
    {
        JobStatus.Running => Color.Primary,
        JobStatus.Succeeded => Color.Success,
        JobStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Cores do painel classico (Windows 11 / Fluent), com tema claro e escuro.</summary>
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0067c0",
            Secondary = "#8a5cf6",
            Success = "#0f7b0f",
            Error = "#c42b1c",
            Warning = "#9d5d00",
            Background = "#f3f3f3",
            Surface = "#ffffff",
            AppbarBackground = "rgba(243,243,243,.85)",
            AppbarText = "#1a1a1a",
            DrawerBackground = "#f9f9f9",
            TextPrimary = "#1a1a1a",
            TextSecondary = "#5c5c5c",
            LinesDefault = "rgba(0,0,0,.08)",
            TableLines = "rgba(0,0,0,.08)",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#60cdff",
            PrimaryContrastText = "#1a1a1a",
            Secondary = "#a78bfa",
            Success = "#6ccb5f",
            Error = "#ff99a4",
            Warning = "#fce100",
            Background = "#202020",
            Surface = "#2b2b2b",
            AppbarBackground = "rgba(32,32,32,.85)",
            AppbarText = "#ffffff",
            DrawerBackground = "#272727",
            TextPrimary = "#ffffff",
            TextSecondary = "#c8c8c8",
            LinesDefault = "rgba(255,255,255,.08)",
            TableLines = "rgba(255,255,255,.08)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Segoe UI Variable Text", "Segoe UI", "system-ui", "sans-serif"] },
            Button = new ButtonTypography { TextTransform = "none", FontWeight = "500" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" },
    };
}
