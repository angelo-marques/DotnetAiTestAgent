using Bogus;
using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAiTestAgent.Application.Abstractions;

/// <summary>
/// Classe base para todos os agentes do pipeline.
///
/// NOVIDADES:
///   1. Prompts carregados do agent-prompts.json via PromptRepository
///   2. TryDeserializeWithCorrectionAsync — auto-correção quando LLM retorna
///      texto em vez de JSON: mostra a resposta inválida ao modelo e pede correção
///   3. GetSystemPrompt() — busca no JSON, fallback no código
/// </summary>
public abstract class BaseAgent<TRequest, TResponse> : IAgent<TRequest, TResponse>
    where TRequest : AgentRequest
    where TResponse : AgentResponse
{
    protected readonly IChatClient ChatClient;
    protected readonly ILogger Logger;
    protected readonly PromptRepository Prompts;

    protected virtual int SelfCorrectionMaxAttempts => 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(
                                            JsonNamingPolicy.CamelCase,
                                            allowIntegerValues: true) }
    };

    protected BaseAgent(IChatClient chatClient, PromptRepository prompts, ILogger logger)
    {
        ChatClient = chatClient;
        Prompts = prompts;
        Logger = logger;
    }

    public abstract string Name { get; }

    public abstract Task<TResponse> HandleAsync(
        TRequest request, AgentThread thread, CancellationToken ct = default);

    /// <summary>
    /// Retorna o system prompt do agent-prompts.json.
    /// Usa o fallback embutido se o arquivo não existir ou o agente não estiver no JSON.
    /// </summary>
    protected string GetSystemPrompt(string fallback = "") =>
        Prompts.GetSystem(Name, fallback);

    /// <summary>
    /// Invoca o LLM preservando histórico da thread.
    /// </summary>
    protected async Task<string> CompleteAsync(
        string system, string user, AgentThread thread, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, system) };
        foreach (var h in thread.History)
            messages.Add(h);
        messages.Add(new ChatMessage(ChatRole.User, user));

        var response = await ChatClient.GetResponseAsync(messages, cancellationToken: ct);
        var content = response.Text ?? string.Empty;

        thread.AddMessage(new ChatMessage(ChatRole.User, user));
        thread.AddMessage(new ChatMessage(ChatRole.Assistant, content));

        return content;
    }

    /// <summary>
    /// Desserializa com auto-correção integrada.
    ///
    /// Quando o LLM retorna texto/markdown em vez de JSON:
    ///   1. Mostra a resposta inválida de volta ao LLM
    ///   2. Injeta o selfCorrection prompt do agent-prompts.json
    ///   3. Pede para corrigir e tenta novamente
    ///   Repete até SelfCorrectionMaxAttempts ou até obter JSON válido.
    /// </summary>
    protected async Task<T?> TryDeserializeWithCorrectionAsync<T>(
        string raw, AgentThread thread, CancellationToken ct = default)
    {
        var result = TryDeserialize<T>(raw);
        if (result is not null) return result;

        var system = GetSystemPrompt();

        for (int attempt = 1; attempt <= SelfCorrectionMaxAttempts; attempt++)
        {
            Logger.LogWarning("[{A}] Auto-correção {N}/{M}", Name, attempt, SelfCorrectionMaxAttempts);

            var correctionMsg =
                $"{Prompts.GetSelfCorrection(Name)}\n\n" +
                $"Sua resposta anterior foi:\n{raw}\n\n" +
                $"Corrija e retorne APENAS o formato solicitado.";

            raw = await CompleteAsync(system, correctionMsg, thread, ct);
            result = TryDeserialize<T>(raw);

            if (result is not null)
            {
                Logger.LogInformation("[{A}] Auto-correção OK na tentativa {N}", Name, attempt);
                return result;
            }
        }

        Logger.LogWarning("[{A}] Auto-correção esgotou {M} tentativas", Name, SelfCorrectionMaxAttempts);
        return default;
    }

    /// <summary>
    /// Desserialização simples sem retry.
    /// Para uso interno e em contextos sem AgentThread disponível.
    /// </summary>
    protected T? TryDeserialize<T>(string raw)
    {
        try
        {
            var clean = StripToJson(raw);
            if (string.IsNullOrWhiteSpace(clean)) return default;
            clean = NormalizeEnumValues(clean);
            return JsonSerializer.Deserialize<T>(clean, JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("[{A}] Deserialização falhou: {E}", Name, ex.Message);
            return default;
        }
    }

    private static string StripToJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var clean = raw
            .Replace("```json", "").Replace("```csharp", "").Replace("```", "")
            .Trim();

        var start = clean.IndexOfAny(new[] { '[', '{' });
        if (start < 0) return string.Empty;
        clean = clean[start..];

        char closeChar = clean[0] == '[' ? ']' : '}';
        var end = clean.LastIndexOf(closeChar);
        if (end < 0) return string.Empty;

        return clean[..(end + 1)].Trim();
    }

    private static string NormalizeEnumValues(string json) => json
        .Replace("\"HIGH\"", "\"High\"").Replace("\"MEDIUM\"", "\"Medium\"")
        .Replace("\"LOW\"", "\"Low\"").Replace("\"CRITICAL\"", "\"Critical\"")
        .Replace("\"INFO\"", "\"Info\"").Replace("\"high\"", "\"High\"")
        .Replace("\"medium\"", "\"Medium\"").Replace("\"low\"", "\"Low\"")
        .Replace("\"critical\"", "\"Critical\"").Replace("\"info\"", "\"Info\"");
}