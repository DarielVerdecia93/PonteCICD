namespace PonteCICD;

/// <summary>
/// Regras de exclusao da publicacao (excludeFromPublish, globais e por ambiente). Aqui so se validam; quem as aplica e o
/// motor (common.ps1, ConvertTo-PublishRuleRegex) ao caminho de cada ficheiro dentro do site publicado. Sintaxe (como .gitignore):
/// <list type="bullet">
/// <item><c>DBScripts</c>, <c>*.sql</c>, <c>Thumbs.db</c>: um so nome vale em qualquer nivel;</item>
/// <item><c>\DBScripts</c> ou <c>/DBScripts</c>: so o da raiz; <c>DBScripts\Antigos</c>: caminho desde a raiz;</item>
/// <item><c>*</c> = texto dentro de um nivel, <c>?</c> = um caracter, <c>**</c> = qualquer numero de niveis.</item>
/// </list>
/// Cada regra exclui o caminho e tudo o que esta por baixo dele.
/// </summary>
public static class PublishRules
{
    public const int MaxLength = 260;

    // Sem eles o site nao arranca: uma regra que os apanhe inteiros e quase de certeza um engano.
    static readonly string[] Essential = ["bin", "Web.config"];

    /// <summary>Valida as regras e devolve-as sem espacos nem linhas vazias ou repetidas.</summary>
    public static (bool ok, List<string> rules, string? error) Validate(IEnumerable<string?>? input)
    {
        var rules = new List<string>();
        foreach (var raw in input ?? [])
        {
            var rule = (raw ?? "").Trim();
            if (rule.Length == 0) continue;
            if (rule.Length > MaxLength) return Fail($"A regra «{rule[..40]}…» é demasiado longa (máx. {MaxLength} caracteres).");
            if (rule.IndexOfAny(['<', '>', ':', '"', '|']) >= 0)
                return Fail($"Regra inválida «{rule}»: indique um caminho relativo ao projeto, sem unidade nem < > : \" |.");

            var segments = rule.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(s => s == ".").ToArray();   // .\DBScripts = \DBScripts
            if (segments.Length == 0) return Fail($"Regra inválida «{rule}»: indique uma pasta ou um ficheiro.");
            if (segments.Any(s => s is "." or ".."))
                return Fail($"Regra inválida «{rule}»: não pode conter «.» nem «..» como pasta.");
            if (segments.All(s => s.Trim('*').Length == 0))
                return Fail($"A regra «{rule}» excluiria tudo.");
            if (segments.Length == 1 && Essential.FirstOrDefault(e => e.Equals(segments[0], StringComparison.OrdinalIgnoreCase)) is { } essential)
                return Fail($"A regra «{rule}» excluiria {essential}, sem o qual o site não arranca.");

            if (!rules.Contains(rule, StringComparer.OrdinalIgnoreCase)) rules.Add(rule);
        }
        return (true, rules, null);

        static (bool, List<string>, string?) Fail(string error) => (false, [], error);
    }
}
