using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Configuration;

/// <summary>
/// Carrega os prompts dos agentes do arquivo agent-prompts.json.
///
/// BENEFÍCIOS:
///   - Prompts editáveis sem recompilar o projeto
///   - Versionamento separado dos prompts (git diff mais limpo)
///   - Fácil A/B testing de prompts diferentes
///   - Cada agente tem um prompt de auto-correção que é injetado
///     automaticamente quando a resposta do LLM não é válida
///
/// LOCALIZAÇÃO DO ARQUIVO:
///   Procura na seguinte ordem:
///   1. Diretório de trabalho atual (onde o executável foi iniciado)
///   2. Diretório do executável (AppContext.BaseDirectory)
///   3. Prompts embutidos no código (fallback — nunca deixa o agente sem prompt)
/// </summary>
public class PromptRepository
{
    private readonly Dictionary<string, AgentPrompt> _prompts;
    private readonly ILogger<PromptRepository> _logger;

    public const string FileName = "agent-prompts.json";

    public PromptRepository(ILogger<PromptRepository> logger)
    {
        _logger  = logger;
        _prompts = Load();
    }

    /// <summary>
    /// Retorna o system prompt do agente.
    /// Se não encontrado no JSON, retorna o fallback fornecido.
    /// </summary>
    public string GetSystem(string agentName, string fallback = "")
    {
        if (_prompts.TryGetValue(agentName, out var p) &&
            !string.IsNullOrWhiteSpace(p.System))
            return p.System;

        _logger.LogDebug("Prompt não encontrado para {A} — usando fallback embutido", agentName);
        return fallback;
    }

    /// <summary>
    /// Retorna o prompt de auto-correção do agente.
    /// Injetado automaticamente pelo BaseAgent quando TryDeserialize falha.
    /// </summary>
    public string GetSelfCorrection(string agentName)
    {
        if (_prompts.TryGetValue(agentName, out var p) &&
            !string.IsNullOrWhiteSpace(p.SelfCorrection))
            return p.SelfCorrection;

        // Fallback genérico se o agente não tiver um prompt de correção específico
        return "Sua resposta anterior não era válida. " +
               "Responda APENAS com o formato solicitado, sem texto adicional, sem markdown.";
    }

    /// <summary>
    /// Recarrega os prompts do disco (útil em watch mode sem reiniciar).
    /// </summary>
    public void Reload()
    {
        var fresh = Load();
        foreach (var kv in fresh)
            _prompts[kv.Key] = kv.Value;

        _logger.LogInformation("Prompts recarregados ({N} agentes)", fresh.Count);
    }

    // ── Carregamento ──────────────────────────────────────────────────────────

    private Dictionary<string, AgentPrompt> Load()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), FileName),
            Path.Combine(AppContext.BaseDirectory, FileName)
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var json    = File.ReadAllText(path);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling         = JsonCommentHandling.Skip,
                    AllowTrailingCommas         = true
                };

                var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, options)
                    ?? new();

                var result = new Dictionary<string, AgentPrompt>(StringComparer.OrdinalIgnoreCase);

                foreach (var (key, value) in raw)
                {
                    // Ignora chaves de metadados (_comment, _formato, etc.)
                    if (key.StartsWith('_')) continue;

                    var prompt = JsonSerializer.Deserialize<AgentPrompt>(value.GetRawText(), options);
                    if (prompt is not null)
                        result[key] = prompt;
                }

                _logger.LogInformation(
                    "Prompts carregados de {F} ({N} agentes)", path, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao carregar {F} — usando prompts embutidos", path);
            }
        }

        _logger.LogWarning(
            "{F} não encontrado em nenhum dos caminhos buscados. " +
            "Os agentes usarão os prompts embutidos no código. " +
            "Copie o arquivo para: {D}",
            FileName,
            Directory.GetCurrentDirectory());

        return new(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Estrutura de um prompt no agent-prompts.json.
/// </summary>
public record AgentPrompt(
    string System,
    string SelfCorrection
);
