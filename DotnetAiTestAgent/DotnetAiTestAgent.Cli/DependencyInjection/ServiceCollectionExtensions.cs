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
            .AddChatClientInternal(config, provider)
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

    // ── Internos ───────────────────────────────────────────────────────────

    private static IServiceCollection AddChatClientInternal(
        this IServiceCollection services,
        AgentConfiguration config,
        string provider)
    {
        services.AddSingleton<IChatClient>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();

            IChatClient baseClient = provider.ToLowerInvariant() switch
            {
                "ollama" => new OllamaChatClient(
                    new Uri(config.Llm.BaseUrl),
                    config.Llm.Models.TestWriter),

                //"openai" => new OpenAIChatClient(
                //    new OpenAIClient(
                //        new System.ClientModel.ApiKeyCredential(RequireEnv("OPENAI_API_KEY"))),
                //    config.Llm.Models.TestWriter),

                //"azure" => new OpenAIChatClient(
                //    new AzureOpenAIClient(
                //        new Uri(RequireEnv("AZURE_OPENAI_ENDPOINT")),
                //        new AzureKeyCredential(RequireEnv("AZURE_OPENAI_KEY"))),
                //    config.Llm.Models.TestWriter),

                _ => throw new ArgumentException(
                    $"Provedor inválido: '{provider}'. Use: ollama | openai | azure")
            };

            return baseClient
                .AsBuilder()
                .UseLogging(lf)
                .UseOpenTelemetry(lf, "dotnet-ai-test-agent", b => b.EnableSensitiveData = false)
                .Build();
        });

        return services;
    }

    private static IServiceCollection AddPluginsInternal(
        this IServiceCollection services,
        string sourcePath,
        string outputPath)
    {
        // FileSystemPlugin recebe os dois caminhos separados
        services.AddSingleton(sp => new FileSystemPlugin(
            sourcePath, outputPath,
            sp.GetRequiredService<ILogger<FileSystemPlugin>>()));

        services.AddSingleton(sp => new RoslynPlugin(
            sp.GetRequiredService<ILogger<RoslynPlugin>>()));

        // DotnetRunnerPlugin usa outputPath: compila e roda os testes gerados
        services.AddSingleton(sp => new DotnetRunnerPlugin(
            outputPath,
            sp.GetRequiredService<ILogger<DotnetRunnerPlugin>>()));

        services.AddSingleton(sp => new CoverageParserPlugin(
            sp.GetRequiredService<ILogger<CoverageParserPlugin>>()));

        // StrykerPlugin usa outputPath: analisa os testes gerados
        services.AddSingleton(sp => new StrykerPlugin(
            outputPath,
            sp.GetRequiredService<ILogger<StrykerPlugin>>()));

        // GitPlugin monitora sourcePath: detecta mudanças no código real
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
            var chat = sp.GetRequiredService<IChatClient>();
            var fs = sp.GetRequiredService<FileSystemPlugin>();
            var roslyn = sp.GetRequiredService<RoslynPlugin>();
            var parser = sp.GetRequiredService<CoverageParserPlugin>();
            var stryker = sp.GetRequiredService<StrykerPlugin>();

            return new AgentRuntime(lf.CreateLogger<AgentRuntime>())
                .Register(new OrchestratorAgent(chat, roslyn, lf.CreateLogger<OrchestratorAgent>()))
                .Register(new FakeGeneratorAgent(chat, fs, lf.CreateLogger<FakeGeneratorAgent>()))
                .Register(new TestWriterAgent(chat, fs, lf.CreateLogger<TestWriterAgent>()))
                .Register(new CompileFixAgent(chat, fs, lf.CreateLogger<CompileFixAgent>()))
                .Register(new TestDebugAgent(chat, lf.CreateLogger<TestDebugAgent>()))
                .Register(new CoverageReviewAgent(chat, parser, fs, lf.CreateLogger<CoverageReviewAgent>()))
                .Register(new MutationTestAgent(chat, stryker, lf.CreateLogger<MutationTestAgent>()))
                .Register(new LogicAnalysisAgent(chat, lf.CreateLogger<LogicAnalysisAgent>()))
                .Register(new QualityAnalysisAgent(chat, lf.CreateLogger<QualityAnalysisAgent>()))
                .Register(new ArchitectureReviewAgent(chat, lf.CreateLogger<ArchitectureReviewAgent>()))
                .Register(new ReportGeneratorAgent(chat, fs, lf.CreateLogger<ReportGeneratorAgent>()));
        });

        return services;
    }

    private static IServiceCollection AddPipelineServicesInternal(this IServiceCollection services)
    {
        services.AddSingleton<AgentPipeline>();
        services.AddSingleton<PipelineStateManager>();
        services.AddSingleton<ProjectWatcher>();
        return services;
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Variável de ambiente '{name}' não definida.");
}
