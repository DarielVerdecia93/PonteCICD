using System.Text.Json;
using LegadosCI;
using Xunit;

namespace LegadosCI.Tests;

public class CiStoreTests
{
    // (a) A legacy ci.json (with iisExpress, no sitePath) round-trips through CiStore.Json so that the
    // reserialized output drops 'iisExpress' and includes an empty 'sitePath' on each environment.
    [Fact]
    public void LegacyConfig_RoundTrip_DropsIisExpress_AndAddsEmptySitePath()
    {
        const string legacy = """
        {
          "repoUrl": "https://example/repo",
          "iisExpress": "C:\\Program Files\\IIS Express\\iisexpress.exe",
          "preserve": [ "UploadFiles" ],
          "keepBackups": 20,
          "environments": [
            { "name": "develop", "branch": "main", "source": "remote", "port": 8090 }
          ]
        }
        """;

        var cfg = JsonSerializer.Deserialize<CiConfig>(legacy, CiStore.Json);
        Assert.NotNull(cfg);

        var reserialized = JsonSerializer.Serialize(cfg, CiStore.Json);
        using var doc = JsonDocument.Parse(reserialized);
        var rootEl = doc.RootElement;

        Assert.False(rootEl.TryGetProperty("iisExpress", out _), "iisExpress should be dropped.");
        Assert.True(rootEl.TryGetProperty("keepReleases", out _), "keepReleases should be present.");

        var env = rootEl.GetProperty("environments")[0];
        Assert.True(env.TryGetProperty("sitePath", out var sp));
        Assert.Equal("", sp.GetString());
    }

    // (b) SiteName/AppPoolName default to 'LegadosCI-<name>' when empty.
    [Fact]
    public void DefaultSiteName_IsPrefixedWithEnvName()
    {
        Assert.Equal("LegadosCI-develop", CiStore.DefaultSiteName("develop"));
        Assert.Equal("LegadosCI-prod", CiStore.DefaultSiteName("prod"));
    }

    // (d) ActiveReleaseDir returns releases\<id> when current.txt is present and the dir exists,
    // and falls back to site\ when current.txt is absent.
    [Fact]
    public void ActiveReleaseDir_UsesReleaseWhenCurrentTxtPresent_ElseFallsBackToSite()
    {
        var root = Path.Combine(Path.GetTempPath(), "legadosci-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var envBase = Path.Combine(root, "envs", "develop");
            Directory.CreateDirectory(Path.Combine(envBase, "site"));

            // Write ci.json so the store can resolve the env's base (no SitePath -> default base).
            var cfg = new CiConfig { Environments = { new EnvConfig { Name = "develop", Port = 8090 } } };
            File.WriteAllText(Path.Combine(root, "ci.json"), JsonSerializer.Serialize(cfg, CiStore.Json));

            var store = new CiStore(root);

            // No current.txt -> legacy site fallback.
            Assert.Equal(Path.Combine(envBase, "site"), store.ActiveReleaseDir("develop"));

            // current.txt naming an existing release -> that release.
            const string id = "20261024-161500_ff3b1d6";
            var release = Path.Combine(envBase, "releases", id);
            Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(envBase, "current.txt"), id); // writer uses no trailing newline
            Assert.Equal(release, store.ActiveReleaseDir("develop"));

            // current.txt naming a missing release -> fall back to site.
            File.WriteAllText(Path.Combine(envBase, "current.txt"), "does-not-exist");
            Assert.Equal(Path.Combine(envBase, "site"), store.ActiveReleaseDir("develop"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
