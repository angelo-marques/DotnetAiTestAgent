using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Detecta problemas de lógica no código-fonte (não nos testes).
/// Analisa cada classe individualmente e acumula os resultados.
/// A AgentThread compartilhada permite detectar padrões cross-class
/// como acoplamento temporal entre classes diferentes.
/// </summary>
public class LogicAnalysisAgent : BaseAgent<AnalyzeLogicRequest, LogicAnalysisResponse>
{
    public override string Name => "LogicAnalysisAgent";

    public LogicAnalysisAgent(IChatClient chat, ILogger<LogicAnalysisAgent> logger)
        : base(chat, logger) { }

    public override async Task<LogicAnalysisResponse> HandleAsync(
        AnalyzeLogicRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var all = new List<LogicIssue>();

        foreach (var cls in request.Classes)
        {
            var json   = await CompleteAsync(SystemPrompt,
                $"Classe {cls.ClassName}:\n```csharp\n{cls.SourceCode}\n```", thread, ct);

            var issues = TryDeserialize<List<LogicIssue>>(json) ?? new();
            issues.ForEach(i => { i.FilePath = cls.FilePath; i.ClassName = cls.ClassName; });
            all.AddRange(issues);
        }

        Logger.LogInformation("[{A}] {N} problemas de lógica encontrados", Name, all.Count);
        return new LogicAnalysisResponse(all);
    }

    private const string SystemPrompt = """
        Analise código C# buscando APENAS problemas reais de lógica:

        - NullReferenceException potenciais (analise fluxo de nullables)
        - Race conditions em código async/await
        - Código morto inacessível (dead code)
        - Condições sempre true ou sempre false
        - Erros de lógica: comparações erradas, off-by-one, loops infinitos
        - IDisposable sem Dispose ou sem using
        - ConfigureAwait faltando em bibliotecas (não em aplicações)

        Retorne JSON APENAS (sem markdown):
        [
          {
            "methodName": "...",
            "issueType": "NullRisk|RaceCondition|DeadCode|AlwaysTrue|LogicError|MissingDispose|MissingConfigureAwait",
            "description": "descrição clara do problema",
            "suggestion": "como corrigir",
            "line": 0,
            "severity": "Low|Medium|High|Critical",
            "estimatedFixMinutes": 0
          }
        ]
        Retorne [] se não houver problemas.
        """;
}
