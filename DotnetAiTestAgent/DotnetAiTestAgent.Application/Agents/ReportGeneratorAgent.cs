using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Domain.Enums;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Plugins;
using DotnetAiTestAgent.Application.Reports;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Gera os relatórios finais do pipeline em Markdown e JSON.
/// Delega a construção de cada relatório ao ReportBuilder (Infrastructure),
/// mantendo o agente focado apenas em orquestrar a geração e salvar os arquivos.
/// </summary>
public class ReportGeneratorAgent : BaseAgent<GenerateReportsRequest, ReportsGeneratedResponse>
{
    private readonly FileSystemPlugin _fileSystem;

    public override string Name => "ReportGeneratorAgent";

    public ReportGeneratorAgent(
        IChatClient chat,
        FileSystemPlugin fileSystem,
        ILogger<ReportGeneratorAgent> logger)
        : base(chat, logger) => _fileSystem = fileSystem;

    public override async Task<ReportsGeneratedResponse> HandleAsync(
        GenerateReportsRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var ctx = request.Context;
        var builder = new ReportBuilder(ctx);

        var reports = new Dictionary<string, string>
        {
            ["00-executive-summary.md"] = builder.BuildExecutiveSummary(),
            ["02-logic-issues.md"] = builder.BuildLogicReport(),
            ["03-quality-analysis.md"] = builder.BuildQualityReport(),
            ["05-architecture-issues.md"] = builder.BuildArchitectureReport(),
            ["06-improvement-suggestions.md"] = builder.BuildImprovements(),
            ["07-technical-debt.md"] = builder.BuildDebtReport(),
            ["08-full-summary.json"] = builder.BuildJsonSummary(),
        };

        var paths = new List<string>();
        foreach (var (name, content) in reports)
        {
            await _fileSystem.WriteReportAsync(name, content);
            paths.Add(name);
            Logger.LogInformation("[{A}] ✓ {F}", Name, name);
        }

        return new ReportsGeneratedResponse(paths);
    }
}