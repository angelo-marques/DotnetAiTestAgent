using System.Text.Json;
using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Domain.Enums;

namespace DotnetAiTestAgent.Core.Infrastructure.Reports;

/// <summary>
/// Constrói o conteúdo de cada relatório Markdown e JSON.
/// Extraído do ReportGeneratorAgent para manter o agente pequeno
/// e o conteúdo dos relatórios testável de forma isolada.
/// </summary>
public class ReportBuilder
{
    private readonly AgentContext _ctx;
    private readonly int _totalDebtMinutes;

    public ReportBuilder(AgentContext ctx)
    {
        _ctx              = ctx;
        _totalDebtMinutes = ctx.LogicIssues.Sum(i => i.EstimatedFixMinutes)
                          + ctx.QualityIssues.Sum(i => i.EstimatedFixMinutes);
    }

    public string BuildExecutiveSummary() => $"""
        # Resumo Executivo — dotnet-ai-test-agent
        **Data:** {DateTime.Now:dd/MM/yyyy HH:mm} | **Projeto:** {_ctx}

        ## Métricas Gerais

        | Métrica | Valor |
        |---|---|
        | Cobertura de linha | {_ctx.CurrentCoverage:F1}% |
        | Mutation Score | {_ctx.MutationScore:F1}% |
        | Classes analisadas | {_ctx.DiscoveredClasses.Count} |
        | Testes gerados | {_ctx.GeneratedTests.Count} |
        | Fakes gerados | {_ctx.GeneratedFakes.Count} |
        | Problemas de lógica | {_ctx.LogicIssues.Count} |
        | Problemas de qualidade | {_ctx.QualityIssues.Count} |
        | Problemas de arquitetura | {_ctx.ArchitectureIssues.Count} |
        | Dívida técnica total | {Math.Round(_totalDebtMinutes / 60.0, 1)}h |

        ## Issues Críticos
        {CriticalIssuesList()}
        """;

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

    public string BuildImprovements()
    {
        var all = _ctx.LogicIssues
            .Select(i => (i.Severity, i.Description, i.Suggestion, i.EstimatedFixMinutes))
            .Concat(_ctx.QualityIssues
                .Select(i => (i.Severity, i.Description, i.Suggestion, i.EstimatedFixMinutes)))
            .OrderByDescending(i => i.Severity)
            .ToList();

        static string Section(string label, IEnumerable<(IssueSeverity, string Desc, string Sug, int Min)> items) =>
            $"## {label}\n{string.Join("\n", items.Select(i => $"- {i.Desc} → **{i.Sug}** ({i.Min}min)"))}";

        return $"""
            # Sugestões de Melhoria Priorizadas

            {Section("🔴 Alta prioridade",   all.Where(i => i.Severity >= IssueSeverity.High))}

            {Section("🟡 Média prioridade",  all.Where(i => i.Severity == IssueSeverity.Medium))}

            {Section("🟢 Baixa prioridade",  all.Where(i => i.Severity <= IssueSeverity.Low))}
            """;
    }

    public string BuildDebtReport()
    {
        static string Row(string label, IEnumerable<Domain.Entities.LogicIssue> issues) =>
            $"| {label} | {issues.Count()} | {issues.Sum(i => i.EstimatedFixMinutes)}min |";

        return $"""
            # Dívida Técnica — {Math.Round(_totalDebtMinutes / 60.0, 1)}h estimadas

            | Severidade | Issues | Tempo |
            |---|---|---|
            {Row("Crítico", _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Critical))}
            {Row("Alto",    _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.High))}
            {Row("Médio",   _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Medium))}
            {Row("Baixo",   _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Low))}
            """;
    }

    public string BuildJsonSummary() =>
        JsonSerializer.Serialize(new
        {
            GeneratedAt        = DateTime.UtcNow,
            Coverage           = _ctx.CurrentCoverage,
            MutationScore      = _ctx.MutationScore,
            LogicIssues        = _ctx.LogicIssues.Count,
            QualityIssues      = _ctx.QualityIssues.Count,
            ArchitectureIssues = _ctx.ArchitectureIssues.Count,
            TechnicalDebtHours = Math.Round(_totalDebtMinutes / 60.0, 1),
            CriticalIssues     = _ctx.LogicIssues.Count(i => i.Severity == IssueSeverity.Critical)
        }, new JsonSerializerOptions { WriteIndented = true });

    private string CriticalIssuesList()
    {
        var criticals = _ctx.LogicIssues.Where(i => i.Severity == IssueSeverity.Critical).ToList();
        return criticals.Any()
            ? string.Join("\n", criticals.Select(i => $"- ❗ `{i.ClassName}.{i.MethodName}` — {i.Description}"))
            : "_Nenhum issue crítico encontrado_ ✅";
    }
}
