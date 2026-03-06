using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Pipeline;


/// <summary>
/// Orquestra o pipeline completo de geração de testes.
///
/// sourcePath → onde estão os .cs a analisar
/// outputPath → onde os testes gerados, fakes e relatórios serão escritos
///
/// ETAPAS:
///   [0/8] Scaffolding do projeto de testes (cria .csproj com xunit SE não existir)
///   [1/8] Descoberta de classes e interfaces via Roslyn
///   [2/8] Geração de Fakes e FakeBuilders
///   [3/8] Geração de testes xUnit AAA
///   [4/8] Compilação + auto-correção
///   [5/8] Execução e debug de testes
///   [6/8] Análise de lógica
///   [7/8] Análise de qualidade e arquitetura
///   [8/8] Geração de relatório Markdown
/// </summary>

public class AgentPipeline
{
    private readonly IAgentRuntime _runtime;
    private readonly AgentConfiguration _config;
    private readonly PipelineStateManager _stateManager;
    private readonly DotnetRunnerPlugin _dotnet;
    private readonly TestProjectScaffolder _scaffolder;
    private readonly ILogger<AgentPipeline> _logger;

    public AgentPipeline(
        IAgentRuntime runtime,
        AgentConfiguration config,
        PipelineStateManager stateManager,
        DotnetRunnerPlugin dotnet,
        TestProjectScaffolder scaffolder,
        ILogger<AgentPipeline> logger)
    {
        _runtime = runtime;
        _config = config;
        _stateManager = stateManager;
        _dotnet = dotnet;
        _scaffolder = scaffolder;
        _logger = logger;
    }

    public async Task RunAsync(
        PipelineOptions options,
        string sourcePath,
        string? outputPath = null,
        CancellationToken ct = default)
    {
        outputPath ??= sourcePath;

        var id = Guid.NewGuid().ToString();
        var state = await _stateManager.LoadOrCreateAsync(sourcePath);
        var context = new AgentContext
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            Options = options,
            State = state
        };

        PrintBanner(sourcePath, outputPath, options);

        try
        {
            // [0] SCAFFOLDING — cria outputPath/tests/*.csproj com xunit + coverlet
            //     DEVE rodar ANTES dos agentes gerarem os .cs
            await StepScaffoldAsync(context, ct);

            await StepDiscoverAsync(context, id, ct);
            await StepGenerateFakesAsync(context, id, ct);
            await StepGenerateTestsAsync(context, id, new(), ct);
            await StepCompileAsync(context, id, options.MaxRetriesPerAgent, ct);
            await StepDebugTestsAsync(context, id, options.MaxRetriesPerAgent, ct);
            await StepLogicAnalysisAsync(context, id, ct);
            await StepQualityAndArchitectureAsync(context, id, ct);
            await StepGenerateReportsAsync(context, id, ct);

            await _stateManager.SaveAsync(state, sourcePath);

            _logger.LogInformation("✅ Concluído! Testes: {T} | Bugs: {B} | Issues: {I}",
                context.GeneratedTests.Count,
                context.LogicIssues.Count,
                context.QualityIssues.Count);
            _logger.LogInformation("📁 Testes:    {O}", Path.Combine(outputPath, "tests"));
            _logger.LogInformation("📄 Relatório: {R}",
                Path.Combine(outputPath, "ai-test-reports", "test-report.md"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Falha no pipeline");
            await TryGeneratePartialReportAsync(context, id, ct);
            throw;
        }
    }

    // ── Etapas ────────────────────────────────────────────────────────────────

    private async Task StepScaffoldAsync(AgentContext ctx, CancellationToken ct)
    {
        _logger.LogInformation("[0/8] Preparando projeto de testes em {O}/tests...", ctx.OutputPath);
        await _scaffolder.EnsureTestProjectAsync(ctx.SourcePath, ctx.OutputPath);
    }

    private async Task StepDiscoverAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[1/8] Descobrindo classes em {S}...", ctx.SourcePath);
        var r = await _runtime.SendAsync<DiscoverProjectRequest, ProjectDiscoveredResponse>(
            new DiscoverProjectRequest(ctx.SourcePath) { CorrelationId = id }, ct);
        ctx.DiscoveredClasses = r.Classes;
        ctx.DiscoveredInterfaces = r.Interfaces;
    }

