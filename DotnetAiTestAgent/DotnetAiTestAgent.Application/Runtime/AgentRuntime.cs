using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Domain.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Runtime;

/// <summary>
/// Implementação do IAgentRuntime — roteador central de mensagens do Agent Framework.
///
/// Responsabilidades:
///   - Registrar agentes associando tipos de mensagem aos handlers
///   - Rotear mensagens ao agente correto pelo tipo (sem acoplamento direto)
///   - Gerenciar AgentThreads por CorrelationId (histórico isolado por execução)
///   - Executar retry com backoff e propagação do RetryCount na thread
///
/// Por que não usar switch/if para rotear:
///   O dicionário indexado por Type garante O(1) no roteamento e permite
///   registrar novos agentes sem modificar o runtime — Open/Closed Principle.
/// </summary>
public class AgentRuntime : IAgentRuntime
{
    private readonly Dictionary<Type, Func<AgentRequest, AgentThread, CancellationToken, Task<AgentResponse>>>
        _handlers = new();

    private readonly Dictionary<string, AgentThread> _threads = new();
    private readonly ILogger<AgentRuntime> _logger;

    public AgentRuntime(ILogger<AgentRuntime> logger) => _logger = logger;

    public IAgentRuntime Register<TReq, TRes>(IAgent<TReq, TRes> agent)
        where TReq : AgentRequest
        where TRes : AgentResponse
    {
        _handlers[typeof(TReq)] = async (req, thread, ct) =>
        {
            _logger.LogDebug("[Runtime] {Type} → {Agent}", typeof(TReq).Name, agent.Name);
            return await agent.HandleAsync((TReq)req, thread, ct);
        };

        _logger.LogDebug("[Runtime] Registrado: {Agent} ({Req} → {Res})",
            agent.Name, typeof(TReq).Name, typeof(TRes).Name);

        return this;
    }

    public async Task<TRes> SendAsync<TReq, TRes>(TReq request, CancellationToken ct = default)
        where TReq : AgentRequest
        where TRes : AgentResponse
    {
        if (!_handlers.TryGetValue(typeof(TReq), out var handler))
            throw new InvalidOperationException(
                $"Nenhum agente registrado para o tipo '{typeof(TReq).Name}'. " +
                $"Verifique o registro no ServiceCollectionExtensions.");

        var thread = GetOrCreateThread(request.CorrelationId);
        return (TRes)await handler(request, thread, ct);
    }

    public async Task<TRes> SendWithRetryAsync<TReq, TRes>(
        TReq request, int maxRetries, Func<TRes, bool> isSuccess, CancellationToken ct = default)
        where TReq : AgentRequest
        where TRes : AgentResponse
    {
        TRes result = default!;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            result = await SendAsync<TReq, TRes>(request, ct);

            if (isSuccess(result))
            {
                _logger.LogInformation("[Runtime] ✓ {T} sucesso na tentativa {A}/{M}",
                    typeof(TReq).Name, attempt, maxRetries);
                return result;
            }

            if (attempt < maxRetries)
            {
                // Propaga o número de tentativas para a thread —
                // os agentes usam thread.RetryCount para adaptar seus prompts
                var thread = GetOrCreateThread(request.CorrelationId);
                thread.RetryCount = attempt;

                var delay = TimeSpan.FromSeconds(attempt * 2);
                _logger.LogWarning("[Runtime] ↩ {T} tentativa {A}/{M} falhou — aguardando {D}s",
                    typeof(TReq).Name, attempt, maxRetries, delay.TotalSeconds);

                await Task.Delay(delay, ct);
            }
        }

        _logger.LogWarning("[Runtime] ⚠ {T} esgotou {M} tentativas", typeof(TReq).Name, maxRetries);
        return result;
    }

    private AgentThread GetOrCreateThread(string correlationId)
    {
        if (!_threads.TryGetValue(correlationId, out var thread))
            _threads[correlationId] = thread = new AgentThread();
        return thread;
    }
}
