using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DotnetAiTestAgent.Application.Agents;

/// <summary>
/// Corrige erros de compilação nos testes gerados.
///
/// TRÊS NÍVEIS DE CORREÇÃO (executados em ordem):
///
///   Nível 1 — Infraestrutura (ShellExecutorPlugin + AutoFixPlugin)
///     Detecta pacotes NuGet faltando e executa "dotnet add package" no SO.
///     Funciona no Windows e no Linux. Sem chamar o LLM.
///
///   Nível 2 — Determinístico (sem LLM)
///     Padrões conhecidos que o Falcon3 gera frequentemente:
///     [Values] NUnit → [InlineData] xUnit, new DateTime() em InlineData,
///     using faltando para tipos BCL (MailAddress, etc.).
///
///   Nível 3 — LLM com auto-correção (BaseAgent.TryDeserializeWithCorrectionAsync)
///     Para o restante. Se o LLM retornar texto em vez de JSON,
///     o prompt de self-correction do agent-prompts.json é injetado automaticamente.
/// </summary>
public class CompileFixAgent : BaseAgent<CompileFixRequest, CompileResultResponse>
{
    private readonly FileSystemPlugin _fileSystem;
    private readonly AutoFixPlugin _autoFix;

    private static readonly Encoding Utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public override string Name => "CompileFixAgent";

    public CompileFixAgent(
        IChatClient chat,
        PromptRepository prompts,
        FileSystemPlugin fileSystem,
        AutoFixPlugin autoFix,
        ILogger<CompileFixAgent> logger)
        : base(chat, prompts, logger)
    {
        _fileSystem = fileSystem;
        _autoFix = autoFix;
    }

    public override async Task<CompileResultResponse> HandleAsync(
        CompileFixRequest request, AgentThread thread, CancellationToken ct = default)
    {
        if (request.BuildOutput.Contains("Build succeeded"))
        {
            Logger.LogInformation("[{A}] ✓ Compilação OK", Name);
            return new CompileResultResponse(true, request.BuildOutput);
        }

        Logger.LogWarning("[{A}] Corrigindo erros (tentativa {R})", Name, thread.RetryCount + 1);

        // ── Nível 1: infraestrutura — pacotes NuGet, conflitos, restore ───────
        var csproj = FindTestCsproj(request.ProjectPath);
        var autoResult = await _autoFix.FixAsync(request.BuildOutput, csproj, ct);

        if (autoResult.AppliedFixes.Count > 0)
        {
            Logger.LogInformation("[{A}] 🔧 {N} fix(es) de infraestrutura via shell:",
                Name, autoResult.AppliedFixes.Count);
            foreach (var fix in autoResult.AppliedFixes)
                Logger.LogInformation("[{A}]   → {F}", Name, fix);
        }

        if (autoResult.FailedFixes.Count > 0)
        {
            foreach (var fail in autoResult.FailedFixes)
                Logger.LogWarning("[{A}] ⚠ {F}", Name, fail);
        }

        // ── Nível 2: correções determinísticas nos .cs (sem LLM) ─────────────
        var det = await ApplyDeterministicFixesAsync(request.ProjectPath, request.BuildOutput, ct);
        if (det > 0)
            Logger.LogInformation("[{A}] ✏ {N} correção(ões) determinística(s) nos .cs", Name, det);

        // ── Nível 3: LLM para o restante, com auto-correção embutida ──────────
        var userMsg = thread.RetryCount == 0
            ? $"ERROS DE COMPILAÇÃO:\n{request.BuildOutput}"
            : $"TENTATIVA {thread.RetryCount + 1} — APENAS JSON.\nERROS:\n{request.BuildOutput}";

        var system = GetSystemPrompt();
        var raw = await CompleteAsync(system, userMsg, thread, ct);
        var fixes = await TryDeserializeWithCorrectionAsync<List<CompileFix>>(raw, thread, ct);

        if (fixes?.Count > 0)
            await ApplyLlmFixesAsync(fixes, request.ProjectPath, ct);

        return new CompileResultResponse(false, request.BuildOutput);
    }

    // ── Nível 2: correções determinísticas nos arquivos .cs ───────────────────

    private async Task<int> ApplyDeterministicFixesAsync(
        string outputPath, string buildOutput, CancellationToken ct)
    {
        var testsDir = Path.Combine(outputPath, "tests");
        if (!Directory.Exists(testsDir)) return 0;

        var csFiles = Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories);
        int count = 0;

