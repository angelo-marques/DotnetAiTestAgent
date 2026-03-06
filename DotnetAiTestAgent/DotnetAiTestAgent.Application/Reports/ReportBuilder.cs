using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Domain.Enums;
using System.Text;
using System.Text.Json;

namespace DotnetAiTestAgent.Application.Reports;


/// <summary>
/// Constrói o conteúdo do relatório Markdown de testes.
/// Produz um único test-report.md com:
///   - Estatísticas gerais (testes gerados, fakes, issues)
///   - Tabela de classes e seus métodos de teste
///   - Sugestões de melhoria priorizadas por severidade
/// </summary>
public class ReportBuilder
{
    private readonly AgentContext _ctx;
    private readonly int _totalDebtMinutes;

    public ReportBuilder(AgentContext ctx)
    {
        _ctx = ctx;
        _totalDebtMinutes = ctx.LogicIssues.Sum(i => i.EstimatedFixMinutes)
                          + ctx.QualityIssues.Sum(i => i.EstimatedFixMinutes);
    }

    // ── Relatório principal ───────────────────────────────────────────────────

    /// <summary>
    /// Gera o test-report.md completo com 3 seções:
    ///   1. Estatísticas gerais
    ///   2. Classes e métodos com testes
    ///   3. Sugestões de melhoria priorizadas
    /// </summary>
    public string BuildTestReport()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Relatório de Testes — dotnet-ai-test-agent");
        sb.AppendLine($"**Data:** {DateTime.Now:dd/MM/yyyy HH:mm}  ");
        sb.AppendLine($"**Projeto:** `{_ctx.SourcePath}`");
        sb.AppendLine();

        sb.Append(BuildStatsSection());
        sb.AppendLine();
        sb.Append(BuildClassCoverageSection());
        sb.AppendLine();
        sb.Append(BuildImprovementsSection());

