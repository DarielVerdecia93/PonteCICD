using PonteCICD;
using Xunit;

namespace PonteCICD.Tests;

public class EnvValidationTests
{
    // (c) ValidateSitePath accepts a valid absolute path, rejects '..', and rejects a path inside
    // another env's base.
    [Fact]
    public void ValidateSitePath_Empty_IsAccepted()
    {
        var (ok, error) = EnvValidation.ValidateSitePath("", []);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateSitePath_AbsolutePath_IsAccepted()
    {
        var (ok, error) = EnvValidation.ValidateSitePath(@"C:\sites\app", []);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateSitePath_RelativePath_IsRejected()
    {
        var (ok, _) = EnvValidation.ValidateSitePath(@"sites\app", []);
        Assert.False(ok);
    }

    [Fact]
    public void ValidateSitePath_DotDot_IsRejected()
    {
        var (ok, _) = EnvValidation.ValidateSitePath(@"C:\sites\..\app", []);
        Assert.False(ok);
    }

    [Fact]
    public void ValidateSitePath_InsideAnotherEnvBase_IsRejected()
    {
        var others = new[] { new EnvConfig { Name = "develop", SitePath = @"C:\sites\develop" } };
        var (ok, _) = EnvValidation.ValidateSitePath(@"C:\sites\develop\child", others);
        Assert.False(ok);
    }

    [Fact]
    public void ValidateSitePath_SiblingOfAnotherEnvBase_IsAccepted()
    {
        var others = new[] { new EnvConfig { Name = "develop", SitePath = @"C:\sites\develop" } };
        var (ok, _) = EnvValidation.ValidateSitePath(@"C:\sites\staging", others);
        Assert.True(ok);
    }

    [Theory]
    [InlineData("sitio.teste.br", "sitio.teste.br")]
    [InlineData("  Sitio.Teste.BR ", "sitio.teste.br")]   // trims and lower-cases
    [InlineData("app", "app")]
    [InlineData("a-b.c1.example.com", "a-b.c1.example.com")]
    [InlineData("", "")]                                   // empty = no host header
    [InlineData(null, "")]
    public void ValidateHostName_AcceptsAndNormalizes(string? input, string expected)
    {
        var (ok, value, error) = EnvValidation.ValidateHostName(input);
        Assert.True(ok);
        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://sitio.teste.br")]
    [InlineData("sitio.teste.br:4555")]
    [InlineData("sitio.teste.br/app")]
    [InlineData("sitio teste.br")]
    [InlineData("-sitio.teste.br")]
    [InlineData("sitio..teste.br")]
    [InlineData("sitio_x.teste.br")]
    [InlineData("*.teste.br")]
    public void ValidateHostName_RejectsSchemePortPathAndBadChars(string input)
    {
        var (ok, _, error) = EnvValidation.ValidateHostName(input);
        Assert.False(ok);
        Assert.NotEmpty(error!);
    }

    [Fact]
    public void ValidateHostUnique_RejectsANameUsedByAnotherEnvironment()
    {
        var others = new[] { new EnvConfig { Name = "develop", HostHeader = "Sitio.Teste.br" } };
        var (ok, error) = EnvValidation.ValidateHostUnique("sitio.teste.br", others);
        Assert.False(ok);
        Assert.Contains("develop", error);
        Assert.True(EnvValidation.ValidateHostUnique("outro.teste.br", others).ok);
        Assert.True(EnvValidation.ValidateHostUnique("", others).ok);
        Assert.False(EnvValidation.ValidateHostUnique("localhost", others).ok);
    }
}
