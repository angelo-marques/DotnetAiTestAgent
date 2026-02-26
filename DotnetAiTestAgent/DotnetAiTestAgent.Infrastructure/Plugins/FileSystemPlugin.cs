using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;
/// <summary>
/// Acesso ao sistema de arquivos do projeto.
///
/// Leitura → sempre da SourcePath (código real a analisar)
/// Escrita → sempre da OutputPath (onde os artefatos gerados vão)
///
/// Essa separação permite:
///   - Analisar projetos sem permissão de escrita no diretório fonte
///   - Isolar os artefatos gerados em uma pasta dedicada
///   - Rodar em modo read-only sobre a source
/// </summary>
public class FileSystemPlugin
{
    private readonly string _sourcePath;
    private readonly string _outputPath;
    private readonly ILogger<FileSystemPlugin> _logger;

    public FileSystemPlugin(string sourcePath, string outputPath, ILogger<FileSystemPlugin> logger)
    {
        _sourcePath = sourcePath;
        _outputPath = outputPath;
        _logger = logger;
    }

    // ── Leitura (sempre na source) ─────────────────────────────────────────

    public async Task<string> ReadFileAsync(string relativePath)
    {
        var full = Path.Combine(_sourcePath, relativePath);
        return File.Exists(full)
            ? await File.ReadAllTextAsync(full)
            : $"Arquivo não encontrado: {relativePath}";
    }

    public IEnumerable<string> ListCSharpFiles()
    {
        var src = Path.Combine(_sourcePath, "src");
        var root = Directory.Exists(src) ? src : _sourcePath;

        return Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsNotGenerated)
            .Select(f => Path.GetRelativePath(_sourcePath, f));
    }

    // ── Escrita (sempre no output) ─────────────────────────────────────────

    /// <summary>
    /// Salva arquivo de teste em {OutputPath}/tests/{fileName}.
    /// Cria a pasta automaticamente se não existir.
    /// </summary>
    public async Task<string> WriteTestFileAsync(string fileName, string content)
    {
        var testsRoot = Path.GetFullPath(Path.Combine(_outputPath, "tests"));
        var safePath = Path.GetFullPath(Path.Combine(testsRoot, fileName));

        GuardOutputPath(safePath, testsRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
        await File.WriteAllTextAsync(safePath, content);

        _logger.LogDebug("✓ tests/{F}", fileName);
        return safePath;
    }

    /// <summary>
    /// Salva Fake em {OutputPath}/tests/Fakes/{fileName}.
    /// </summary>
    public async Task<string> WriteFakeFileAsync(string fileName, string content)
    {
        var fakesRoot = Path.GetFullPath(Path.Combine(_outputPath, "tests", "Fakes"));
        var safePath = Path.GetFullPath(Path.Combine(fakesRoot, fileName));

        GuardOutputPath(safePath, fakesRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
        await File.WriteAllTextAsync(safePath, content);

        _logger.LogDebug("✓ tests/Fakes/{F}", fileName);
        return safePath;
    }

    /// <summary>
    /// Lê o XML de cobertura gerado pelo coverlet.
    /// O coverlet escreve em {OutputPath}/TestResults — onde os testes rodaram.
    /// </summary>
    public async Task<string> ReadCoverageReportAsync()
    {
        var report = Directory
            .GetFiles(_outputPath, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (report is null)
        {
            _logger.LogWarning("Relatório de cobertura não encontrado em {O}", _outputPath);
            return string.Empty;
        }

        _logger.LogDebug("Lendo cobertura: {F}", report);
        return await File.ReadAllTextAsync(report);
    }

    /// <summary>
    /// Salva relatório em {OutputPath}/ai-test-reports/{reportName}.
    /// </summary>
    public async Task WriteReportAsync(string reportName, string content)
    {
        var path = Path.Combine(_outputPath, "ai-test-reports", reportName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        _logger.LogDebug("✓ ai-test-reports/{R}", reportName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void GuardOutputPath(string safePath, string allowedRoot)
    {
        if (!safePath.StartsWith(allowedRoot))
            throw new UnauthorizedAccessException(
                $"Tentativa de escrita fora da pasta permitida.\n" +
                $"  Tentou: {safePath}\n" +
                $"  Permitido: {allowedRoot}");
    }

    private static bool IsNotGenerated(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}");
}