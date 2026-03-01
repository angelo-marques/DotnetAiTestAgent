using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Analisa qualidade de código: SOLID, code smells e complexidade.
///
/// CIRCUIT BREAKER POR CLASSE:
///   Máximo de MaxRetriesPerClass tentativas por classe para obter JSON válido.
///   Evita loop infinito quando o modelo insiste em responder com texto.
/// </summary>
public class QualityAnalysisAgent : BaseAgent<AnalyzeQualityRequest, QualityAnalysisResponse>
{
    private const int MaxRetriesPerClass = 3;

    public override string Name => "QualityAnalysisAgent";

    public QualityAnalysisAgent(IChatClient chat, ILogger<QualityAnalysisAgent> logger)
        : base(chat, logger) { }

    public override async Task<QualityAnalysisResponse> HandleAsync(
        AnalyzeQualityRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var all = new List<QualityIssue>();

        foreach (var cls in request.Classes)
        {
            var issues = await AnalyzeClassWithRetryAsync(cls, thread, ct);
            issues.ForEach(i => { i.FilePath = cls.FilePath; i.ClassName = cls.ClassName; });
            all.AddRange(issues);
        }

        Logger.LogInformation("[{A}] {N} problemas de qualidade encontrados", Name, all.Count);
        return new QualityAnalysisResponse(all);
    }

    private async Task<List<QualityIssue>> AnalyzeClassWithRetryAsync(
        Domain.Entities.CSharpClassInfo cls, AgentThread thread, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetriesPerClass; attempt++)
        {
            var prompt = attempt == 1
                ? BuildUserPrompt(cls.ClassName, cls.SourceCode)
                : BuildRetryPrompt(cls.ClassName, cls.SourceCode, attempt);

            var raw = await CompleteAsync(SystemPrompt, prompt, thread, ct);
            var issues = TryDeserialize<List<QualityIssue>>(raw);

            if (issues is not null)
                return issues;

            Logger.LogDebug("[{A}] Classe {C}: tentativa {T}/{M} sem JSON válido",
                Name, cls.ClassName, attempt, MaxRetriesPerClass);
        }

        Logger.LogWarning("[{A}] Classe {C}: pulada após {M} tentativas sem JSON válido",
            Name, cls.ClassName, MaxRetriesPerClass);
        return new();
    }

    private static string BuildUserPrompt(string className, string sourceCode) =>
        $"Classe: {className}\n\n```csharp\n{sourceCode}\n```";

    private static string BuildRetryPrompt(string className, string sourceCode, int attempt) =>
        $"TENTATIVA {attempt} — RESPONDA SOMENTE COM JSON ARRAY, SEM TEXTO.\n" +
        $"Se não houver problemas, responda: []\n\n" +
        $"Classe: {className}\n\n```csharp\n{sourceCode}\n```";

    private const string SystemPrompt = """
        Você é um analisador de qualidade de código C#. Sua ÚNICA saída deve ser um array JSON.
        NÃO escreva texto antes do JSON. NÃO escreva texto depois do JSON.
        NÃO use markdown. NÃO use ```json. Comece DIRETAMENTE com [.

        Detecte problemas de design:
        - Violações SOLID (SRP, OCP, LSP, ISP, DIP)
        - Code smells: God Class, Feature Envy, Long Method, Data Clump
        - Complexidade ciclomática acima de 10
        - Anti-patterns: Service Locator, Singleton abusivo

        Formato obrigatório (array JSON puro):
        [{"issueType":"SRP|OCP|LSP|ISP|DIP|GodClass|FeatureEnvy|LongMethod|DataClump|HighComplexity|AntiPattern","description":"problema","suggestion":"como refatorar","severity":"Low|Medium|High|Critical","cyclomaticComplexity":0,"estimatedFixMinutes":30}]

        Use severity exatamente assim: "Low", "Medium", "High" ou "Critical".
        Se não houver problemas: []
        """;
}