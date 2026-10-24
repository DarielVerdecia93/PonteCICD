using System.Diagnostics;
using System.Text.Json;

namespace LegadosCI;

/// <summary>
/// Real IIS site/pool state for the panel and the poller, read from engine\sitestate.ps1.
/// It runs the script the same way Jobs.cs launches the engine (powershell.exe -NoProfile
/// -NonInteractive -ExecutionPolicy Bypass -File, WorkingDirectory = EngineDir) and caches the
/// result for a few seconds so the /state endpoint and the poller don't spawn a process per call.
/// Any error or timeout degrades to "not running / IIS not available" instead of throwing.
/// </summary>
public sealed class IisStateProbe(CiStore store, ILogger<IisStateProbe> log)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);
    static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(15);

    sealed record EnvState(string Name, string PoolState, string SiteState, bool Running);
    sealed record PreflightResult(bool Ok, bool IisAvailable, bool WebAppInit, string? Message);

    readonly object _lock = new();
    Dictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);
    bool _iisAvailable;
    DateTimeOffset _at = DateTimeOffset.MinValue;

    /// <summary>True when IIS could be queried on the last probe (pool/site state is meaningful).</summary>
    public bool IisAvailable
    {
        get { Refresh(); lock (_lock) return _iisAvailable; }
    }

    /// <summary>Whether the env's IIS app pool and website are both Started (false when IIS is unavailable).</summary>
    public bool Running(string env)
    {
        Refresh();
        lock (_lock) return _running.TryGetValue(env, out var r) && r;
    }

    void Refresh()
    {
        lock (_lock)
        {
            if (DateTimeOffset.Now - _at < CacheFor) return;
            _at = DateTimeOffset.Now; // reserve the slot so concurrent callers don't all probe
        }

        var running = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var available = false;
        try
        {
            var states = Run<List<EnvState>>("-All");
            if (states is not null)
            {
                available = true;
                foreach (var s in states) running[s.Name] = s.Running;
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "sitestate -All falhou; o IIS e tratado como nao disponivel");
        }

        lock (_lock)
        {
            _running = running;
            _iisAvailable = available;
        }
    }

    /// <summary>One-shot preflight (does not use the cache). True = IIS is available to operate.</summary>
    public bool PreflightOk()
    {
        try
        {
            var p = Run<PreflightResult>("-Preflight");
            return p?.IisAvailable == true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "sitestate -Preflight falhou");
            return false;
        }
    }

    T? Run<T>(string arg) where T : class
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = store.EngineDir,
        };
        foreach (var a in (string[])
                 ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                  "-File", Path.Combine(store.EngineDir, "sitestate.ps1"), arg])
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Nao foi possivel iniciar o powershell.exe.");
        var stdout = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit((int)RunTimeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
        return JsonSerializer.Deserialize<T>(stdout, CiStore.Json);
    }
}
