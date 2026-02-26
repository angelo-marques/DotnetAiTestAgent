using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Analisa qualidade de código: princípios SOLID, code smells e complexidade.
/// Foca em problemas de design que impactam manutenibilidade e testabilidade.
/// </summary>
public class QualityAnalysisAgent : BaseAgent<AnalyzeQualityRequest, QualityAnalysisResponse>
{
    public override string Name => "QualityAnalysisAgent";

    public QualityAnalysisAgent(IChatClient chat, ILogger<QualityAnalysisAgent> logger)
        : base(chat, logger) { }

    public override async Task<QualityAnalysisResponse> HandleAsync(
        AnalyzeQualityRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var all = new List<QualityIssue>();

        foreach (var cls in request.Classes)
        {
            var json   = await CompleteAsync(SystemPrompt,
                $"Classe {cls.ClassName}:\n```csharp\n{cls.SourceCode}\n```", thread, ct);

            var issues = TryDeserialize<List<QualityIssue>>(json) ?? new();
            issues.ForEach(i => { i.FilePath = cls.FilePath; i.ClassName = cls.ClassName; });
            all.AddRange(issues);
        }

        Logger.LogInformation("[{A}] {N} problemas de qualidade encontrados", Name, all.Count);
        return new QualityAnalysisResponse(all);
    }

    private const string SystemPrompt = """
        Analise qualidade de código C# identificando problemas de design:

        SOLID:
        - SRP: classe com mais de uma responsabilidade
        - OCP: difícil de estender sem modificar
        - LSP: subclasse viola contrato da base
        - ISP: interface muito ampla
        - DIP: dependência de implementação concreta

        Code Smells:
        - God Class (classe que sabe e faz demais)
        - Feature Envy (método usa mais dados de outra classe)
        - Long Method (método longo e difícil de entender)
        - Data Clump (grupo de dados que sempre andam juntos)
        - Primitive Obsession (uso de primitivos onde deveria haver Value Object)

        Outros:
        - Complexidade ciclomática acima de 10 por método
        - Anti-patterns: Service Locator, Singleton abusivo

        Retorne JSON APENAS (sem markdown):
        [
          {
            "issueType": "SRP|OCP|LSP|ISP|DIP|GodClass|FeatureEnvy|LongMethod|DataClump|HighComplexity|AntiPattern",
            "description": "descrição clara do problema",
            "suggestion": "como refatorar",
            "severity": "Low|Medium|High|Critical",
            "cyclomaticComplexity": 0,
            "estimatedFixMinutes": 0
          }
        ]
        """;
}
