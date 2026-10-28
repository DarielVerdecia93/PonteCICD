using System.Diagnostics;
using System.Text;
using PonteCICD;
using Xunit;

namespace PonteCICD.Tests;

public class GitAuthTests
{
    [Fact]
    public void Apply_scopes_the_basic_auth_header_to_the_repo_host()
    {
        var psi = new ProcessStartInfo("git");
        GitAuth.Apply(psi, "https://dev.azure.com/org/proj/_git/repo", new GitSecret("", "s3cret"));

        Assert.Equal("1", psi.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://dev.azure.com/.extraheader", psi.Environment["GIT_CONFIG_KEY_0"]);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("pontecicd:s3cret"));
        Assert.Equal($"AUTHORIZATION: Basic {expected}", psi.Environment["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public void Apply_ignores_non_http_urls()
    {
        var psi = new ProcessStartInfo("git");
        GitAuth.Apply(psi, "git@github.com:org/repo.git", new GitSecret("u", "t"));
        Assert.False(psi.Environment.ContainsKey("GIT_CONFIG_COUNT") && psi.Environment["GIT_CONFIG_KEY_0"]?.StartsWith("http.") == true);
    }

    [Fact]
    public void Redact_hides_the_token_and_url_userinfo()
    {
        var text = "fatal: unable to access 'https://user:s3cret@github.com/o/r.git/': denied s3cret";
        var clean = GitAuth.Redact(text, "s3cret");
        Assert.DoesNotContain("s3cret", clean);
    }
}
