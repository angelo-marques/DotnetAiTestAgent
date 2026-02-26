using System.Text.Json;
using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Abstractions;

/// <summary>
/// Classe base para todos os agentes do pipeline.
/// Usa IChatClient (Microsoft.Extensions.AI) — funciona com Ollama, OpenAI e Azure
/// sem alterar o código dos agentes que herdam desta classe.
/// </summary>
public abstract class BaseAgent<TRequest, TResponse> : IAgent<TRequest, TResponse>
    where TRequest  : AgentRequest
    where TResponse : AgentResponse
{
    protected readonly IChatClient ChatClient;
    protected readonly ILogger Logger;

    protected BaseAgent(IChatClient chatClient, ILogger logger)
    {
        ChatClient = chatClient;
        Logger     = logger;
    }

    public abstract string Name { get; }

    public abstract Task<TResponse> HandleAsync(
        TRequest request, AgentThread thread, CancellationToken ct = default);

    /// <summary>
    /// Invoca o LLM via IChatClient preservando o histórico da AgentThread.
    /// O histórico permite que o modelo aprenda com tentativas anteriores de retry.
    /// </summary>
    protected async Task<string> CompleteAsync(
        string system, string user, AgentThread thread, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
        };

        // Injeta histórico da thread para contexto de retries
        foreach (var h in thread.History)
            messages.Add(h);

        messages.Add(new ChatMessage(ChatRole.User, user));

        var response = await ChatClient.GetResponseAsync(messages, cancellationToken: ct);
        var content  = response.Messages.FirstOrDefault()?.ToString() ?? string.Empty;

        // Persiste na thread
        thread.AddMessage(new ChatMessage(ChatRole.User,      user));
        thread.AddMessage(new ChatMessage(ChatRole.Assistant, content));

        return content;
    }

    /// <summary>
    /// Deserializa JSON da resposta do LLM com limpeza de markdown code fences.
    /// </summary>
    protected T? TryDeserialize<T>(string json)
    {
        try
        {
            var clean = json
                .Replace("```json", "").Replace("```csharp", "").Replace("```", "")
                .Trim();

            // Localiza início do JSON caso o modelo adicione texto antes
            var start = clean.IndexOfAny(new[] { '[', '{' });
            if (start > 0) clean = clean[start..];

            return JsonSerializer.Deserialize<T>(clean,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[{Agent}] Deserialização falhou: {Err}", Name, ex.Message);
            return default;
        }
    }
}
