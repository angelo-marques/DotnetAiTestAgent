using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Domain.ValueObjects;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;
/// <summary>
/// Parseia o relatório XML do coverlet e identifica gaps de cobertura.
/// Prioriza gaps por severidade (cobertura abaixo de 40% = High, até 80% = Medium)
/// para orientar o TestWriterAgent na retroalimentação.
/// </summary>
public class CoverageReviewAgent : BaseAgent<ReviewCoverageRequest, CoverageResultResponse>
{
    private readonly CoverageParserPlugin _parser;
    private readonly FileSystemPlugin _fileSystem;

    public override string Name => "CoverageReviewAgent";

    public CoverageReviewAgent(
        IChatClient chat,
        PromptRepository prompts,
        CoverageParserPlugin parser,
        FileSystemPlugin fileSystem,
        ILogger<CoverageReviewAgent> logger)
        : base(chat, prompts, logger)
    {
        _parser = parser;
        _fileSystem = fileSystem;
    }

    public override async Task<CoverageResultResponse> HandleAsync(
        ReviewCoverageRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var xml = await _fileSystem.ReadCoverageReportAsync();
        var resultJson = _parser.ParseCoverage(xml);
        var result = TryDeserialize<CoverageResult>(resultJson) ?? new();

        Logger.LogInformation("[{A}] {L:F1}% linha | {B:F1}% branch | {G} gaps",
            Name, result.AverageCoverage, result.BranchCoverage, result.Gaps.Count);

        return new CoverageResultResponse(result.AverageCoverage, result.BranchCoverage, result.Gaps);
    }
}