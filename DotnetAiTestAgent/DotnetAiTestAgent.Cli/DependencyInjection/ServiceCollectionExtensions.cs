using Azure;
using Azure.AI.OpenAI;
using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Agents;
using DotnetAiTestAgent.Application.Pipeline;
using DotnetAiTestAgent.Application.Runtime;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Serilog;

namespace DotnetAiTestAgent.Cli.DependencyInjection;

public static class ServiceCollectionExtensions
{
    // ── Web ────────────────────────────────────────────────────────────────

    public static IServiceCollection AddAgentPipelineServices(
        this IServiceCollection services,
        AgentConfiguration config,
        string sourcePath,
        string outputPath,
        string provider = "ollama")
    {
        services.AddSingleton(config);
        services
            .AddChatClientFactoryInternal(config, provider)
            .AddPluginsInternal(sourcePath, outputPath)
            .AddAgentRuntimeInternal()
            .AddPipelineServicesInternal();

        return services;
    }

    // ── CLI ────────────────────────────────────────────────────────────────

    public static IServiceProvider BuildPipelineServices(
        AgentConfiguration config,
        string sourcePath,
        string outputPath,
        string provider)
    {
        var services = new ServiceCollection();

        services.AddLogging(l => l
            .ClearProviders()
            .AddSerilog(Serilog.Log.Logger, dispose: false)
            .SetMinimumLevel(LogLevel.Information));

        services.AddOpenTelemetry()
            .WithTracing(b => b
                .AddSource("Microsoft.Extensions.AI")
                .AddSource("dotnet-ai-test-agent")
                .AddConsoleExporter());

        services.AddAgentPipelineServices(config, sourcePath, outputPath, provider);

        return services.BuildServiceProvider();
    }

    // ── IChatClient factory — cria um cliente por modelo ──────────────────
    //
    // Cada agente pode usar um modelo diferente.
    // Com Ollama local e um único modelo (falcon3:7b), todos apontam para
    // o mesmo endpoint mas a factory permite trocar individualmente no futuro.

    private static IServiceCollection AddChatClientFactoryInternal(
        this IServiceCollection services,
        AgentConfiguration config,
        string provider)
    {
        // Registra a factory como singleton para reutilização
        services.AddSingleton<IChatClientFactory>(sp =>
            new OllamaOrRemoteChatClientFactory(
                config, provider,
                sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }

    private static IServiceCollection AddPluginsInternal(
        this IServiceCollection services,
        string sourcePath,
        string outputPath)
    {
        services.AddSingleton(sp => new FileSystemPlugin(
            sourcePath, outputPath,
            sp.GetRequiredService<ILogger<FileSystemPlugin>>()));

        services.AddSingleton(sp => new RoslynPlugin(
            sp.GetRequiredService<ILogger<RoslynPlugin>>()));

        services.AddSingleton(sp => new DotnetRunnerPlugin(
            outputPath,
            sp.GetRequiredService<ILogger<DotnetRunnerPlugin>>()));

        services.AddSingleton(sp => new CoverageParserPlugin(
            sp.GetRequiredService<ILogger<CoverageParserPlugin>>()));

        services.AddSingleton(sp => new StrykerPlugin(
            outputPath,
            sp.GetRequiredService<ILogger<StrykerPlugin>>()));

        services.AddSingleton(sp => new GitPlugin(
            sourcePath,
            sp.GetRequiredService<ILogger<GitPlugin>>()));

        return services;
    }

    private static IServiceCollection AddAgentRuntimeInternal(this IServiceCollection services)
    {
        services.AddSingleton<IAgentRuntime>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var factory = sp.GetRequiredService<IChatClientFactory>();
            var fs = sp.GetRequiredService<FileSystemPlugin>();
            var roslyn = sp.GetRequiredService<RoslynPlugin>();
            var parser = sp.GetRequiredService<CoverageParserPlugin>();
            var stryker = sp.GetRequiredService<StrykerPlugin>();
            var config = sp.GetRequiredService<AgentConfiguration>();
            var m = config.Llm.Models;

            // Cada agente recebe o IChatClient com o modelo definido na config.
            // Com falcon3:7b em todos, são instâncias distintas mas apontam
            // para o mesmo modelo — sem custo extra, permite trocar individualmente.
            return new AgentRuntime(lf.CreateLogger<AgentRuntime>())
                .Register(new OrchestratorAgent(
                    factory.Create(m.TestWriter), roslyn,
                    lf.CreateLogger<OrchestratorAgent>()))

                .Register(new FakeGeneratorAgent(
                    factory.Create(m.FakeGenerator), fs,
                    lf.CreateLogger<FakeGeneratorAgent>()))

                .Register(new TestWriterAgent(
                    factory.Create(m.TestWriter), fs,
                    lf.CreateLogger<TestWriterAgent>()))

                .Register(new CompileFixAgent(
                    factory.Create(m.CompileFix), fs,
                    lf.CreateLogger<CompileFixAgent>()))

                .Register(new TestDebugAgent(
                    factory.Create(m.TestDebug),
                    lf.CreateLogger<TestDebugAgent>()))

                .Register(new CoverageReviewAgent(
                    factory.Create(m.TestWriter), parser, fs,
                    lf.CreateLogger<CoverageReviewAgent>()))

                .Register(new MutationTestAgent(
                    factory.Create(m.TestWriter), stryker,
                    lf.CreateLogger<MutationTestAgent>()))

                .Register(new LogicAnalysisAgent(
                    factory.Create(m.LogicAnalysis),
                    lf.CreateLogger<LogicAnalysisAgent>()))

                .Register(new QualityAnalysisAgent(
                    factory.Create(m.QualityAnalysis),
                    lf.CreateLogger<QualityAnalysisAgent>()))

                .Register(new ArchitectureReviewAgent(
                    factory.Create(m.ArchitectureReview),
                    lf.CreateLogger<ArchitectureReviewAgent>()))

                .Register(new ReportGeneratorAgent(
                    factory.Create(m.ReportGenerator), fs,
                    lf.CreateLogger<ReportGeneratorAgent>()));
        });

        return services;
    }

