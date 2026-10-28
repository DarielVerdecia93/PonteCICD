using PonteCICD;
using Xunit;

namespace PonteCICD.Tests;

public class ProjectDetectorTests
{
    const string WebGuid = "<ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}</ProjectTypeGuids>";
    const string LibraryProject = "<Project><PropertyGroup><OutputType>Library</OutputType></PropertyGroup></Project>";

    static DetectedProject Detect(string[] files, Dictionary<string, string>? contents = null)
    {
        // The detector only reads the candidate projects: build their text like the git reader would.
        var read = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ProjectDetector.ProjectCandidates(files))
            read[path] = contents is not null && contents.TryGetValue(path, out var t) ? t : LibraryProject;
        return ProjectDetector.Choose(files, read);
    }

    [Fact]
    public void Picks_the_MVC_application_over_the_WCF_service_projects_that_share_its_project_type()
    {
        // Same layout as the real ENSE repository: three "web application" projects, only one is the UI.
        string[] files =
        [
            "EMNC.BalcaoUnico.sln",
            "ENMC.BalcaoUnico.UI.WebApplication/ENMC.BalcaoUnico.UI.WebApplication.sln",
            "ENMC.BalcaoUnico.UI.WebApplication/ENMC.BalcaoUnico.UI.WebApplication.csproj",
            "ENMC.BalcaoUnico.UI.WebApplication/Web.config",
            "ENMC.BalcaoUnico.UI.WebApplication/Global.asax",
            "ENMC.BalcaoUnico.UI.WebApplication/Views/Home/Index.cshtml",
            "ENMC.BalcaoUnico.UI.WebApplication/Content/site.css",
            "ENMC.BalcaoUnico.UI.WebApplication/Scripts/app.js",
            "BalcaoUnicoWebServices/ENMC.BalcaoUnico.WebServices.csproj",
            "BalcaoUnicoWebServices/Web.config",
            "BalcaoUnicoWebServices/Pessoas.svc",
            "RefDataENMC_Wcf/Ex_RaidServices_Wcf.csproj",
            "RefDataENMC_Wcf/Web.config",
            "RefDataENMC_Wcf/Raid.svc",
            "Dominio/Dominio.csproj",
        ];
        var contents = files.Where(ProjectDetector.IsProjectFile).Take(3).ToDictionary(f => f, _ => WebGuid);

        var d = Detect(files, contents);

        Assert.Equal(@"ENMC.BalcaoUnico.UI.WebApplication\ENMC.BalcaoUnico.UI.WebApplication.csproj", d.Project);
        Assert.Equal("EMNC.BalcaoUnico.sln", d.Solution); // the root solution, not the nested one
        Assert.DoesNotContain(d.Projects, p => p.StartsWith("Dominio"));
    }

    [Fact]
    public void Ignores_test_projects_even_when_they_look_like_web_apps()
    {
        string[] files = ["App.sln", "Site/Site.csproj", "Site/Web.config", "Site.Tests/Site.Tests.csproj", "Site.Tests/Web.config"];
        var contents = new Dictionary<string, string> { ["Site/Site.csproj"] = WebGuid, ["Site.Tests/Site.Tests.csproj"] = WebGuid };

        Assert.Equal(@"Site\Site.csproj", Detect(files, contents).Project);
    }

    [Fact]
    public void Recognises_SDK_style_web_projects()
    {
        string[] files = ["src/Api/Api.csproj"];
        var contents = new Dictionary<string, string> { ["src/Api/Api.csproj"] = @"<Project Sdk=""Microsoft.NET.Sdk.Web""></Project>" };

        var d = Detect(files, contents);
        Assert.Equal(@"src\Api\Api.csproj", d.Project);
        Assert.Null(d.Solution);
    }

    [Fact]
    public void Returns_no_project_when_nothing_is_a_web_application()
    {
        string[] files = ["Lib.sln", "Lib/Lib.csproj", "Console/Console.csproj"];

        var d = Detect(files);
        Assert.Null(d.Project);
        Assert.Equal("Lib.sln", d.Solution);
        Assert.Empty(d.Projects);
    }

    [Fact]
    public void Skips_packages_bin_and_obj_folders()
    {
        string[] files = ["packages/Foo/Foo.csproj", "Site/bin/Old.csproj", "Site/Site.csproj", "Site/Web.config"];
        Assert.Equal(["Site/Site.csproj"], ProjectDetector.ProjectCandidates(files));
    }

    [Fact]
    public void A_web_project_at_the_repository_root_is_supported()
    {
        string[] files = ["Site.csproj", "Web.config", "Site.sln", "Views/Home/Index.cshtml"];
        var contents = new Dictionary<string, string> { ["Site.csproj"] = WebGuid };

        var d = Detect(files, contents);
        Assert.Equal("Site.csproj", d.Project);
        Assert.Equal("Site.sln", d.Solution);
    }
}
