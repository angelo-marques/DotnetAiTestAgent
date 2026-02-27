using System.CommandLine;
using Bogus;
using DotnetAiTestAgent.Cli.Commands;
using DotnetAiTestAgent.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Serilog;
// Fase 1: logger mínimo para capturar erros antes do builder estar pronto
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Iniciando dotnet-ai-test-agent...");

    var builder = WebApplication.CreateBuilder(args);

    // Adiciona o ai-test-agent.json além do appsettings.json padrão
    // O appsettings.json contém a seção "Serilog"
    // O ai-test-agent.json contém a seção "llm", "pipeline", "output", "features"
    builder.Configuration
        .AddJsonFile("ai-test-agent.json", optional: false, reloadOnChange: true)
        .AddEnvironmentVariables("AITA_");

    // Fase 2: Serilog lê do appsettings.json e substitui o bootstrap logger
    builder.Host.UseSerilog((ctx, services, loggerConfig) =>
    {
        loggerConfig
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithProcessId();
    });

    // Lê AgentConfiguration do ai-test-agent.json
    var agentConfig = builder.Configuration.Get<AgentConfiguration>()
        ?? throw new InvalidOperationException(
            "Falha ao carregar ai-test-agent.json. Verifique se o arquivo existe e é JSON válido (sem comentários).");

    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    var root = new RootCommand("dotnet-ai-test-agent — Agentes de IA para testes .NET");
    root.Subcommands.Add(AnalyzeCommand.Build(agentConfig));
    root.Subcommands.Add(WatchCommand.Build(agentConfig));

    // Se chamado com args CLI, executa o comando e encerra sem subir o servidor web
    if (args.Length > 0 && !args[0].StartsWith("--urls"))
    {
        return await root.Parse(args).InvokeAsync();
    }
    builder.AddServiceDefaults();
    var app = builder.Build();
   
    app.UseSerilogRequestLogging(opts =>
    {
        opts.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0.000}ms)";
        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            diagCtx.Set("RequestHost", httpCtx.Request.Host.Value);
            diagCtx.Set("RequestScheme", httpCtx.Request.Scheme);
        };
    });

    app.MapDefaultEndpoints();

    app.MapGet("/", () => "Hello World!");
    app.UseRouting();
    app.MapControllers();

    Log.Information("Servidor rodando. Use Ctrl+C para encerrar.");
    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Aplicação encerrada inesperadamente");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}