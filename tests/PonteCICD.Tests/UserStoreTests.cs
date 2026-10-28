using PonteCICD;
using Xunit;

namespace PonteCICD.Tests;

public class UserStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "pontecicd-users-" + Guid.NewGuid().ToString("N"));

    UserStore NewStore() => new(new CiStore(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void First_run_seeds_admin_with_the_default_password()
    {
        var users = NewStore();
        Assert.True(users.Verify("admin", "p4sw00rdAdm1n", out var info));
        Assert.True(info!.IsDefaultPassword);
        Assert.True(users.Verify("ADMIN", "p4sw00rdAdm1n", out _)); // case-insensitive name
        Assert.False(users.Verify("admin", "wrong", out _));
        Assert.False(users.Verify("nobody", "p4sw00rdAdm1n", out _));
    }

    [Fact]
    public void Password_is_not_stored_in_clear()
    {
        NewStore();
        var file = File.ReadAllText(Path.Combine(_root, "secrets", "users.json"));
        Assert.DoesNotContain("p4sw00rdAdm1n", file);
    }

    [Fact]
    public void Changing_the_password_clears_the_default_flag_and_rotates_the_stamp()
    {
        var users = NewStore();
        var before = users.StampOf("admin");

        Assert.True(users.ChangePassword("admin", "p4sw00rdAdm1n", "NovaSenha#2026", out _));

        Assert.False(users.Verify("admin", "p4sw00rdAdm1n", out _));
        Assert.True(users.Verify("admin", "NovaSenha#2026", out var info));
        Assert.False(info!.IsDefaultPassword);
        Assert.NotEqual(before, users.StampOf("admin"));
        // persists across restarts
        Assert.True(NewStore().Verify("admin", "NovaSenha#2026", out _));
    }

    [Theory]
    [InlineData("errada", "NovaSenha#2026")]      // current password wrong
    [InlineData("p4sw00rdAdm1n", "curta")]        // too short
    [InlineData("p4sw00rdAdm1n", "p4sw00rdAdm1n")] // default or same
    public void Invalid_changes_are_rejected(string current, string next)
    {
        var users = NewStore();
        Assert.False(users.ChangePassword("admin", current, next, out var error));
        Assert.NotEmpty(error);
        Assert.True(users.Verify("admin", "p4sw00rdAdm1n", out _)); // unchanged
    }

    [Fact]
    public void Throttle_locks_after_five_failures()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 4; i++) { t.Fail("admin"); Assert.False(t.IsLocked("admin", out _)); }
        t.Fail("admin");
        Assert.True(t.IsLocked("admin", out var wait));
        Assert.InRange(wait, 1, 60);
        t.Succeed("admin");
        Assert.False(t.IsLocked("admin", out _));
    }
}
