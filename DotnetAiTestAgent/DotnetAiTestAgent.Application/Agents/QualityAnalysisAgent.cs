using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

public class QualityAnalysisAgent : BaseAgent<AnalyzeQualityRequest, QualityAnalysisResponse>
{
    public override string Name => "QualityAnalysisAgent";

    public QualityAnalysisAgent(IChatClient chat, PromptRepository prompts, ILogger<QualityAnalysisAgent> logger)
        : base(chat, prompts, logger) { }

    public override async Task<QualityAnalysisResponse> HandleAsync(
        AnalyzeQualityRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var all = new List<QualityIssue>();
        var system = GetSystemPrompt();

        foreach (var cls in request.Classes)
        {
            var raw = await CompleteAsync(system, $"Classe: {cls.ClassName}\n\n{cls.SourceCode}", thread, ct);
            var issues = await TryDeserializeWithCorrectionAsync<List<QualityIssue>>(raw, thread, ct)
                         ?? new();

            issues.ForEach(i => { i.FilePath = cls.FilePath; i.ClassName = cls.ClassName; });
            all.AddRange(issues);
        }

        Logger.LogInformation("[{A}] {N} problemas de qualidade encontrados", Name, all.Count);
        return new QualityAnalysisResponse(all);
    }
}
