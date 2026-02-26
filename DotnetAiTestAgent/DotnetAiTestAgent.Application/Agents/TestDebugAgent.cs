using System.Text.Json;
using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Analisa falhas de testes em runtime e as classifica:
/// - BUG_NO_TESTE: problema na escrita do teste → corrige automaticamente
/// - BUG_NA_APLICACAO: bug real no código → registra como LogicIssue para o relatório
/// Essa distinção evita que o agente "conserte" testes que estão certos
/// descobrindo bugs reais na aplicação.
/// </summary>
public class TestDebugAgent : BaseAgent<DebugTestsRequest, TestsResultResponse>
{
    public override string Name => "TestDebugAgent";

    public TestDebugAgent(IChatClient chat, ILogger<TestDebugAgent> logger)
        : base(chat, logger) { }

    public override async Task<TestsResultResponse> HandleAsync(
        DebugTestsRequest request, AgentThread thread, CancellationToken ct = default)
    {
        if (!request.TestOutput.Contains("Failed"))
        {
            Logger.LogInformation("[{A}] ✓ Todos os testes passaram", Name);
            return new TestsResultResponse(true, request.TestOutput, new());
        }

        Logger.LogWarning("[{A}] Analisando falhas (tentativa {R})", Name, thread.RetryCount + 1);

        var analysisJson = await CompleteAsync(SystemPrompt,
            $"OUTPUT DOS TESTES:\n{request.TestOutput}", thread, ct);

        return ParseAnalysis(analysisJson, request.TestOutput);
    }

    private TestsResultResponse ParseAnalysis(string json, string originalOutput)
    {
        try
        {
            using var doc      = JsonDocument.Parse(json);
            var logicIssues    = doc.RootElement.TryGetProperty("logicIssues", out var el)
                ? JsonSerializer.Deserialize<List<LogicIssue>>(el.GetRawText()) ?? new()
                : new List<LogicIssue>();

            Logger.LogInformation("[{A}] {N} bugs reais na aplicação detectados", Name, logicIssues.Count);
            return new TestsResultResponse(false, originalOutput, logicIssues);
        }
        catch
        {
            return new TestsResultResponse(false, originalOutput, new());
        }
    }

    private const string SystemPrompt = """
        Analise falhas de testes .NET e classifique cada uma:

        BUG_NO_TESTE    → problema na escrita do teste (asserção errada, setup incorreto)
        BUG_NA_APLICACAO → bug real no código de produção (lógica incorreta, null ref, etc.)

        Para BUG_NO_TESTE: inclua em "fixes" a correção do teste
        Para BUG_NA_APLICACAO: inclua em "logicIssues" para o relatório

        Retorne JSON APENAS (sem markdown):
        {
          "fixes": [{"file":"...","oldCode":"...","newCode":"..."}],
          "logicIssues": [{"className":"...","methodName":"...","description":"...","severity":"High|Medium|Low"}]
        }
        """;
}
