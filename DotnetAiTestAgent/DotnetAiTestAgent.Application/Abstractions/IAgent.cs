using DotnetAiTestAgent.Domain.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;

namespace DotnetAiTestAgent.Application.Abstractions;

/// <summary>
/// Contrato tipado de um agente do Agent Framework.
/// TRequest = mensagem de entrada | TResponse = mensagem de saída.
/// </summary>
public interface IAgent<TRequest, TResponse>
    where TRequest  : AgentRequest
    where TResponse : AgentResponse
{
    string Name { get; }
    Task<TResponse> HandleAsync(TRequest request, AgentThread thread, CancellationToken ct = default);
}

/// <summary>
/// Runtime do Agent Framework: registra agentes e roteia mensagens pelo tipo.
/// Equivalente ao AgentRuntime do Microsoft Agent Framework nativo.
/// </summary>
public interface IAgentRuntime
{
    IAgentRuntime Register<TReq, TRes>(IAgent<TReq, TRes> agent)
        where TReq : AgentRequest
        where TRes : AgentResponse;

    Task<TRes> SendAsync<TReq, TRes>(TReq request, CancellationToken ct = default)
        where TReq : AgentRequest
        where TRes : AgentResponse;

    Task<TRes> SendWithRetryAsync<TReq, TRes>(TReq request, int maxRetries,
        Func<TRes, bool> isSuccess, CancellationToken ct = default)
        where TReq : AgentRequest
        where TRes : AgentResponse;
}

/// <summary>
/// Thread de conversa do Agent Framework.
/// Mantém histórico de mensagens e estado local entre chamadas do mesmo agente.
/// Cada CorrelationId tem sua própria thread — contexto isolado por execução.
/// </summary>
public class AgentThread
{
    private readonly List<ChatMessage> _history = new();
    private readonly Dictionary<string, object> _state = new();

    public string ThreadId  { get; } = Guid.NewGuid().ToString();
    public int RetryCount   { get; set; }

    public void AddMessage(ChatMessage msg)          => _history.Add(msg);
    public IReadOnlyList<ChatMessage> History        => _history.AsReadOnly();
    public void SetState<T>(string key, T v) where T : notnull => _state[key] = v;
    public T? GetState<T>(string key)               => _state.TryGetValue(key, out var v) ? (T)v : default;
}
