using PonteCICD;
using Xunit;

namespace PonteCICD.Tests;

public class PublishRulesTests
{
    [Fact]
    public void Validate_Null_IsEmpty()
    {
        var (ok, rules, error) = PublishRules.Validate(null);
        Assert.True(ok);
        Assert.Empty(rules);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_TrimsAndDropsBlankAndDuplicateRules()
    {
        var (ok, rules, _) = PublishRules.Validate(["  DBScripts ", "", null, "   ", "dbscripts", "*.sql"]);
        Assert.True(ok);
        Assert.Equal(["DBScripts", "*.sql"], rules);
    }

    [Theory]
    [InlineData("DBScripts")]
    [InlineData(@"\DBScripts")]
    [InlineData("/DBScripts/")]
    [InlineData(@".\DBScripts")]
    [InlineData(@"DBScripts\!!!!!After Deploy\19032026 novas mudanças de títulos físicos")]
    [InlineData("*.sql")]
    [InlineData(@"Content\**\*.map")]
    [InlineData("bin/*.pdb")]          // dentro de bin sim; a pasta inteira nao
    [InlineData("O'Brien (2)")]
    [InlineData("100% feito")]
    public void Validate_ValidRule_IsAccepted(string rule)
    {
        var (ok, rules, error) = PublishRules.Validate([rule]);
        Assert.True(ok, error);
        Assert.Equal([rule.Trim()], rules);
    }

    [Theory]
    [InlineData(@"C:\site\DBScripts")]   // absoluto
    [InlineData(@"..\outro")]
    [InlineData(@"DBScripts\..\bin")]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("a|b")]
    [InlineData("*")]                    // excluiria tudo
    [InlineData("**")]
    [InlineData(@"**\*")]
    [InlineData("bin")]                  // essenciais para o site arrancar
    [InlineData("/BIN/")]
    [InlineData("web.config")]
    public void Validate_InvalidRule_IsRejected(string rule)
    {
        var (ok, rules, error) = PublishRules.Validate(["DBScripts", rule]);
        Assert.False(ok);
        Assert.Empty(rules);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Validate_TooLongRule_IsRejected()
    {
        var (ok, _, _) = PublishRules.Validate([new string('a', PublishRules.MaxLength + 1)]);
        Assert.False(ok);
    }
}
