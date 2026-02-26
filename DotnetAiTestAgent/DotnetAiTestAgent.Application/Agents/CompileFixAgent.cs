using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;
/// <summary>
/// Corrige erros de compilação nos testes gerados.
/// request.ProjectPath aqui é sempre o OutputPath — onde os testes foram escritos.
/// </summary>
public class CompileFixAgent : BaseAgent<CompileFixRequest, CompileResultResponse>
{
    private readonly FileSystemPlugin _fileSystem;

    public override string Name => "CompileFixAgent";

    public CompileFixAgent(IChatClient chat, FileSystemPlugin fileSystem, ILogger<CompileFixAgent> logger)
        : base(chat, logger) => _fileSystem = fileSystem;

    public override async Task<CompileResultResponse> HandleAsync(
        CompileFixRequest request, AgentThread thread, CancellationToken ct = default)
    {
        if (request.BuildOutput.Contains("Build succeeded"))
        {
            Logger.LogInformation("[{A}] ✓ Compilação OK", Name);
            return new CompileResultResponse(true, request.BuildOutput);
        }

        Logger.LogWarning("[{A}] Corrigindo erros (tentativa {R})", Name, thread.RetryCount + 1);

        var fixesJson = await CompleteAsync(SystemPrompt,
            $"ERROS DE COMPILAÇÃO:\n{request.BuildOutput}", thread, ct);

        await ApplyFixesAsync(fixesJson, request.ProjectPath, ct);

        return new CompileResultResponse(false, request.BuildOutput);
    }

    private async Task ApplyFixesAsync(string fixesJson, string outputPath, CancellationToken ct)
    {
        var fixes = TryDeserialize<List<CompileFix>>(fixesJson) ?? new();

        foreach (var fix in fixes)
        {
            // Usa outputPath diretamente — é o mesmo path que FileSystemPlugin.WriteTestFileAsync usa
            var testsRoot = Path.Combine(outputPath, "tests");
            var path = Path.GetFullPath(Path.Combine(testsRoot, fix.File));

            // Proteção: não escrever fora da pasta de testes
            if (!path.StartsWith(testsRoot) || !File.Exists(path)) continue;

            var content = await File.ReadAllTextAsync(path, ct);
            await File.WriteAllTextAsync(path, content.Replace(fix.OldCode, fix.NewCode), ct);
            Logger.LogDebug("[{A}] Fix: {F}", Name, fix.File);
        }
    }

    private record CompileFix(string File, string OldCode, string NewCode);

    private const string SystemPrompt = """
        Corrija erros de compilação C# nos arquivos de teste.

        REGRAS:
        - Corrija SOMENTE a sintaxe, NUNCA altere a lógica dos testes
        - Retorne JSON APENAS (sem markdown, sem explicações):
          [{"file":"NomeArquivo.cs","oldCode":"trecho original exato","newCode":"trecho corrigido"}]
        """;
}