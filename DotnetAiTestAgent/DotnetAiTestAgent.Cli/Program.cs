using Bogus;
using DotnetAiTestAgent.Application.Agents;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Cli.Commands;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Services;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OpenAI;
using Serilog;
using System.CommandLine;


// Fase 1: logger mínimo para capturar erros antes do builder estar pronto
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // 1. REGRA DE OURO PARA WEB APPS COM ROSLYN:
    // O MSBuildLocator DEVE ser registrado antes de QUALQUER chamada ao compilador JIT,
    // ou seja, obrigatoriamente antes de chamar WebApplication.CreateBuilder.
    if (!MSBuildLocator.IsRegistered)
    {
        var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
        if (instances.Length > 0)
            MSBuildLocator.RegisterInstance(instances[0]);
        else
            throw new Exception("SDK do .NET não encontrado na máquina.");
    }

    Log.Information("Iniciando dotnet-ai-test-agent...");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddChatClient(new OllamaApiClient("http://localhost:11434", "qwen3-vl:8b-instruct"));

    // Registrando nossos novos serviços
    builder.Services.AddSingleton<ProjectAnalyzerService>();
    builder.Services.AddSingleton<RoslynValidator>();
    builder.Services.AddTransient<TestGeneratorAgent>();
    builder.Services.AddSingleton<InterfaceExtractorService>();

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
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // 4. Criando um Endpoint para disparar a geração de testes
    app.MapPost("/api/agente/gerar-teste", async (
        GenerateTestRequest request,
        ProjectAnalyzerService analyzer,
        TestGeneratorAgent agent) =>
    {
        try
        {
            // ATENÇÃO: Em um cenário real de produção/uso intenso, você deve fazer CACHE 
            // do 'projectReferences' por caminho de projeto (request.TargetProjectCsprojPath)
            // para não carregar o Workspace do MSBuild a cada chamada na API.
            var projectReferences = await analyzer.GetProjectReferencesAsync(request.TargetProjectCsprojPath);

            // Dispara o agente com o Feedback Loop
            string finalTestCode = await agent.GenerateAndValidateTestsAsync(
                request.ClassCode,
                request.InterfacesCode,
                projectReferences);

            // Aqui você pode retornar o código para o front-end exibir ou salvar direto no disco
            return Results.Ok(new
            {
                Sucesso = true,
                CodigoGerado = finalTestCode
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: ex.Message, title: "Erro na geração do teste");
        }
    })
    .WithName("GerarTesteDeUnidade");
    

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

public record GenerateTestRequest(
    string TargetProjectCsprojPath,
    string ClassCode,
    string InterfacesCode
);