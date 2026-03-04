using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

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

    // SanitizeCSharp: limpa TODOS os artefatos do LLM antes de salvar o .cs
    //
    // PROBLEMAS TRATADOS:
    //   1. Newlines literais "\n" (dois chars: barra+n) em vez de quebra real
    //      -> causa erros na linha 1 com colunas altissimas (ex: coluna 264)
    //      -> o LLM serializa o codigo como JSON interno e escapa as quebras
    //   2. \r\n e \r sozinhos -> normaliza para \n real
    //   3. Code fences: linhas com ``` -> descartadas
    //   4. Backticks inline: `valor` -> "valor" (corrige CS1056)
    //   5. @@ duplo arroba -> @ simples (corrige CS9008)
    //   6. Bold/italic markdown: **x** / *x* -> x
    //   7. Headers markdown: ## Titulo -> // Titulo
    //   8. Blockquotes: > texto -> // texto
    //   9. Epilogo de texto apos o ultimo } ou ;
    private string SanitizeCSharp(string raw, string fileName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // PASSO 1: normaliza newlines
        // O Falcon3 frequentemente retorna o codigo com "\n" literal (dois chars)
        // em vez de quebra de linha real. Isso faz TODO o arquivo ficar em 1 linha.
        var normalized = NormalizeNewlines(raw);

        var lines = normalized.Split('\n');
        var kept = new System.Collections.Generic.List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // PASSO 2: descarta code fences
            if (trimmed.StartsWith("```"))
                continue;

            var p = line;

            // PASSO 3: backticks inline -> corrige CS1056
            if (p.Contains('`'))
                p = FixInlineBackticks(p);

            // PASSO 4: @@ duplo arroba -> @ simples (corrige CS9008)
            if (p.Contains("@@"))
                p = p.Replace("@@", "@");

            // PASSO 5: bold/italic markdown
            if (p.Contains('*'))
                p = Regex.Replace(p, @"\*{1,2}([^*\n]+)\*{1,2}", "$1");

            // PASSO 6: headers markdown -> comentario C#
            if (trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
                p = "// " + trimmed.TrimStart('#').Trim();

            // PASSO 7: blockquotes -> comentario C#
            if (trimmed.StartsWith("> "))
                p = "// " + trimmed[2..];

            kept.Add(p);
        }

        var joined = string.Join('\n', kept).Trim();

        // PASSO 8: remove epilogo de texto apos o ultimo } ou ;
        var lastClose = Math.Max(joined.LastIndexOf('}'), joined.LastIndexOf(';'));
        if (lastClose > 0 && lastClose < joined.Length - 2)
        {
            var after = joined[(lastClose + 1)..].Trim();
            if (after.Length > 0 && !after.StartsWith("//") && !after.StartsWith("/*"))
                joined = joined[..(lastClose + 1)];
        }

        var result = joined.Trim();

        if (result.Length < raw.Length * 0.9)
            _logger.LogDebug("LLM output sanitizado: {F} ({B}->{A} chars)",
                fileName, raw.Length, result.Length);

        return result;
    }

    // Normaliza TODAS as variantes de newline que o LLM pode produzir:
    //
    //   "\\n"  (4 chars: dois backslashes + n) -> newline real
    //            ocorre quando o modelo escapa duas vezes
    //   "\n"    (2 chars: backslash + n literal) -> newline real
    //            ocorre quando o modelo serializa o codigo como texto JSON
    //   "\r\n" -> newline real (Windows CRLF serializado)
    //   "\r"    -> newline real (Mac CR antigo)
    //   "\r" real (carriage return) -> newline
    private static string NormalizeNewlines(string raw)
    {
        // Substitui sequencias literais de escape (texto, nao chars de controle)
        // Ordem importa: trata o mais longo primeiro
        var s = raw;

        // "\\n" literal (4 chars) -> newline
        s = s.Replace("\\r\\n", "\n");
        s = s.Replace("\\n", "\n");
        s = s.Replace("\\r", "\n");

        // Normaliza CRLF e CR reais para LF
        s = s.Replace("\r\n", "\n");
        s = s.Replace("\r", "\n");

        return s;
    }

    // Corrige backticks inline numa linha de codigo C#.
    // Em comentarios: remove backtick, mantém conteudo
    // Em contexto de valor (=, (, ,, [, :): converte para string literal
    // Identifier puro: remove backtick, mantém identifier
    // Backtick solto sem par: remove
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
                return isId ? inner : "\"" + inner + "\"";
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










