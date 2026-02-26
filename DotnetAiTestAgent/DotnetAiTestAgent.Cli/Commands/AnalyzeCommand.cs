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
///   1. Pasta única (source e output juntos — comportamento original)
///      dotnet-ai-test-agent analyze ./MinhaApi
///
///   2. Pastas separadas (recomendado)
///      dotnet-ai-test-agent analyze --source ./MinhaApi/src --output ./MinhaApi/tests-gerados
///
///   3. Pastas separadas com opções
///      dotnet-ai-test-agent analyze \
///          --source  ./MinhaApi/src \
///          --output  ./MinhaApi/tests-gerados \
///          --threshold 90 \
///          --workers 6 \
///          --provider openai
/// </summary>
public static class AnalyzeCommand
{
    private static readonly Option<string> SourceOpt = new("--source", "-s")
    {
        Description = "Pasta com o código-fonte a analisar (onde estão os .cs)",
        Required = true
    };

    private static readonly Option<string?> OutputOpt = new Option<string?>("--output", "-o")
    {
        Description = "Pasta de destino para testes, fakes e relatórios gerados. " +
                      "Se omitido, usa a mesma pasta do --source."
    };

    private static readonly Option<int> ThresholdOpt = new("--threshold", "-t")
    {
        Description = "Threshold de cobertura de linha (%)",
        DefaultValueFactory = _ => 80
    };

    private static readonly Option<int> RetriesOpt = new("--max-retries", "-r")
    {
        Description = "Máximo de retries automáticos por agente",
        DefaultValueFactory = _ => 3
    };

    private static readonly Option<int> WorkersOpt = new("--workers", "-w")
    {
        Description = "Workers paralelos para geração de testes",
        DefaultValueFactory = _ => 4
    };

    private static readonly Option<bool> IncrementalOpt = new("--incremental", "-i")
    {
        Description = "Modo incremental — processa só arquivos alterados (git diff)",
        DefaultValueFactory = _ => true
    };

    private static readonly Option<string> ProviderOpt = new("--provider", "-p")
    {
        Description = "Provedor LLM: ollama | openai | azure",
        DefaultValueFactory = _ => "ollama"
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
            var source = parseResult.GetValue(SourceOpt)!;
            var output = parseResult.GetValue(OutputOpt);   // null = usa source
            var threshold = parseResult.GetValue(ThresholdOpt);
            var retries = parseResult.GetValue(RetriesOpt);
            var workers = parseResult.GetValue(WorkersOpt);
            var incremental = parseResult.GetValue(IncrementalOpt);
            var provider = parseResult.GetValue(ProviderOpt)!;

            // Normaliza e valida os caminhos antes de iniciar
            source = Path.GetFullPath(source);
            output = output is not null ? Path.GetFullPath(output) : source;

            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"Pasta de origem não encontrada: {source}");

            // Cria a pasta de saída se não existir
            Directory.CreateDirectory(output);

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
}