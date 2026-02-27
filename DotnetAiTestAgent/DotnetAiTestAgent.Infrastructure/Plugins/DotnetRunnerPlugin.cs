using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;
/// <summary>
/// Executa comandos dotnet (build, test, test com cobertura) e reportgenerator (HTML).
///
/// FLUXO DE COBERTURA:
///   RunTestsWithCoverageAsync
///     → dotnet test --collect:"XPlat Code Coverage"
///     → gera TestResults/{guid}/coverage.cobertura.xml
///     → chama GenerateHtmlReportAsync automaticamente
///
///   GenerateHtmlReportAsync
///     → reportgenerator -reports:*.xml -targetdir:ai-test-reports/coverage-html
///     → gera index.html navegável com detalhe por classe e linha
///
/// PRÉ-REQUISITO (instalar uma vez):
///   dotnet tool install --global dotnet-reportgenerator-globaltool
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

    public Task<string> BuildAsync(string projectPath)
    {
        var target = FindCsproj(projectPath, preferTests: true) ?? projectPath;
        return RunAsync("dotnet", $"build \"{target}\" --no-restore -v minimal");
    }

    public Task<string> RunTestsAsync(string projectPath)
    {
        var target = FindCsproj(projectPath, preferTests: true) ?? projectPath;
        return RunAsync("dotnet", $"test \"{target}\" --no-build -v normal");
    }

    public async Task<string> RunTestsWithCoverageAsync(string outputPath)
    {
        var testsDir = Path.Combine(outputPath, "tests");
        var target = FindCsproj(testsDir, preferTests: true) ?? testsDir;
        var resultsDir = Path.Combine(testsDir, "TestResults");

        Directory.CreateDirectory(resultsDir);

        _logger.LogDebug("Rodando testes com cobertura: {T}", target);

        await RunAsync("dotnet", $"restore \"{target}\" -v minimal");

        var result = await RunAsync("dotnet",
            $"test \"{target}\" " +
            $"--collect:\"XPlat Code Coverage\" " +
            $"--results-directory \"{resultsDir}\" " +
            $"-- DataCollectionRunSettings.DataCollectors.DataCollector" +
            $".Configuration.Format=cobertura");

        await GenerateHtmlReportAsync(outputPath);

        return result;
    }

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

        var result = await RunAsync("reportgenerator",
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
            {
                var text = await File.ReadAllTextAsync(summary);
                _logger.LogInformation("--- Resumo de Cobertura ---\n{S}", text.Trim());
            }
        }
        else
        {
            _logger.LogWarning(
                "⚠️  reportgenerator não gerou o HTML. " +
                "Instale com: dotnet tool install --global dotnet-reportgenerator-globaltool");
        }

        return result;
    }

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

    private async Task<string> RunAsync(string command, string arguments)
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
            ?? throw new InvalidOperationException($"Falha ao iniciar: {command} {arguments}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("STDERR [{Cmd}]: {E}", command, stderr.Trim());

        return string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";
    }
}