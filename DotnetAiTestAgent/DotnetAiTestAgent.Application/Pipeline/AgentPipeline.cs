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
///   [0/10] Scaffolding do projeto de testes (cria .csproj com xunit + coverlet SE não existir)
///   [1/10] Descoberta de classes e interfaces via Roslyn
///   [2/10] Geração de Fakes e FakeBuilders
///   [3/10] Geração de testes xUnit AAA
///   [4/10] Compilação + auto-correção
///   [5/10] Execução e debug de testes
///   [6/10] Loop de cobertura (XML + HTML via reportgenerator)
///   [7/10] Mutation testing (Stryker.NET)
///   [8/10] Análise de lógica
///   [9/10] Análise de qualidade e arquitetura
///   [10/10] Geração de relatórios Markdown + JSON
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
            await StepCoverageLoopAsync(context, id, options, ct);
            await StepMutationAsync(context, id, ct);
            await StepLogicAnalysisAsync(context, id, ct);
            await StepQualityAndArchitectureAsync(context, id, ct);
            await StepGenerateReportsAsync(context, id, ct);

            await _stateManager.SaveAsync(state, sourcePath);

            _logger.LogInformation("✅ Concluído! Cobertura: {C:F1}% | Mutation: {M:F1}%",
                context.CurrentCoverage, context.MutationScore);
            _logger.LogInformation("📁 Testes: {O}", Path.Combine(outputPath, "tests"));
            _logger.LogInformation("📊 HTML:   {H}",
                Path.Combine(outputPath, "ai-test-reports", "coverage-html", "index.html"));
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
        _logger.LogInformation("[0/10] Preparando projeto de testes em {O}/tests...", ctx.OutputPath);
        await _scaffolder.EnsureTestProjectAsync(ctx.SourcePath, ctx.OutputPath);
    }

    private async Task StepDiscoverAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[1/10] Descobrindo classes em {S}...", ctx.SourcePath);
        var r = await _runtime.SendAsync<DiscoverProjectRequest, ProjectDiscoveredResponse>(
            new DiscoverProjectRequest(ctx.SourcePath) { CorrelationId = id }, ct);
        ctx.DiscoveredClasses = r.Classes;
        ctx.DiscoveredInterfaces = r.Interfaces;
    }

    private async Task StepGenerateFakesAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        if (!_config.Features.GenerateFakes) return;
        _logger.LogInformation("[2/10] Gerando Fakes em {O}/tests/Fakes...", ctx.OutputPath);
        var r = await _runtime.SendAsync<GenerateFakesRequest, FakesGeneratedResponse>(
            new GenerateFakesRequest(ctx.OutputPath, ctx.DiscoveredInterfaces) { CorrelationId = id }, ct);
        ctx.GeneratedFakes = r.GeneratedFakes;
    }

    private async Task StepGenerateTestsAsync(
        AgentContext ctx, string id,
        List<Domain.ValueObjects.CoverageGap> gaps,
        CancellationToken ct)
    {
        _logger.LogInformation("[3/10] Gerando testes xUnit em {O}/tests...", ctx.OutputPath);
        var r = await _runtime.SendAsync<GenerateTestsRequest, TestsGeneratedResponse>(
            new GenerateTestsRequest(ctx.OutputPath, ctx.DiscoveredClasses, gaps) { CorrelationId = id }, ct);
        ctx.GeneratedTests = r.GeneratedTests;
    }

    private async Task StepCompileAsync(AgentContext ctx, string id, int maxRetries, CancellationToken ct)
    {
        _logger.LogInformation("[4/10] Compilando testes...");
        await _runtime.SendWithRetryAsync<CompileFixRequest, CompileResultResponse>(
            new CompileFixRequest(ctx.OutputPath, await _dotnet.BuildAsync(ctx.OutputPath))
            { CorrelationId = id },
            maxRetries, r => r.Success, ct);
    }

    private async Task StepDebugTestsAsync(AgentContext ctx, string id, int maxRetries, CancellationToken ct)
    {
        _logger.LogInformation("[5/10] Executando testes...");
        var r = await _runtime.SendWithRetryAsync<DebugTestsRequest, TestsResultResponse>(
            new DebugTestsRequest(ctx.OutputPath, await _dotnet.RunTestsAsync(ctx.OutputPath))
            { CorrelationId = id },
            maxRetries, r => r.AllPassed, ct);
        ctx.LogicIssues.AddRange(r.BugsFound);
    }

    private async Task StepCoverageLoopAsync(
        AgentContext ctx, string id, PipelineOptions opts, CancellationToken ct)
    {
        _logger.LogInformation("[6/10] Cobertura (alvo {T}%)...", opts.CoverageThreshold);

        await _dotnet.RunTestsWithCoverageAsync(ctx.OutputPath);

        for (int i = 1; i <= opts.MaxRetriesPerAgent; i++)
        {
            var r = await _runtime.SendAsync<ReviewCoverageRequest, CoverageResultResponse>(
                new ReviewCoverageRequest(ctx.OutputPath, opts.CoverageThreshold)
                { CorrelationId = id }, ct);

            ctx.CurrentCoverage = r.Coverage;

            if (r.Coverage >= opts.CoverageThreshold)
            {
                _logger.LogInformation("  ✓ Cobertura: {C:F1}%", r.Coverage);
                return;
            }

            _logger.LogWarning("  ↩ {C:F1}% < {T}% — complementando testes ({I}/{M})",
                r.Coverage, opts.CoverageThreshold, i, opts.MaxRetriesPerAgent);

            if (i < opts.MaxRetriesPerAgent)
            {
                ctx.CoverageGaps = r.Gaps;
                await StepGenerateTestsAsync(ctx, id, r.Gaps, ct);
                await StepCompileAsync(ctx, id, maxRetries: 2, ct);
                await _dotnet.RunTestsWithCoverageAsync(ctx.OutputPath);
            }
        }
    }

    private async Task StepMutationAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        if (!_config.Features.MutationTesting) return;
        _logger.LogInformation("[7/10] Mutation testing (Stryker.NET)...");
        var r = await _runtime.SendAsync<RunMutationRequest, MutationResultResponse>(
            new RunMutationRequest(ctx.OutputPath) { CorrelationId = id }, ct);
        ctx.MutationScore = r.Score;
    }

    private async Task StepLogicAnalysisAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[8/10] Analisando lógica em {S}...", ctx.SourcePath);
        var r = await _runtime.SendAsync<AnalyzeLogicRequest, LogicAnalysisResponse>(
            new AnalyzeLogicRequest(ctx.SourcePath, ctx.DiscoveredClasses) { CorrelationId = id }, ct);
        ctx.LogicIssues.AddRange(r.Issues);
    }

    private async Task StepQualityAndArchitectureAsync(AgentContext ctx, string id, CancellationToken ct)
    {
        _logger.LogInformation("[9/10] Qualidade e arquitetura...");
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
        _logger.LogInformation("[10/10] Gerando relatórios em {O}/ai-test-reports...", ctx.OutputPath);
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
        _logger.LogInformation("  Cobertura alvo: {T}% | Workers: {W} | Retries: {R}",
            opts.CoverageThreshold, opts.ParallelWorkers, opts.MaxRetriesPerAgent);
        _logger.LogInformation("═══════════════════════════════════════════════════");
    }
}