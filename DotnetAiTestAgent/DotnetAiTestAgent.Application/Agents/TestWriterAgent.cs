using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Domain.ValueObjects;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;


/// <summary>
/// Gera testes xUnit para cada classe pública usando os Fakes gerados.
/// Suporta retroalimentação via CoverageGaps — quando a cobertura está abaixo
/// do threshold, recebe os gaps e reescreve testes focando nas linhas não cobertas.
/// Processa classes em paralelo com controle de concorrência via SemaphoreSlim.
/// </summary>
public class TestWriterAgent : BaseAgent<GenerateTestsRequest, TestsGeneratedResponse>
{
    private readonly FileSystemPlugin _fileSystem;

    public override string Name => "TestWriterAgent";

    public TestWriterAgent(IChatClient chat, PromptRepository prompts, FileSystemPlugin fileSystem, ILogger<TestWriterAgent> logger)
        : base(chat, prompts, logger) => _fileSystem = fileSystem;

    public override async Task<TestsGeneratedResponse> HandleAsync(
        GenerateTestsRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var generated = new List<string>();
        var semaphore = new SemaphoreSlim(initialCount: 4); // máx 4 classes em paralelo

        // Modo retroalimentação: filtra só as classes com gaps de cobertura
        var toProcess = request.Gaps.Any()
            ? request.Classes.Where(c => request.Gaps.Any(g => g.ClassName == c.ClassName)).ToList()
            : request.Classes;

        Logger.LogInformation("[{A}] Processando {N} classe(s)...", Name, toProcess.Count);

        await Parallel.ForEachAsync(toProcess, ct, async (cls, token) =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                // Thread isolada por classe: evita que o contexto de uma classe
                // contamine os testes de outra classe gerada em paralelo
                var classThread = new AgentThread();
                var gapHint = BuildGapHint(cls.ClassName, request.Gaps);

                var code = await CompleteAsync(Prompts.GetSystem(Name), BuildUserPrompt(cls.SourceCode, gapHint), classThread, token);
                var fileName = $"{cls.ClassName}Tests.cs";

                await _fileSystem.WriteTestFileAsync(fileName, code);

                lock (generated) generated.Add(fileName);
                Logger.LogInformation("[{A}] ✓ {F}", Name, fileName);
            }
            finally { semaphore.Release(); }
        });

        return new TestsGeneratedResponse(generated);
    }

    private static string BuildGapHint(string className, List<CoverageGap> gaps)
    {
        var classGaps = gaps.Where(g => g.ClassName == className).ToList();
        if (!classGaps.Any()) return string.Empty;

        var lines = string.Join(", ", classGaps.SelectMany(g => g.UncoveredLines));
        var methods = string.Join(", ", classGaps.Select(g => g.MethodName).Distinct());
        return $"\n\nFOQUE NAS LINHAS NÃO COBERTAS: {lines}\nMÉTODOS SEM COBERTURA: {methods}";
    }

    private static string BuildUserPrompt(string sourceCode, string gapHint) =>
        $"Classe a testar:\n```csharp\n{sourceCode}\n```{gapHint}";

}