    private static IServiceCollection AddPipelineServicesInternal(this IServiceCollection services)
    {
        services.AddSingleton<AgentPipeline>();
        services.AddSingleton<TestProjectScaffolder>();
        services.AddSingleton<PipelineStateManager>();
        services.AddSingleton<ProjectWatcher>();
        return services;
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Variável de ambiente '{name}' não definida.");
}

// ── IChatClientFactory ────────────────────────────────────────────────────────

/// <summary>
/// Cria instâncias de IChatClient para um modelo específico.
/// Abstraída para permitir trocar o provedor sem alterar o registro de agentes.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(string modelId);
}

public class OllamaOrRemoteChatClientFactory : IChatClientFactory
{
    private readonly AgentConfiguration _config;
    private readonly string _provider;
    private readonly ILoggerFactory _loggerFactory;

    public OllamaOrRemoteChatClientFactory(
        AgentConfiguration config,
        string provider,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _provider = provider;
        _loggerFactory = loggerFactory;
    }

    public IChatClient Create(string modelId)
    {
        IChatClient baseClient = _provider.ToLowerInvariant() switch
        {
            "ollama" => new OllamaChatClient(
                new Uri(_config.Llm.BaseUrl),
                modelId),                          // ← usa o modelo do agente

            //"openai" => new Microsoft.Extensions.AI.OpenAIChatClient(
            //    new OpenAIClient(
            //        new System.ClientModel.ApiKeyCredential(
            //            RequireEnv("OPENAI_API_KEY"))),
            //    modelId),

            //"azure" => new Microsoft.Extensions.AI.OpenAIChatClient(
            //    new AzureOpenAIClient(
            //        new Uri(RequireEnv("AZURE_OPENAI_ENDPOINT")),
            //        new AzureKeyCredential(RequireEnv("AZURE_OPENAI_KEY"))),
            //    modelId),

            _ => throw new ArgumentException(
                $"Provedor inválido: '{_provider}'. Use: ollama | openai | azure")
        };

        return baseClient
            .AsBuilder()
            .UseLogging(_loggerFactory)
            .UseOpenTelemetry(_loggerFactory, "dotnet-ai-test-agent",
                b => b.EnableSensitiveData = false)
            .Build();
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Variável de ambiente '{name}' não definida.");
}