using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Executa Stryker.NET e retorna o mutation score.
/// O mutation score valida a QUALIDADE REAL dos testes —
/// um teste com 80% de cobertura de linha mas 30% de mutation score
/// significa que os testes não detectam bugs, apenas executam o código.
/// </summary>
public class MutationTestAgent : BaseAgent<RunMutationRequest, MutationResultResponse>
{
    private readonly StrykerPlugin _stryker;

    public override string Name => "MutationTestAgent";

    public MutationTestAgent(IChatClient chat, StrykerPlugin stryker, ILogger<MutationTestAgent> logger)
        : base(chat, logger) => _stryker = stryker;

    public override async Task<MutationResultResponse> HandleAsync(
        RunMutationRequest request, AgentThread thread, CancellationToken ct = default)
    {
        Logger.LogInformation("[{A}] Executando Stryker.NET (pode demorar alguns minutos)...", Name);

        var scoreStr = await _stryker.RunAsync(request.ProjectPath);
        var score    = double.TryParse(scoreStr,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.0;

        Logger.LogInformation("[{A}] Mutation score: {S:F1}%", Name, score);
        return new MutationResultResponse(score);
    }
}
