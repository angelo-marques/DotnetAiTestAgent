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
///   dotnet-ai-test-agent watch --source ./MinhaApi/src
///   dotnet-ai-test-agent watch --source ./MinhaApi/src --output ./MinhaApi/tests-gerados
/// </summary>
public static class WatchCommand
{
    private static readonly Option<string> SourceOpt = new("--source", "-s")
    {
        Description = "Pasta com o código-fonte a monitorar",
        Required = true
    };

    private static readonly Option<string?> OutputOpt = new Option<string?>("--output", "-o")
    {
        Description = "Pasta de destino dos testes gerados. Se omitido, usa --source."
    };

    private static readonly Option<string> ProviderOpt = new("--provider", "-p")
    {
        Description = "Provedor LLM: ollama | openai | azure",
        DefaultValueFactory = _ => "ollama"
    };

    public static Command Build(AgentConfiguration config)
    {
        var command = new Command("watch", "Monitora alterações e regenera testes automaticamente");

        command.Options.Add(SourceOpt);
        command.Options.Add(OutputOpt);
        command.Options.Add(ProviderOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = Path.GetFullPath(parseResult.GetValue(SourceOpt)!);
            var rawOutput = parseResult.GetValue(OutputOpt);
            var output = rawOutput is not null ? Path.GetFullPath(rawOutput) : source;
            var provider = parseResult.GetValue(ProviderOpt)!;

            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"Pasta de origem não encontrada: {source}");

            Directory.CreateDirectory(output);

            var sp = ServiceCollectionExtensions.BuildPipelineServices(config, source, output, provider);
            var watcher = sp.GetRequiredService<ProjectWatcher>();

            await watcher.StartAsync(source);
        });

        return command;
    }
}