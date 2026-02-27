using DotnetAiTestAgent.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DotnetAiTestAgent.Application.Pipeline;

/// <summary>
/// Persiste e carrega o estado do pipeline entre execuções.
/// Habilita o modo incremental: só processa arquivos alterados desde a última execução.
/// </summary>
public class PipelineStateManager
{
    private const string StateFile = ".ai-test-agent.state.json";
    private readonly ILogger<PipelineStateManager> _logger;

    public PipelineStateManager(ILogger<PipelineStateManager> logger) => _logger = logger;

    public async Task<PipelineState> LoadOrCreateAsync(string projectPath)
    {
        var path = Path.Combine(projectPath, StateFile);
        if (!File.Exists(path)) return new PipelineState();
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<PipelineState>(json) ?? new PipelineState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Estado anterior inválido — iniciando do zero.");
            return new PipelineState();
        }
    }

    public async Task SaveAsync(PipelineState state, string projectPath)
    {
        state.LastRunAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(projectPath, StateFile), json);
    }
}

/// <summary>
/// Monitora alterações em arquivos .cs e dispara o pipeline automaticamente.
/// Debounce de 2 segundos para evitar execuções duplicadas em saves rápidos.
/// </summary>
public class ProjectWatcher
{
    private readonly AgentPipeline _pipeline;
    private readonly ILogger<ProjectWatcher> _logger;

    public ProjectWatcher(AgentPipeline pipeline, ILogger<ProjectWatcher> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <param name="sourcePath">Pasta monitorada (onde estão os .cs).</param>
    /// <param name="outputPath">Pasta de saída dos testes. Se null, usa sourcePath.</param>
    /// <param name="ct">Token de cancelamento — Ctrl+C encerra o watcher.</param>
    public async Task StartAsync(
        string sourcePath,
        string? outputPath = null,
        CancellationToken ct = default)
    {
        outputPath ??= sourcePath;

        _logger.LogInformation("👁 Watch mode");
        _logger.LogInformation("  📂 Monitorando: {S}", sourcePath);
        _logger.LogInformation("  📁 Saída:       {O}", outputPath);
        _logger.LogInformation("  Ctrl+C para encerrar");

        using var watcher = new FileSystemWatcher(sourcePath, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        System.Threading.Timer? debounce = null;
        var capturedOutput = outputPath;

        watcher.Changed += Trigger;
        watcher.Created += Trigger;
        watcher.EnableRaisingEvents = true;

        await Task.Delay(Timeout.Infinite, ct);

        void Trigger(object _, FileSystemEventArgs e)
        {
            if (IsIgnored(e.FullPath)) return;

            debounce?.Dispose();
            debounce = new System.Threading.Timer(async _ =>
            {
                _logger.LogInformation("🔄 Alteração: {F}", Path.GetFileName(e.FullPath));
                await _pipeline.RunAsync(
                    new PipelineOptions { IncrementalMode = true },
                    sourcePath, capturedOutput, ct);
            }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        static bool IsIgnored(string path) =>
            path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
    }
}