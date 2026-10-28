using Microsoft.Extensions.Logging.Abstractions;
using PonteCICD;
using Xunit;

namespace PonteCICD.Tests;

public class RepositoryAccessTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "pontecicd-access-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/proj/_git/repo", true, true)]   // URL + token
    [InlineData("https://dev.azure.com/org/proj/_git/repo", false, false)] // URL without token
    [InlineData("", true, false)]                                          // token without URL
    [InlineData(null, true, false)]
    [InlineData("   ", true, false)]
    [InlineData("https://user:tok@github.com/o/r.git", false, true)]       // credentials embedded in the URL (older installs)
    [InlineData("https://${AZURE_DEVOPS_PAT}@dev.azure.com/o/p/_git/r", false, true)]
    [InlineData("https://github.com/o/r@v2.git", false, false)]            // an '@' after the host is not user info
    public void Branches_are_only_available_with_a_URL_and_a_credential(string? url, bool hasToken, bool expected) =>
        Assert.Equal(expected, GitAuth.HasCredentials(url, hasToken));

    [Fact]
    public async Task Without_access_a_sync_wipes_the_on_disk_mirror_and_reports_it()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ci.json"), """{ "repoUrl": "", "environments": [] }""");
        // A mirror left behind by an earlier configuration, with git-style read-only object files.
        var objects = Path.Combine(_root, "mirror.git", "objects", "ab");
        Directory.CreateDirectory(objects);
        var obj = Path.Combine(objects, "cdef");
        File.WriteAllText(obj, "secret source code");
        File.SetAttributes(obj, FileAttributes.ReadOnly);

        var mirror = new Mirror(new CiStore(_root), new Notifier(), NullLogger<Mirror>.Instance);
        var changed = await mirror.SyncRemoteAsync(force: true, CancellationToken.None);

        Assert.False(changed);
        Assert.False(mirror.HasAccess);
        Assert.Empty(mirror.Branches);
        Assert.False(Directory.Exists(Path.Combine(_root, "mirror.git")));
        Assert.Contains("nao configurado", mirror.LastError);
    }

    [Fact]
    public async Task Reset_removes_the_mirror_even_when_nothing_is_wrong()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mirror.git", "refs"));
        File.WriteAllText(Path.Combine(_root, "ci.json"), """{ "repoUrl": "https://example.com/r.git", "environments": [] }""");
        var mirror = new Mirror(new CiStore(_root), new Notifier(), NullLogger<Mirror>.Instance);

        await mirror.ResetAsync();

        Assert.False(Directory.Exists(Path.Combine(_root, "mirror.git")));
    }
}
