using LegadosCI;
using Xunit;

namespace LegadosCI.Tests;

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
}
