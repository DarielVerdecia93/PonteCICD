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
    public void A_fresh_install_has_no_users_and_no_default_account()
    {
        var users = NewStore();
        Assert.False(users.HasUsers);
        Assert.False(users.Verify("admin", "admin", out _));
        Assert.False(File.Exists(Path.Combine(_root, "secrets", "users.json")));
    }

    [Fact]
    public void SetAdmin_creates_the_account_and_stores_only_a_hash()
    {
        var users = NewStore();
        Assert.True(users.SetAdmin("dariel", "Segredo#Forte1", onlyIfEmpty: false, out var error), error);

        Assert.True(users.HasUsers);
        Assert.True(users.Verify("dariel", "Segredo#Forte1", out var info));
        Assert.False(info!.IsDefaultPassword);
        Assert.True(users.Verify("DARIEL", "Segredo#Forte1", out _)); // case-insensitive name
        Assert.False(users.Verify("dariel", "outra-coisa", out _));

        var file = File.ReadAllText(Path.Combine(_root, "secrets", "users.json"));
        Assert.DoesNotContain("Segredo#Forte1", file);
        // persists across restarts
        Assert.True(NewStore().Verify("dariel", "Segredo#Forte1", out _));
    }

    [Fact]
    public void SetAdmin_replaces_any_existing_account()
    {
        var users = NewStore();
        users.SetAdmin("antigo", "Senha-Antiga-1", onlyIfEmpty: false, out _);
        Assert.True(users.SetAdmin("novo", "Senha-Nova-22", onlyIfEmpty: false, out _));

        Assert.False(users.Verify("antigo", "Senha-Antiga-1", out _));
        Assert.True(users.Verify("novo", "Senha-Nova-22", out _));
    }

    [Fact]
    public void First_run_setup_only_works_while_there_is_no_administrator()
    {
        var users = NewStore();
        Assert.True(users.SetAdmin("primeiro", "Senha-Primeiro-1", onlyIfEmpty: true, out _));

        Assert.False(users.SetAdmin("intruso", "Senha-Intruso-1", onlyIfEmpty: true, out var error));
        Assert.NotEmpty(error);
        Assert.False(users.Verify("intruso", "Senha-Intruso-1", out _));
        Assert.True(users.Verify("primeiro", "Senha-Primeiro-1", out _));
    }

    [Theory]
    [InlineData("ab", "Senha-Valida-1")]            // name too short
    [InlineData("nome com espaco", "Senha-Valida-1")]
    [InlineData("nome/invalido", "Senha-Valida-1")]
    [InlineData("", "Senha-Valida-1")]
    [InlineData("admin", "curta")]                  // password too short
    [InlineData("admin", "")]
    public void Invalid_credentials_are_rejected(string name, string password)
    {
        var users = NewStore();
        Assert.False(users.SetAdmin(name, password, onlyIfEmpty: false, out var error));
        Assert.NotEmpty(error);
        Assert.False(users.HasUsers);
    }

    [Fact]
    public void Changing_the_password_rotates_the_stamp_and_persists()
    {
        var users = NewStore();
        users.SetAdmin("admin", "Senha-Inicial-1", onlyIfEmpty: false, out _);
        var before = users.StampOf("admin");

        Assert.True(users.ChangePassword("admin", "Senha-Inicial-1", "NovaSenha#2026", out _));

        Assert.False(users.Verify("admin", "Senha-Inicial-1", out _));
        Assert.True(users.Verify("admin", "NovaSenha#2026", out _));
        Assert.NotEqual(before, users.StampOf("admin"));
        Assert.True(NewStore().Verify("admin", "NovaSenha#2026", out _));
    }

    [Theory]
    [InlineData("errada", "NovaSenha#2026")]        // current password wrong
    [InlineData("Senha-Inicial-1", "curta")]        // too short
    [InlineData("Senha-Inicial-1", "Senha-Inicial-1")] // same as current
    public void Invalid_password_changes_are_rejected(string current, string next)
    {
        var users = NewStore();
        users.SetAdmin("admin", "Senha-Inicial-1", onlyIfEmpty: false, out _);

        Assert.False(users.ChangePassword("admin", current, next, out var error));
        Assert.NotEmpty(error);
        Assert.True(users.Verify("admin", "Senha-Inicial-1", out _)); // unchanged
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
