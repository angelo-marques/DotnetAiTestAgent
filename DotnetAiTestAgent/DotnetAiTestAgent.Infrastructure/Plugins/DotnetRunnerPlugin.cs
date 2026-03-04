using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;
/// <summary>
/// Executa comandos dotnet (build, test, test com cobertura) e reportgenerator (HTML).
///
/// FLUXO COMPLETO DE COBERTURA:
///   RunTestsWithCoverageAsync(outputPath)
///     1. Localiza o .csproj de testes em outputPath/tests/
///     2. dotnet restore   (garante coverlet nos pacotes)
///     3. dotnet build     (compila — log de erro visível se falhar)
///     4. dotnet test --collect:"XPlat Code Coverage"
///        → gera: outputPath/tests/TestResults/{guid}/coverage.cobertura.xml
///     5. GenerateHtmlReportAsync
///        → gera: outputPath/ai-test-reports/coverage-html/index.html
/// </summary>
public class DotnetRunnerPlugin
{
    private readonly string _workingDirectory;
    private readonly ILogger<DotnetRunnerPlugin> _logger;

    public DotnetRunnerPlugin(string workingDirectory, ILogger<DotnetRunnerPlugin> logger)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    // ── Build ──────────────────────────────────────────────────────────────────

    public Task<string> BuildAsync(string projectPath)
    {
        var target = FindCsproj(projectPath, preferTests: true) ?? projectPath;
        return RunAsync("dotnet", $"build \"{target}\" --no-restore -v minimal");
    }

    // ── Testes simples (sem cobertura) ────────────────────────────────────────

    public Task<string> RunTestsAsync(string projectPath)
    {
        var target = FindCsproj(projectPath, preferTests: true) ?? projectPath;
        // Sem --no-build: garante que o projeto compila antes de rodar os testes
        return RunAsync("dotnet", $"test \"{target}\" -v normal");
    }

    // ── Testes com cobertura (XML + HTML) ─────────────────────────────────────

    public async Task<string> RunTestsWithCoverageAsync(string outputPath)
    {
        var testsDir = Path.Combine(outputPath, "tests");
        var target = FindCsproj(testsDir, preferTests: true);
        var resultsDir = Path.Combine(testsDir, "TestResults");

        if (target is null)
        {
            _logger.LogError(
                "Nenhum .csproj encontrado em {D}. " +
                "O TestProjectScaffolder deve ter criado o projeto antes desta etapa.",
                testsDir);
            return string.Empty;
        }

        Directory.CreateDirectory(resultsDir);

        _logger.LogInformation("📦 restore: {T}", Path.GetFileName(target));
        await RunWithLogAsync("dotnet", $"restore \"{target}\" -v minimal");

        _logger.LogInformation("🔨 build: {T}", Path.GetFileName(target));
        var buildOut = await RunWithLogAsync("dotnet", $"build \"{target}\" -v minimal");

        if (buildOut.Contains("Error") || buildOut.Contains("FAILED"))
        {
            _logger.LogError(
                "Build do projeto de testes falhou — cobertura não será coletada.\n{O}",
                buildOut);
            return buildOut;
        }

        _logger.LogInformation("🧪 test com cobertura: {T}", Path.GetFileName(target));
        var testOut = await RunWithLogAsync("dotnet",
            $"test \"{target}\" " +
            $"--no-build " +
            $"--collect:\"XPlat Code Coverage\" " +
            $"--results-directory \"{resultsDir}\" " +
            $"-- DataCollectionRunSettings.DataCollectors.DataCollector" +
            $".Configuration.Format=cobertura");

        // Verifica se o XML foi gerado
        var xmlFiles = Directory
            .GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (!xmlFiles.Any())
        {
            _logger.LogWarning(
                "coverage.cobertura.xml não gerado em {R}.\n" +
                "Verifique se o .csproj tem coverlet.collector instalado.\n" +
                "Saída do dotnet test:\n{O}",
                resultsDir, testOut);
        }
        else
        {
            _logger.LogDebug("✓ coverage XML: {F}", xmlFiles[0]);
            await GenerateHtmlReportAsync(outputPath);
        }

        return testOut;
    }

    // ── Relatório HTML ────────────────────────────────────────────────────────

    public async Task<string> GenerateHtmlReportAsync(string outputPath)
    {
        var xmlFiles = Directory
            .GetFiles(outputPath, "coverage.cobertura.xml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        if (!xmlFiles.Any())
        {
            _logger.LogWarning(
                "Nenhum coverage.cobertura.xml encontrado em {O}. " +
                "Verifique se o projeto de testes tem coverlet.collector instalado.",
                outputPath);
            return string.Empty;
        }

        var reports = string.Join(";", xmlFiles.Select(f => $"\"{f}\""));
        var htmlDir = Path.Combine(outputPath, "ai-test-reports", "coverage-html");
        Directory.CreateDirectory(htmlDir);

        _logger.LogInformation("📊 Gerando relatório HTML em: {D}", htmlDir);

        var result = await RunWithLogAsync("reportgenerator",
            $"-reports:{reports} " +
            $"-targetdir:\"{htmlDir}\" " +
            $"-reporttypes:Html;HtmlSummary;Badges;TextSummary;Cobertura " +
            $"-title:\"Cobertura — DotnetAiTestAgent\" " +
            $"-verbosity:Warning");

        var indexHtml = Path.Combine(htmlDir, "index.html");
        if (File.Exists(indexHtml))
        {
            _logger.LogInformation("✅ Relatório HTML: {F}", indexHtml);
            var summary = Path.Combine(htmlDir, "Summary.txt");
            if (File.Exists(summary))
                _logger.LogInformation("--- Resumo ---\n{S}",
                    (await File.ReadAllTextAsync(summary)).Trim());
        }
        else
        {
            _logger.LogWarning(
                "reportgenerator não gerou index.html.\n" +
                "Instale com: dotnet tool install --global dotnet-reportgenerator-globaltool");
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? FindCsproj(string dir, bool preferTests)
    {
        if (!Directory.Exists(dir)) return null;

        var all = Directory
            .GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(dir, f);
                return !rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");
            })
            .ToList();

        if (!all.Any()) return null;

        bool IsTest(string f)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
        }

        if (preferTests)
        {
            var found = all.FirstOrDefault(IsTest);
            if (found is not null) { _logger.LogDebug("Projeto de testes: {F}", found); return found; }
        }
        else
        {
            var found = all.FirstOrDefault(f => !IsTest(f));
            if (found is not null) return found;
        }

        return all[0];
    }

    /// <summary>
    /// RunAsync com log de erro visível (Warning) quando ExitCode != 0.
    /// Substitui o RunAsync anterior que só logava em Debug.
    /// </summary>
    private async Task<string> RunWithLogAsync(string command, string arguments)
    {
        _logger.LogDebug("$ {Cmd} {Args}", command, arguments);

        var psi = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Falha ao iniciar: {command}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";

        // Log visível (Warning) quando o processo falha — antes só era Debug
        if (process.ExitCode != 0)
            _logger.LogWarning("[{Cmd}] ExitCode={Code}\n{Out}",
                command, process.ExitCode, combined.Trim());

        return combined;
    }

    // Mantém compatibilidade com o nome antigo
    private Task<string> RunAsync(string command, string arguments) =>
        RunWithLogAsync(command, arguments);
}