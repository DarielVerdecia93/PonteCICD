using PonteCICD.Components;
using Xunit;

namespace PonteCICD.Tests;

public class UiTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/classico/")]
    [InlineData("/?aviso=x")]
    [InlineData("/login?voltar=%2F")]
    public void SafeReturnUrl_KeepsLocalPaths(string url) => Assert.Equal(url, Ui.SafeReturnUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.example")]
    [InlineData(@"/\evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/\t/evil.example")]   // o navegador tira a tabulacao e fica //evil.example
    [InlineData("/\n/evil.example")]
    public void SafeReturnUrl_OtherSitesGoHome(string? url) => Assert.Equal("/", Ui.SafeReturnUrl(url));

    [Theory]
    [InlineData("main", "main")]
    [InlineData("feature/Nova-Tela", "nova-tela")]
    [InlineData("bugfix/CASH-123_valor", "cash-123-valor")]
    [InlineData("release/2.0", "release-2-0")]
    [InlineData("--x--", "x")]
    public void Slug_SuggestsEnvironmentName(string branch, string expected) => Assert.Equal(expected, Ui.Slug(branch));

    [Fact]
    public void Slug_IsAtMost40Characters() => Assert.Equal(40, Ui.Slug("feature/" + new string('a', 60)).Length);
}