        foreach (var file in csFiles)
        {
            var original = await File.ReadAllTextAsync(file, Utf8NoBom, ct);
            var patched = ApplyPatterns(original, buildOutput, file);

            if (patched == original) continue;

            await File.WriteAllTextAsync(file, patched, Utf8NoBom, ct);
            Logger.LogDebug("[{A}] Patches determinísticos: {F}", Name, Path.GetFileName(file));
            count++;
        }

        return count;
    }

    private string ApplyPatterns(string code, string buildOutput, string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        // NUnit [Values] → xUnit [InlineData]
        if (buildOutput.Contains(fileName) && buildOutput.Contains("ValuesAttribute"))
            code = Regex.Replace(code, @"\[Values\(", "[InlineData(");

        // NUnit [TestCase] / MSTest [DataRow] → xUnit [InlineData]
        code = code.Replace("[TestCase(", "[InlineData(")
                   .Replace("[DataRow(", "[InlineData(");

        // [InlineData(new Xxx(...))] — não é constante → corrige
        if (buildOutput.Contains(fileName) && buildOutput.Contains("CS0182"))
            code = FixNonConstantInlineData(code, fileName);

        // Usings BCL faltando (tipos que já existem no runtime, só falta o using)
        code = EnsureUsing(code, "MailAddress", "System.Net.Mail");
        code = EnsureUsing(code, "SmtpClient", "System.Net.Mail");
        code = EnsureUsing(code, "NetworkCredential", "System.Net");
        code = EnsureUsing(code, "IPAddress", "System.Net");
        code = EnsureUsing(code, "HttpClient", "System.Net.Http");
        code = EnsureUsing(code, "Encoding", "System.Text");
        code = EnsureUsing(code, "CultureInfo", "System.Globalization");
        code = EnsureUsing(code, "Regex", "System.Text.RegularExpressions");
        code = EnsureUsing(code, "JsonSerializer", "System.Text.Json");

        return code;
    }

    private string FixNonConstantInlineData(string code, string fileName)
    {
        var lines = code.Split('\n').ToList();
        var cleaned = new List<string>();
        bool changed = false;

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("[InlineData(") &&
                (t.Contains("new ") || t.Contains("DateTime") ||
                 t.Contains("TimeSpan") || t.Contains("Guid.")))
            {
                var fixed_ = FixInlineDataLine(line);
                cleaned.Add(fixed_);
                changed = true;
                Logger.LogDebug("[{A}] {F}: [InlineData] não-constante corrigido", Name, fileName);
            }
            else cleaned.Add(line);
        }

        return changed ? string.Join('\n', cleaned) : code;
    }

    private static string FixInlineDataLine(string line)
    {
        if (line.Contains("DateTime"))
            return Regex.Replace(line, @"new DateTime\([^)]*\)", "\"2024-01-01\"");
        if (line.Contains("TimeSpan"))
            return Regex.Replace(line, @"new TimeSpan\([^)]*\)", "0");
        if (line.Contains("Guid"))
            return Regex.Replace(line, @"(?:new Guid\([^)]*\)|Guid\.NewGuid\(\))",
                "\"00000000-0000-0000-0000-000000000000\"");
        return Regex.Replace(line, @"new \w+\([^)]*\)", "null");
    }

    private static string EnsureUsing(string code, string typeName, string ns)
    {
        var usingLine = $"using {ns};";
        if (!code.Contains(typeName) || code.Contains(usingLine)) return code;

        var lastUsing = code.LastIndexOf("\nusing ");
        if (lastUsing < 0) return usingLine + "\n" + code;

        var insertAt = code.IndexOf('\n', lastUsing + 1);
        if (insertAt < 0) insertAt = code.Length;
        return code[..(insertAt + 1)] + usingLine + "\n" + code[(insertAt + 1)..];
    }

    // ── Nível 3: aplicação de fixes do LLM ────────────────────────────────────

    private async Task ApplyLlmFixesAsync(
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
            if (!path.StartsWith(testsRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path)) continue;

            var content = await File.ReadAllTextAsync(path, Utf8NoBom, ct);
            var updated = content.Replace(fix.OldCode, fix.NewCode);

            if (updated == content) continue;

            await File.WriteAllTextAsync(path, updated, Utf8NoBom, ct);
            Logger.LogDebug("[{A}] LLM fix: {F}", Name, fix.File);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FindTestCsproj(string outputPath)
    {
        var testsDir = Path.Combine(outputPath, "tests");
        return Directory
            .GetFiles(testsDir, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault() ?? testsDir;
    }

    private record CompileFix(string File, string OldCode, string NewCode);
}
