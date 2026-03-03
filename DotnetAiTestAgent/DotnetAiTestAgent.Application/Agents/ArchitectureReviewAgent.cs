using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Analisa a arquitetura do projeto a partir do grafo de dependências
/// extraído pelo Roslyn. Detecta dependências circulares, violações de
/// camadas e acoplamento excessivo entre componentes.
/// </summary>
public class ArchitectureReviewAgent : BaseAgent<ReviewArchitectureRequest, ArchitectureReviewResponse>
{
    public override string Name => "ArchitectureReviewAgent";

    public ArchitectureReviewAgent(IChatClient chat, PromptRepository prompts, ILogger<ArchitectureReviewAgent> logger)
        : base(chat, prompts, logger) { }

    public override async Task<ArchitectureReviewResponse> HandleAsync(
        ReviewArchitectureRequest request, AgentThread thread, CancellationToken ct = default)
    {
        // Constrói o grafo apenas com o necessário para análise — sem enviar source code completo
        var graph = request.Classes.Select(c => new
        {
            c.ClassName,
            c.Namespace,
            c.Dependencies,
            c.CyclomaticComplexity
        });

        var json = await CompleteAsync(Prompts.GetSystem(Name),
            JsonSerializer.Serialize(graph, new JsonSerializerOptions { WriteIndented = true }),
            thread, ct);

        var issues = TryDeserialize<List<ArchitectureIssue>>(json) ?? new();

        Logger.LogInformation("[{A}] {N} problemas de arquitetura encontrados", Name, issues.Count);
        return new ArchitectureReviewResponse(issues);
    }
}