    private async Task StepGenerateFakesAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        if (!_config.Features.GenerateFakes) return;
        _logger.LogInformation("[2/8] Gerando Fakes em {O}/tests/Fakes...", ctx.OutputPath);
        var r = await _runtime.SendAsync<GenerateFakesRequest, FakesGeneratedResponse>(
            new GenerateFakesRequest(ctx.OutputPath, ctx.DiscoveredInterfaces) { CorrelationId = id }, ct);
        ctx.GeneratedFakes = r.GeneratedFakes;
    }

    private async Task StepGenerateTestsAsync(
        AgentContext ctx, string id,
        List<Domain.ValueObjects.CoverageGap> gaps,
        CancellationToken ct)
    {
        _logger.LogInformation("[3/8] Gerando testes xUnit em {O}/tests...", ctx.OutputPath);
        var r = await _runtime.SendAsync<GenerateTestsRequest, TestsGeneratedResponse>(
            new GenerateTestsRequest(ctx.OutputPath, ctx.DiscoveredClasses, gaps) { CorrelationId = id }, ct);
        ctx.GeneratedTests = r.GeneratedTests;
    }

    private async Task StepCompileAsync(AgentContext ctx, string id, int maxRetries, CancellationToken ct)
    {
        _logger.LogInformation("[4/8] Compilando testes...");
        await _runtime.SendWithRetryAsync<CompileFixRequest, CompileResultResponse>(
            new CompileFixRequest(ctx.OutputPath, await _dotnet.BuildAsync(ctx.OutputPath))
            { CorrelationId = id },
            maxRetries, r => r.Success, ct);
    }

    private async Task StepDebugTestsAsync(AgentContext ctx, string id, int maxRetries, CancellationToken ct)
    {
        _logger.LogInformation("[5/8] Executando testes...");
        var testOutput = await _dotnet.RunTestsAsync(ctx.OutputPath);
        ctx.TestRunOutput = testOutput;

        var r = await _runtime.SendWithRetryAsync<DebugTestsRequest, TestsResultResponse>(
            new DebugTestsRequest(ctx.OutputPath, testOutput) { CorrelationId = id },
            maxRetries, r => r.AllPassed, ct);
        ctx.LogicIssues.AddRange(r.BugsFound);
        ctx.TestsAllPassed = r.AllPassed;
    }

    private async Task StepLogicAnalysisAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[6/8] Analisando lógica em {S}...", ctx.SourcePath);
        var r = await _runtime.SendAsync<AnalyzeLogicRequest, LogicAnalysisResponse>(
            new AnalyzeLogicRequest(ctx.SourcePath, ctx.DiscoveredClasses) { CorrelationId = id }, ct);
        ctx.LogicIssues.AddRange(r.Issues);
    }

    private async Task StepQualityAndArchitectureAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[7/8] Qualidade e arquitetura...");
        var q = await _runtime.SendAsync<AnalyzeQualityRequest, QualityAnalysisResponse>(
            new AnalyzeQualityRequest(ctx.SourcePath, ctx.DiscoveredClasses) { CorrelationId = id }, ct);
        ctx.QualityIssues = q.Issues;

        if (_config.Features.ArchitectureReview)
        {
            var a = await _runtime.SendAsync<ReviewArchitectureRequest, ArchitectureReviewResponse>(
                new ReviewArchitectureRequest(ctx.SourcePath, ctx.DiscoveredClasses) { CorrelationId = id }, ct);
            ctx.ArchitectureIssues = a.Issues;
        }
    }

    private async Task StepGenerateReportsAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[8/8] Gerando relatório em {O}/ai-test-reports...", ctx.OutputPath);
        await _runtime.SendAsync<GenerateReportsRequest, ReportsGeneratedResponse>(
            new GenerateReportsRequest(ctx) { CorrelationId = id }, ct);
    }

    private async Task TryGeneratePartialReportAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        try { await StepGenerateReportsAsync(ctx, id, ct); }
        catch { /* relatório parcial — ignora */ }
    }

    private void PrintBanner(string source, string output, PipelineOptions opts)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════");
        _logger.LogInformation("  dotnet-ai-test-agent  |  Microsoft.Extensions.AI");
        _logger.LogInformation("  📂 Fonte:   {S}", source);
        _logger.LogInformation("  📁 Saída:   {O}", output);
        _logger.LogInformation("  Cobertura alvo: removida | Workers: {W} | Retries: {R}",
            opts.ParallelWorkers, opts.MaxRetriesPerAgent);
        _logger.LogInformation("═══════════════════════════════════════════════════");
    }
}