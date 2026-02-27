using System.CommandLine;
using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Cli.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAiTestAgent.Cli.Commands;

/// <summary>
/// Comando `watch` — monitora alterações e regenera testes automaticamente.
///
/// FORMAS DE USO:
///   dotnet-ai-test-agent watch                               (usa paths do JSON)
///   dotnet-ai-test-agent watch --source C:\MinhaApi\src      (sobrescreve source)
///   dotnet-ai-test-agent watch --source C:\MinhaApi\src --output C:\MinhaApi\tests
///
/// PRIORIDADE: CLI > ai-test-agent.json (paths.sourcePath / paths.outputPath)
/// </summary>
public static class WatchCommand
{
    private static readonly Option<string?> SourceOpt = new("--source", "-s")
    {
        Description = "Pasta com o código-fonte a monitorar. " +
                      "Se omitido, usa paths.sourcePath do ai-test-agent.json."
    };

    private static readonly Option<string?> OutputOpt = new("--output", "-o")
    {
        Description = "Pasta de destino dos testes gerados. " +
                      "Se omitido, usa paths.outputPath do ai-test-agent.json."
    };

    private static readonly Option<string?> ProviderOpt = new("--provider", "-p")
    {
        Description = "Provedor LLM: ollama | openai | azure. " +
                      "Se omitido, usa llm.provider do ai-test-agent.json."
    };

    public static Command Build(AgentConfiguration config)
    {
        var command = new Command("watch", "Monitora alterações e regenera testes automaticamente");

        command.Options.Add(SourceOpt);
        command.Options.Add(OutputOpt);
        command.Options.Add(ProviderOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var sourceCli = parseResult.GetValue(SourceOpt);
            var outputCli = parseResult.GetValue(OutputOpt);
            var providerCli = parseResult.GetValue(ProviderOpt);

            var source = ResolveSource(sourceCli, config.Paths);
            var output = ResolveOutput(outputCli, config.Paths, source);
            var provider = !string.IsNullOrWhiteSpace(providerCli) ? providerCli : config.Llm.Provider;

            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(
                    $"Pasta de origem não encontrada: {source}\n" +
                    $"Configure paths.sourcePath no ai-test-agent.json ou passe --source.");

            Directory.CreateDirectory(output);

            var sp = ServiceCollectionExtensions.BuildPipelineServices(config, source, output, provider);
            var watcher = sp.GetRequiredService<ProjectWatcher>();

            // Usa parâmetros nomeados para evitar ambiguidade de overload
            await watcher.StartAsync(sourcePath: source, outputPath: output, ct: ct);
        });

        return command;
    }

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

    private static string ResolveOutput(string? cli, PathsConfig paths, string resolvedSource)
    {
        if (!string.IsNullOrWhiteSpace(cli))
            return Path.GetFullPath(cli);

        if (!string.IsNullOrWhiteSpace(paths.OutputPath))
            return Path.GetFullPath(paths.OutputPath);

        return resolvedSource;
    }
}