        return sb.ToString().Trim();
    }

    // ── Seção 1: Estatísticas ─────────────────────────────────────────────────

    private string BuildStatsSection()
    {
        var testStatus = _ctx.TestsAllPassed ? "✅ Todos passaram" : "⚠️ Há falhas";
        var issueStatus = _ctx.LogicIssues.Count == 0 && _ctx.QualityIssues.Count == 0
            ? "✅ Nenhum"
            : $"⚠️ {_ctx.LogicIssues.Count + _ctx.QualityIssues.Count} encontrados";
        var debtHours = Math.Round(_totalDebtMinutes / 60.0, 1);

        return $"""
            ## 📊 Estatísticas Gerais

            | Métrica | Valor |
            |---|---|
            | Classes analisadas | {_ctx.DiscoveredClasses.Count} |
            | Testes gerados | {_ctx.GeneratedTests.Count} |
            | Fakes gerados | {_ctx.GeneratedFakes.Count} |
            | Status dos testes | {testStatus} |
            | Problemas de lógica | {_ctx.LogicIssues.Count} |
            | Problemas de qualidade | {_ctx.QualityIssues.Count} |
            | Dívida técnica estimada | {debtHours}h |
            | Issues críticos | {_ctx.LogicIssues.Count(i => i.Severity == IssueSeverity.Critical)} |

            """;
    }

    // ── Seção 2: Classes e métodos ────────────────────────────────────────────

    private string BuildClassCoverageSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🧪 Classes e Métodos com Testes");
        sb.AppendLine();

        if (!_ctx.DiscoveredClasses.Any())
        {
            sb.AppendLine("_Nenhuma classe descoberta._");
            return sb.ToString();
        }

        // Índice dos arquivos de teste gerados para cruzamento rápido
        var testFileNames = _ctx.GeneratedTests
            .Select(t => Path.GetFileNameWithoutExtension(t).Replace("Tests", "").ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cls in _ctx.DiscoveredClasses.OrderBy(c => c.ClassName))
        {
            bool hasTest = testFileNames.Contains(cls.ClassName.ToLowerInvariant());
            var icon = hasTest ? "✅" : "⬜";

            sb.AppendLine($"### {icon} `{cls.ClassName}`");

            if (cls.PublicMethods.Any())
            {
                sb.AppendLine();
                sb.AppendLine("| Método | Retorno | Testado |");
                sb.AppendLine("|---|---|---|");

                foreach (var method in cls.PublicMethods.OrderBy(m => m.Name))
                {
                    var methodTested = hasTest ? "✅" : "—";
                    sb.AppendLine($"| `{method.Name}` | `{method.ReturnType}` | {methodTested} |");
                }

                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("_Sem métodos públicos detectados._");
                sb.AppendLine();
            }
        }

        // Resumo de cobertura por contagem
        var coveredCount = _ctx.DiscoveredClasses.Count(c =>
            testFileNames.Contains(c.ClassName.ToLowerInvariant()));
        var totalCount = _ctx.DiscoveredClasses.Count;
        var pct = totalCount > 0 ? Math.Round(coveredCount * 100.0 / totalCount, 1) : 0;

        sb.AppendLine($"> **{coveredCount}/{totalCount} classes com testes gerados ({pct}%)**");

        return sb.ToString();
    }

    // ── Seção 3: Sugestões de melhoria ────────────────────────────────────────

    private string BuildImprovementsSection()
    {
        var all = _ctx.LogicIssues
            .Select(i => (i.Severity, i.ClassName, i.Description, i.Suggestion, i.EstimatedFixMinutes, Source: "Lógica"))
            .Concat(_ctx.QualityIssues
                .Select(i => (i.Severity, i.ClassName, i.Description, i.Suggestion, i.EstimatedFixMinutes, Source: "Qualidade")))
            .OrderByDescending(i => i.Severity)
            .ToList();

        if (!all.Any())
        {
            return $"""
                ## 💡 Sugestões de Melhoria

                ✅ Nenhum problema de lógica ou qualidade encontrado.

                """;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 💡 Sugestões de Melhoria");
        sb.AppendLine();

        void AppendGroup(string label, string emoji, IEnumerable<(IssueSeverity, string ClassName, string Desc, string Sug, int Min, string Src)> items)
        {
            var list = items.ToList();
            if (!list.Any()) return;

            sb.AppendLine($"### {emoji} {label} ({list.Count})");
            sb.AppendLine();
            foreach (var (_, cls, desc, sug, min, src) in list)
            {
                sb.AppendLine($"- **[{src}]** `{cls}` — {desc}");
                sb.AppendLine($"  → _{sug}_ ⏱ {min}min");
            }
            sb.AppendLine();
        }

        AppendGroup("Alta prioridade", "🔴", all.Where(i => i.Severity >= IssueSeverity.High));
        AppendGroup("Média prioridade", "🟡", all.Where(i => i.Severity == IssueSeverity.Medium));
        AppendGroup("Baixa prioridade", "🟢", all.Where(i => i.Severity <= IssueSeverity.Low));

        var totalMin = all.Sum(i => i.EstimatedFixMinutes);
        sb.AppendLine($"> **Dívida técnica total: {Math.Round(totalMin / 60.0, 1)}h** ({totalMin}min)");

        return sb.ToString();
    }

    // ── Métodos legados mantidos para compatibilidade ─────────────────────────
    // Podem ser removidos se não houver outra referência no projeto.

    public string BuildExecutiveSummary() => BuildTestReport();

    public string BuildLogicReport()
    {
        var items = _ctx.LogicIssues
            .OrderByDescending(i => i.Severity)
            .Select(i => $"""
                ## [{i.Severity}] {i.ClassName}.{i.MethodName}
                **Tipo:** `{i.IssueType}` | **Linha:** {i.Line} | **Tempo:** {i.EstimatedFixMinutes}min
                **Problema:** {i.Description}
                **Sugestão:** {i.Suggestion}
                """);

        return $"# Problemas de Lógica ({_ctx.LogicIssues.Count})\n\n{string.Join("\n\n---\n\n", items)}";
    }

    public string BuildQualityReport()
    {
        var items = _ctx.QualityIssues
            .OrderByDescending(i => i.Severity)
            .Select(i => $"""
                ## [{i.Severity}] {i.ClassName} — {i.IssueType}
                **Complexidade:** {i.CyclomaticComplexity} | **Tempo:** {i.EstimatedFixMinutes}min
                **Problema:** {i.Description}
                **Sugestão:** {i.Suggestion}
                """);

        return $"# Qualidade de Código ({_ctx.QualityIssues.Count})\n\n{string.Join("\n\n---\n\n", items)}";
    }

    public string BuildArchitectureReport()
    {
        var items = _ctx.ArchitectureIssues
            .OrderByDescending(i => i.Severity)
            .Select(i => $"""
                ## [{i.Severity}] {i.IssueType}
                **De:** `{i.FromComponent}` → **Para:** `{i.ToComponent}`
                **Problema:** {i.Description}
                **Sugestão:** {i.Suggestion}
                """);

        return $"# Arquitetura ({_ctx.ArchitectureIssues.Count})\n\n{string.Join("\n\n---\n\n", items)}";
    }

    public string BuildImprovements() => BuildImprovementsSection();

    public string BuildDebtReport()
    {
        static string Row(string label, IEnumerable<Domain.Entities.LogicIssue> issues) =>
            $"| {label} | {issues.Count()} | {issues.Sum(i => i.EstimatedFixMinutes)}min |";

        return $"""
            # Dívida Técnica — {Math.Round(_totalDebtMinutes / 60.0, 1)}h estimadas

            | Severidade | Issues | Tempo |
            |---|---|---|
            {Row("Crítico", _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Critical))}
            {Row("Alto", _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.High))}
            {Row("Médio", _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Medium))}
            {Row("Baixo", _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Low))}
            """;
    }

    public string BuildJsonSummary() =>
        JsonSerializer.Serialize(new
        {
            GeneratedAt = DateTime.UtcNow,
            TestsGenerated = _ctx.GeneratedTests.Count,
            FakesGenerated = _ctx.GeneratedFakes.Count,
            TestsAllPassed = _ctx.TestsAllPassed,
            LogicIssues = _ctx.LogicIssues.Count,
            QualityIssues = _ctx.QualityIssues.Count,
            ArchitectureIssues = _ctx.ArchitectureIssues.Count,
            TechnicalDebtHours = Math.Round(_totalDebtMinutes / 60.0, 1),
            CriticalIssues = _ctx.LogicIssues.Count(i => i.Severity == IssueSeverity.Critical)
        }, new JsonSerializerOptions { WriteIndented = true });
}