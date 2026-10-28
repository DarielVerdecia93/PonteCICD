using System.Text.RegularExpressions;

namespace PonteCICD;

/// <summary>What was found in a branch: the solution to restore and the web project to publish (repo-relative, '\' separated).</summary>
public sealed record DetectedProject(string? Solution, string? Project, IReadOnlyList<string> Solutions, IReadOnlyList<string> Projects);

/// <summary>
/// Finds the solution (.sln) and the ASP.NET web project (.csproj/.vbproj) of a branch from the repository itself,
/// so nobody has to type them in or keep a local copy of the code. Reads only the git tree and a few project files.
/// </summary>
public static class ProjectDetector
{
    // "Web Application" project type GUID (classic ASP.NET .NET Framework) and its MSBuild targets.
    const string WebAppGuid = "349C5851-65DF-11DA-9384-00065B846F21";
    const int MaxProjectsToRead = 60;
    static readonly Regex SkipPath = new(@"(^|/)(packages|node_modules|\.git|bin|obj)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TestName = new(@"(\.|-|_)?(tests?|specs?|unittests?|integrationtests?)(\.|-|_|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    public static bool IsSolutionFile(string path) => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

    /// <summary>Candidate project files whose content is worth reading (cheap filters first).</summary>
    public static List<string> ProjectCandidates(IEnumerable<string> files) =>
        files.Where(f => IsProjectFile(f) && !SkipPath.IsMatch(f))
             .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
             .Take(MaxProjectsToRead).ToList();

    /// <summary>Pure decision, unit-tested: files = git tree paths ('/'), contents = text of the candidate projects.</summary>
    public static DetectedProject Choose(IReadOnlyList<string> files, IReadOnlyDictionary<string, string> contents)
    {
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        var scored = new List<(string Path, int Score)>();
        foreach (var (path, text) in contents)
        {
            var score = 0;
            if (text.Contains(WebAppGuid, StringComparison.OrdinalIgnoreCase)
                || text.Contains("Microsoft.WebApplication.targets", StringComparison.OrdinalIgnoreCase)) score = 100;
            else if (Regex.IsMatch(text, @"Sdk\s*=\s*""Microsoft\.NET\.Sdk\.Web""", RegexOptions.IgnoreCase)) score = 90;
            if (score == 0) continue;

            var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] + "/" : "";
            if (fileSet.Contains(dir + "Web.config")) score += 10;
            // Varios projetos web partilham o mesmo tipo: a aplicacao distingue-se pelo conteudo (vistas, recursos), e os
            // servicos WCF pelos ficheiros .svc. So se olha para a arvore do git (sem ler mais ficheiros).
            var inDir = files.Where(f => dir.Length == 0 || f.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList();
            bool Has(Func<string, bool> pred) => inDir.Any(pred);
            if (Has(f => f.EndsWith(".svc", StringComparison.OrdinalIgnoreCase))) score -= 40;
            if (Has(f => f.StartsWith(dir + "Views/", StringComparison.OrdinalIgnoreCase))) score += 30;
            if (Has(f => f.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))) score += 20;
            if (Has(f => f.Equals(dir + "Global.asax", StringComparison.OrdinalIgnoreCase))) score += 10;
            if (Has(f => f.StartsWith(dir + "Content/", StringComparison.OrdinalIgnoreCase)
                      || f.StartsWith(dir + "Scripts/", StringComparison.OrdinalIgnoreCase))) score += 10;
            if (TestName.IsMatch(Path.GetFileNameWithoutExtension(path))) score -= 60;
            scored.Add((path, score));
        }
        var webProjects = scored.Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Path.Count(c => c == '/')).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path).ToList();

        var solutions = files.Where(f => IsSolutionFile(f) && !SkipPath.IsMatch(f))
            .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var project = webProjects.FirstOrDefault();
        // Several solutions: prefer the shallowest one that sits above the chosen project.
        var solution = solutions.FirstOrDefault(s => project is not null && IsAbove(s, project)) ?? solutions.FirstOrDefault();

        string Win(string p) => p.Replace('/', '\\');
        return new DetectedProject(solution is null ? null : Win(solution), project is null ? null : Win(project),
            solutions.Select(Win).ToList(), webProjects.Select(Win).ToList());
    }

    static bool IsAbove(string solution, string project)
    {
        var dir = solution.Contains('/') ? solution[..solution.LastIndexOf('/')] + "/" : "";
        return dir.Length == 0 || project.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<DetectedProject?> DetectAsync(string mirrorDir, string commit, CancellationToken ct)
    {
        var (code, output) = await Git.RunAsync(["-c", "core.quotepath=false", "ls-tree", "-r", "--name-only", commit], mirrorDir, ct);
        if (code != 0) return null;
        var files = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ProjectCandidates(files))
        {
            var (c, text) = await Git.RunAsync(["show", $"{commit}:{path}"], mirrorDir, ct);
            if (c == 0) contents[path] = text;
        }
        return Choose(files, contents);
    }
}
