using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Acesso ao sistema de arquivos do projeto.
///
/// Leitura → sempre da SourcePath (código real a analisar)
/// Escrita → sempre da OutputPath (onde os artefatos gerados vão)
///
/// ENCODING: todos os arquivos .cs são lidos e escritos em UTF-8 sem BOM.
/// Isso evita corrupção de caracteres acentuados no Windows (ã → ├ú).
/// </summary>
public class FileSystemPlugin
{
    private static readonly Encoding Utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _sourcePath;
    private readonly string _outputPath;
    private readonly ILogger<FileSystemPlugin> _logger;

    public FileSystemPlugin(string sourcePath, string outputPath, ILogger<FileSystemPlugin> logger)
    {
        _sourcePath = sourcePath;
        _outputPath = outputPath;
        _logger = logger;
    }

    public async Task<string> ReadFileAsync(string relativePath)
    {
        var full = Path.Combine(_sourcePath, relativePath);
        return File.Exists(full)
            ? await File.ReadAllTextAsync(full, Utf8NoBom)
            : $"Arquivo nao encontrado: {relativePath}";
    }

    public IEnumerable<string> ListCSharpFiles()
    {
        var src = Path.Combine(_sourcePath, "src");
        var root = Directory.Exists(src) ? src : _sourcePath;
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsNotGenerated)
            .Select(f => Path.GetRelativePath(_sourcePath, f));
    }

    public async Task<string> WriteTestFileAsync(string fileName, string content)
    {
        var testsRoot = Path.GetFullPath(Path.Combine(_outputPath, "tests"));
        var safePath = Path.GetFullPath(Path.Combine(testsRoot, fileName));
        GuardOutputPath(safePath, testsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
        var clean = SanitizeCSharp(content, fileName);
        await File.WriteAllTextAsync(safePath, clean, Utf8NoBom);
        _logger.LogDebug("tests/{F}", fileName);
        return safePath;
    }

    public async Task<string> WriteFakeFileAsync(string fileName, string content)
    {
        var fakesRoot = Path.GetFullPath(Path.Combine(_outputPath, "tests", "Fakes"));
        var safePath = Path.GetFullPath(Path.Combine(fakesRoot, fileName));
        GuardOutputPath(safePath, fakesRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
        var clean = SanitizeCSharp(content, fileName);
        await File.WriteAllTextAsync(safePath, clean, Utf8NoBom);
        _logger.LogDebug("tests/Fakes/{F}", fileName);
        return safePath;
    }

    public async Task<string> ReadCoverageReportAsync()
    {
        var report = Directory.GetFiles(_outputPath, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (report is null)
        {
            _logger.LogWarning("Cobertura nao encontrada em {O}", _outputPath);
            return string.Empty;
        }
        return await File.ReadAllTextAsync(report, Utf8NoBom);
    }

    public async Task WriteReportAsync(string reportName, string content)
    {
        var path = Path.Combine(_outputPath, "ai-test-reports", reportName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Utf8NoBom);
        _logger.LogDebug("ai-test-reports/{R}", reportName);
    }

    // SanitizeCSharp: remove artefatos de markdown do output do LLM
    // 1. Code fences (```) -> linha descartada
    // 2. Backticks inline -> string literal ou identifier (corrige CS1056)
    // 3. Bold/italic: **x** / *x* -> x
    // 4. Markdown headers: ## Titulo -> // Titulo
    // 5. Blockquotes: > texto -> // texto
    // 6. Epilogo de texto apos o ultimo } ou ;
    private string SanitizeCSharp(string raw, string fileName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var lines = raw.Split('\n');
        var kept = new System.Collections.Generic.List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```"))
                continue;

            var p = line;

            if (p.Contains('`'))
                p = FixInlineBackticks(p);

            if (p.Contains('*'))
                p = Regex.Replace(p, @"\*{1,2}([^*\n]+)\*{1,2}", "$1");

            if (trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
                p = "// " + trimmed.TrimStart('#').Trim();

            if (trimmed.StartsWith("> "))
                p = "// " + trimmed[2..];

            kept.Add(p);
        }

        var joined = string.Join('\n', kept).Trim();

        var lastClose = Math.Max(joined.LastIndexOf('}'), joined.LastIndexOf(';'));
        if (lastClose > 0 && lastClose < joined.Length - 2)
        {
            var after = joined[(lastClose + 1)..].Trim();
            if (after.Length > 0 && !after.StartsWith("//") && !after.StartsWith("/*"))
                joined = joined[..(lastClose + 1)];
        }

        var result = joined.Trim();

        if (result.Length < raw.Length * 0.9)
            _logger.LogDebug("Markdown removido de {F}: {B}->{A} chars",
                fileName, raw.Length, result.Length);

        return result;
    }

    private static string FixInlineBackticks(string line)
    {
        var trimmed = line.TrimStart();
        bool isComment = trimmed.StartsWith("//") || trimmed.StartsWith("*");

        var result = Regex.Replace(line, @"`([^`\r\n]*)`", match =>
        {
            var inner = match.Groups[1].Value;
            if (isComment) return inner;

            var idx = match.Index;
            var before = idx > 0 ? line[idx - 1] : ' ';
            bool isValueCtx = before == '=' || before == '('
                           || before == ',' || before == '['
                           || before == ':' || before == ' ';

            if (isValueCtx)
            {
                bool isId = Regex.IsMatch(inner, @"^[A-Za-z_][\w.<>\[\], ]*$");
                return isId ? inner : '"' + inner + '"';
            }
            return inner;
        });

        return result.Replace("`", "");
    }

    private static void GuardOutputPath(string safePath, string allowedRoot)
    {
        if (!safePath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                "Escrita fora da pasta permitida.\n" +
                "  Tentou:    " + safePath + "\n" +
                "  Permitido: " + allowedRoot);
    }

    private static bool IsNotGenerated(string path) =>
        !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
        !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
        !path.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar);
}