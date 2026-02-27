using System.CommandLine;
using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Cli.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAiTestAgent.Cli.Commands;

/// <summary>
/// Comando `analyze`.
///
/// FORMAS DE USO:
///
///   1. Tudo configurado no ai-test-agent.json (sem argumentos)
///      dotnet-ai-test-agent analyze
///
///   2. Sobrescrever só o source via CLI
///      dotnet-ai-test-agent analyze --source C:\OutroProjeto\src
///
///   3. Sobrescrever source e output via CLI
///      dotnet-ai-test-agent analyze \
///          --source  C:\MinhaApi\src \
///          --output  C:\MinhaApi\tests-gerados
///
///   4. Com todas as opções
///      dotnet-ai-test-agent analyze \
///          --source    C:\MinhaApi\src \
///          --output    C:\MinhaApi\tests-gerados \
///          --threshold 90 \
///          --workers   4 \
///          --provider  openai
///
/// PRIORIDADE DE RESOLUÇÃO dos caminhos:
///   CLI (--source / --output)  >  ai-test-agent.json (paths.sourcePath / paths.outputPath)
///
/// Se nenhum dos dois estiver configurado, o comando exibe erro claro.
/// </summary>
public static class AnalyzeCommand
{
    // Opções de caminho — OPCIONAIS porque podem vir do JSON
    private static readonly Option<string?> SourceOpt = new("--source", "-s")
    {
        Description = "Pasta com o código-fonte a analisar (onde estão os .cs). " +
                      "Se omitido, usa paths.sourcePath do ai-test-agent.json."
    };

    private static readonly Option<string?> OutputOpt = new("--output", "-o")
    {
        Description = "Pasta de destino para testes, fakes e relatórios. " +
                      "Se omitido, usa paths.outputPath do ai-test-agent.json. " +
                      "Se ambos omitidos, usa o mesmo valor do source."
    };

    private static readonly Option<int> ThresholdOpt = new("--threshold", "-t")
    {
        Description = "Threshold de cobertura de linha (%)",
        DefaultValueFactory = _ => 0   // 0 = usa o valor do JSON
    };

    private static readonly Option<int> RetriesOpt = new("--max-retries", "-r")
    {
        Description = "Máximo de retries automáticos por agente",
        DefaultValueFactory = _ => 0   // 0 = usa o valor do JSON
    };

    private static readonly Option<int> WorkersOpt = new("--workers", "-w")
    {
        Description = "Workers paralelos para geração de testes",
        DefaultValueFactory = _ => 0   // 0 = usa o valor do JSON
    };

    private static readonly Option<bool> IncrementalOpt = new("--incremental", "-i")
    {
        Description = "Modo incremental — processa só arquivos alterados (git diff)",
        DefaultValueFactory = _ => true
    };

    private static readonly Option<string?> ProviderOpt = new("--provider", "-p")
    {
        Description = "Provedor LLM: ollama | openai | azure. " +
                      "Se omitido, usa llm.provider do ai-test-agent.json."
    };

    public static Command Build(AgentConfiguration config)
    {
        var command = new Command("analyze", "Analisa código-fonte e gera testes, fakes e relatórios");

        command.Options.Add(SourceOpt);
        command.Options.Add(OutputOpt);
        command.Options.Add(ThresholdOpt);
        command.Options.Add(RetriesOpt);
        command.Options.Add(WorkersOpt);
        command.Options.Add(IncrementalOpt);
        command.Options.Add(ProviderOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            // ── Resolução de caminhos: CLI > JSON > erro ──────────────────────
            var sourceCli = parseResult.GetValue(SourceOpt);
            var outputCli = parseResult.GetValue(OutputOpt);

            var source = ResolveSource(sourceCli, config.Paths);
            var output = ResolveOutput(outputCli, config.Paths, source);

            // ── Resolução de opções: CLI > JSON ───────────────────────────────
            var thresholdCli = parseResult.GetValue(ThresholdOpt);
            var retriesCli = parseResult.GetValue(RetriesOpt);
            var workersCli = parseResult.GetValue(WorkersOpt);
            var incremental = parseResult.GetValue(IncrementalOpt);
            var providerCli = parseResult.GetValue(ProviderOpt);

            var threshold = thresholdCli > 0 ? thresholdCli : config.Pipeline.CoverageThreshold;
            var retries = retriesCli > 0 ? retriesCli : config.Pipeline.MaxRetriesPerAgent;
            var workers = workersCli > 0 ? workersCli : config.Pipeline.ParallelWorkers;
            var provider = !string.IsNullOrWhiteSpace(providerCli) ? providerCli : config.Llm.Provider;

            // ── Validação final ───────────────────────────────────────────────
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(
                    $"Pasta de origem não encontrada: {source}\n" +
                    $"Configure paths.sourcePath no ai-test-agent.json ou passe --source.");

            Directory.CreateDirectory(output);

            // ── Pipeline ──────────────────────────────────────────────────────
            var sp = ServiceCollectionExtensions.BuildPipelineServices(config, source, output, provider);
            var pipeline = sp.GetRequiredService<AgentPipeline>();

            await pipeline.RunAsync(new PipelineOptions
            {
                CoverageThreshold = threshold,
                MaxRetriesPerAgent = retries,
                ParallelWorkers = workers,
                IncrementalMode = incremental
            }, source, output, ct);
        });

        return command;
    }

    // ── Helpers de resolução ──────────────────────────────────────────────────

    /// <summary>
    /// Resolve o caminho de origem na ordem de prioridade:
    ///   1. --source CLI
    ///   2. paths.sourcePath do JSON
    ///   3. Lança InvalidOperationException com mensagem clara
    /// </summary>
    private static string ResolveSource(string? cli, PathsConfig paths)
    {
        var raw = !string.IsNullOrWhiteSpace(cli) ? cli
                : !string.IsNullOrWhiteSpace(paths.SourcePath) ? paths.SourcePath
                : null;

        if (raw is null)
            throw new InvalidOperationException(
                "Caminho de origem não configurado.\n" +
                "Opção 1 — passe via CLI:       --source C:\\MeuProjeto\\src\n" +
                "Opção 2 — configure no JSON:   paths.sourcePath em ai-test-agent.json");

        return Path.GetFullPath(raw);
    }

    /// <summary>
    /// Resolve o caminho de saída na ordem de prioridade:
    ///   1. --output CLI
    ///   2. paths.outputPath do JSON
    ///   3. Mesmo valor do source (comportamento legado)
    /// </summary>
    private static string ResolveOutput(string? cli, PathsConfig paths, string resolvedSource)
    {
        if (!string.IsNullOrWhiteSpace(cli))
            return Path.GetFullPath(cli);

        if (!string.IsNullOrWhiteSpace(paths.OutputPath))
            return Path.GetFullPath(paths.OutputPath);

        return resolvedSource;
    }
}