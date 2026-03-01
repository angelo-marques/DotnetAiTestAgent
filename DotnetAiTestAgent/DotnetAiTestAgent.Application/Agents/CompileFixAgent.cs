using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Corrige erros de compilação nos testes gerados.
///
/// PROBLEMAS RESOLVIDOS:
///   - Encoding UTF-8 (0xC3): lê e escreve arquivos explicitamente em UTF-8 sem BOM
///   - Modelo responde em português/texto: prompt reforçado + circuit breaker no parse
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

        // Prompt com reforço de formato na tentativa > 0
        var userMsg = thread.RetryCount == 0
            ? $"ERROS DE COMPILAÇÃO:\n{request.BuildOutput}"
            : $"TENTATIVA {thread.RetryCount + 1} — RESPONDA SOMENTE JSON, SEM TEXTO.\n" +
              $"ERROS DE COMPILAÇÃO:\n{request.BuildOutput}";

        var fixesJson = await CompleteAsync(SystemPrompt, userMsg, thread, ct);

        var fixes = TryDeserialize<List<CompileFix>>(fixesJson);
        if (fixes is null || fixes.Count == 0)
        {
            Logger.LogDebug("[{A}] Nenhuma correção parseada da resposta", Name);
            return new CompileResultResponse(false, request.BuildOutput);
        }

        await ApplyFixesAsync(fixes, request.ProjectPath, ct);
        return new CompileResultResponse(false, request.BuildOutput);
    }

    private async Task ApplyFixesAsync(
        List<CompileFix> fixes, string outputPath, CancellationToken ct)
    {
        var testsRoot = Path.Combine(outputPath, "tests");

        foreach (var fix in fixes)
        {
            if (string.IsNullOrWhiteSpace(fix.File) ||
                string.IsNullOrWhiteSpace(fix.OldCode) ||
                string.IsNullOrWhiteSpace(fix.NewCode))
                continue;

            var path = Path.GetFullPath(Path.Combine(testsRoot, fix.File));

            // Proteção: não escrever fora da pasta de testes
            if (!path.StartsWith(testsRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
                continue;

            // Lê e escreve explicitamente em UTF-8 sem BOM para evitar o erro 0xC3
            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var content = await File.ReadAllTextAsync(path, encoding, ct);
            var updated = content.Replace(fix.OldCode, fix.NewCode);

            await File.WriteAllTextAsync(path, updated, encoding, ct);
            Logger.LogDebug("[{A}] Fix aplicado: {F}", Name, fix.File);
        }
    }

    private record CompileFix(string File, string OldCode, string NewCode);

    private const string SystemPrompt = """
        Você corrige erros de compilação C# em arquivos de teste.
        Sua ÚNICA saída deve ser um array JSON. NÃO escreva texto.
        NÃO use markdown. Comece DIRETAMENTE com [.

        Formato:
        [{"file":"NomeArquivo.cs","oldCode":"trecho original exato","newCode":"trecho corrigido"}]

        Regras:
        - Corrija SOMENTE sintaxe, NUNCA altere lógica dos testes
        - "oldCode" deve ser exatamente igual ao trecho no arquivo (case-sensitive)
        - Se não houver correção possível: []
        """;
}