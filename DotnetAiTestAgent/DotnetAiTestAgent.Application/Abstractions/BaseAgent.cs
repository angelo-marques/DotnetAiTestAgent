using Bogus;
using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // JsonSerializerOptions compartilhado para todos os agentes:
    //   - PropertyNameCaseInsensitive: aceita "severity" ou "Severity"
    //   - JsonStringEnumConverter case-insensitive: aceita "High", "HIGH", "high"
    //   - AllowTrailingCommas + ReadCommentHandling: tolera JSON levemente malformado
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
                                            allowIntegerValues: true) }
    };

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
        var content  = response.Messages.ToString() ?? string.Empty;

        // Persiste na thread
        thread.AddMessage(new ChatMessage(ChatRole.User,      user));
        thread.AddMessage(new ChatMessage(ChatRole.Assistant, content));

        return content;
    }


    /// <summary>
    /// Deserializa JSON da resposta do LLM.
    ///
    /// ESTRATÉGIA DE LIMPEZA (ordem importante):
    ///   1. Remove code fences ```json ... ``` ou ``` ... ```
    ///   2. Localiza o primeiro [ ou { — descarta texto antes (preamble do modelo)
    ///   3. Localiza o último ] ou } compatível — descarta texto depois (epílogo do modelo)
    ///   4. Normaliza valores de enum: "HIGH" → "High", "CRITICAL" → "Critical"
    ///   5. Desserializa com opções tolerantes (case-insensitive, trailing commas)
    /// </summary>
    protected T? TryDeserialize<T>(string raw)
    {
        try
        {
            var clean = StripToJson(raw);
            if (string.IsNullOrWhiteSpace(clean)) return default;

            // Normaliza enum strings que o modelo envia em MAIÚSCULAS ou com espaço
            clean = NormalizeEnumValues(clean);

            return JsonSerializer.Deserialize<T>(clean, _jsonOptions);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("[{Agent}] Deserialização falhou: {Err}", Name, ex.Message);
            return default;
        }
    }

    // ── Helpers de limpeza ────────────────────────────────────────────────────

    private static string StripToJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Remove code fences
        var clean = raw
            .Replace("```json", "")
            .Replace("```csharp", "")
            .Replace("```", "")
            .Trim();

        // Descarta texto antes do primeiro [ ou {
        var start = clean.IndexOfAny(new[] { '[', '{' });
        if (start < 0) return string.Empty;
        clean = clean[start..];

        // Descarta texto depois do último ] ou } compatível com o início
        char closeChar = clean[0] == '[' ? ']' : '}';
        var end = clean.LastIndexOf(closeChar);
        if (end < 0) return string.Empty;
        clean = clean[..(end + 1)];

        return clean.Trim();
    }

    /// <summary>
    /// Normaliza valores de enum no JSON que o modelo envia com casing incorreto.
    ///
    /// Exemplos corrigidos:
    ///   "severity": "HIGH"     → "severity": "High"
    ///   "severity": "CRITICAL" → "severity": "Critical"
    ///   "severity": "medium"   → "severity": "Medium"
    ///   "severity": "low"      → "severity": "Low"
    /// </summary>
    private static string NormalizeEnumValues(string json)
    {
        // Usa Replace simples para os casos mais comuns que o Falcon3 produz
        // Evita Regex para manter performance e dependência mínima
        return json
            .Replace("\"HIGH\"", "\"High\"")
            .Replace("\"MEDIUM\"", "\"Medium\"")
            .Replace("\"LOW\"", "\"Low\"")
            .Replace("\"CRITICAL\"", "\"Critical\"")
            .Replace("\"INFO\"", "\"Info\"")
            .Replace("\"high\"", "\"High\"")
            .Replace("\"medium\"", "\"Medium\"")
            .Replace("\"low\"", "\"Low\"")
            .Replace("\"critical\"", "\"Critical\"")
            .Replace("\"info\"", "\"Info\"");
    }
}
