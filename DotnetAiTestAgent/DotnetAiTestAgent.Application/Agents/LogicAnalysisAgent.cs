using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Detecta problemas de lógica no código-fonte (não nos testes).
///
/// CIRCUIT BREAKER POR CLASSE:
///   Cada classe tem no máximo MaxRetriesPerClass tentativas de parsear JSON válido.
///   Se o modelo insistir em responder com texto, a classe é pulada e o pipeline continua.
///   Isso evita o loop infinito de 30+ minutos observado no log.
/// </summary>
public class LogicAnalysisAgent : BaseAgent<AnalyzeLogicRequest, LogicAnalysisResponse>
{
    private const int MaxRetriesPerClass = 3;

    public override string Name => "LogicAnalysisAgent";

    public LogicAnalysisAgent(IChatClient chat, ILogger<LogicAnalysisAgent> logger)
        : base(chat, logger) { }

    public override async Task<LogicAnalysisResponse> HandleAsync(
        AnalyzeLogicRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var all = new List<LogicIssue>();

        foreach (var cls in request.Classes)
        {
            var issues = await AnalyzeClassWithRetryAsync(cls, thread, ct);
            issues.ForEach(i => { i.FilePath = cls.FilePath; i.ClassName = cls.ClassName; });
            all.AddRange(issues);
        }

        Logger.LogInformation("[{A}] {N} problemas de lógica encontrados", Name, all.Count);
        return new LogicAnalysisResponse(all);
    }

    private async Task<List<LogicIssue>> AnalyzeClassWithRetryAsync(
        Domain.Entities.CSharpClassInfo cls, AgentThread thread, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetriesPerClass; attempt++)
        {
            // Na primeira tentativa usa o prompt padrão.
            // Nas tentativas seguintes adiciona reforço de formato para corrigir o comportamento.
            var prompt = attempt == 1
                ? BuildUserPrompt(cls.ClassName, cls.SourceCode)
                : BuildRetryPrompt(cls.ClassName, cls.SourceCode, attempt);

            var raw = await CompleteAsync(SystemPrompt, prompt, thread, ct);
            var issues = TryDeserialize<List<LogicIssue>>(raw);

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
        $"TENTATIVA {attempt} — RESPONDA SOMENTE COM JSON, SEM TEXTO ANTES OU DEPOIS.\n" +
        $"Se não houver problemas, responda: []\n\n" +
        $"Classe: {className}\n\n```csharp\n{sourceCode}\n```";

    // Prompt intencionalmente curto e direto.
    // Modelos 7B tendem a ignorar prompts longos e a adicionar explicações extras.
    // Quanto mais simples o prompt, maior a chance de o modelo retornar JSON puro.
    private const string SystemPrompt = """
        Você é um analisador de código C#. Sua ÚNICA saída deve ser um array JSON.
        NÃO escreva texto antes do JSON. NÃO escreva texto depois do JSON.
        NÃO use markdown. NÃO use ```json. Comece DIRETAMENTE com [ ou {.

        Detecte problemas de lógica reais:
        - NullReferenceException potenciais
        - Race conditions em async/await
        - Dead code (código inacessível)
        - Loops infinitos ou condições sempre true/false
        - IDisposable sem Dispose
        - Off-by-one errors

        Formato obrigatório (array JSON puro):
        [{"methodName":"nome","issueType":"NullRisk|RaceCondition|DeadCode|LogicError|MissingDispose","description":"problema","suggestion":"como corrigir","line":0,"severity":"Low|Medium|High|Critical","estimatedFixMinutes":5}]

        Se não houver problemas: []
        """;
}