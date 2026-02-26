using System.Text.Json;
using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Analisa a arquitetura do projeto a partir do grafo de dependências
/// extraído pelo Roslyn. Detecta dependências circulares, violações de
/// camadas e acoplamento excessivo entre componentes.
/// </summary>
public class ArchitectureReviewAgent : BaseAgent<ReviewArchitectureRequest, ArchitectureReviewResponse>
{
    public override string Name => "ArchitectureReviewAgent";

    public ArchitectureReviewAgent(IChatClient chat, ILogger<ArchitectureReviewAgent> logger)
        : base(chat, logger) { }

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

        var json   = await CompleteAsync(SystemPrompt,
            JsonSerializer.Serialize(graph, new JsonSerializerOptions { WriteIndented = true }),
            thread, ct);

        var issues = TryDeserialize<List<ArchitectureIssue>>(json) ?? new();

        Logger.LogInformation("[{A}] {N} problemas de arquitetura encontrados", Name, issues.Count);
        return new ArchitectureReviewResponse(issues);
    }

    private const string SystemPrompt = """
        Analise o grafo de dependências de um projeto .NET e identifique:

        - Dependências circulares (A → B → A)
        - Violações de camadas (ex: Infrastructure referenciando Domain diretamente)
        - Acoplamento excessivo (classe com mais de 7 dependências diretas)
        - Componentes instáveis dependendo de componentes estáveis

        Retorne JSON APENAS (sem markdown):
        [
          {
            "issueType": "CircularDep|LayerViolation|HighCoupling|InstabilityViolation",
            "description": "descrição clara do problema",
            "fromComponent": "NomeClasseOuNamespace",
            "toComponent": "NomeClasseOuNamespace",
            "severity": "Low|Medium|High|Critical",
            "suggestion": "como resolver"
          }
        ]
        Retorne [] se a arquitetura estiver saudável.
        """;
}
