using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

public class LogicAnalysisAgent : BaseAgent<AnalyzeLogicRequest, LogicAnalysisResponse>
{
    public override string Name => "LogicAnalysisAgent";

    public LogicAnalysisAgent(IChatClient chat, PromptRepository prompts, ILogger<LogicAnalysisAgent> logger)
        : base(chat, prompts, logger) { }

    public override async Task<LogicAnalysisResponse> HandleAsync(
        AnalyzeLogicRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var all = new List<LogicIssue>();
        var system = GetSystemPrompt();

        foreach (var cls in request.Classes)
        {
            var raw = await CompleteAsync(system, $"Classe: {cls.ClassName}\n\n{cls.SourceCode}", thread, ct);
            // TryDeserializeWithCorrectionAsync: se falhar, mostra a resposta
            // inválida ao LLM e pede auto-correção (até 2 vezes)
            var issues = await TryDeserializeWithCorrectionAsync<List<LogicIssue>>(raw, thread, ct)
                         ?? new();

            issues.ForEach(i => { i.FilePath = cls.FilePath; i.ClassName = cls.ClassName; });
            all.AddRange(issues);
        }

        Logger.LogInformation("[{A}] {N} problemas de lógica encontrados", Name, all.Count);
        return new LogicAnalysisResponse(all);
    }
}
