using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Application.Reports;
using DotnetAiTestAgent.Domain.Enums;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Gera o relatório final do pipeline em Markdown.
/// Produz um único arquivo test-report.md com estatísticas gerais,
/// cobertura por classe e sugestões de melhoria priorizadas.
/// </summary>
public class ReportGeneratorAgent : BaseAgent<GenerateReportsRequest, ReportsGeneratedResponse>
{
    private readonly FileSystemPlugin _fileSystem;

    public override string Name => "ReportGeneratorAgent";

    public ReportGeneratorAgent(
        IChatClient chat,
        PromptRepository prompts,
        FileSystemPlugin fileSystem,
        ILogger<ReportGeneratorAgent> logger)
        : base(chat, prompts, logger) => _fileSystem = fileSystem;

    public override async Task<ReportsGeneratedResponse> HandleAsync(
        GenerateReportsRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var ctx = request.Context;
        var builder = new ReportBuilder(ctx);

        const string reportName = "test-report.md";
        await _fileSystem.WriteReportAsync(reportName, builder.BuildTestReport());
        Logger.LogInformation("[{A}] ✓ {F}", Name, reportName);

        return new ReportsGeneratedResponse([reportName]);
    }
}