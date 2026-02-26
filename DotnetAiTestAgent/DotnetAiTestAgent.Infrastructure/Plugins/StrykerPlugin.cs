using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Executa o Stryker.NET e extrai o mutation score do output.
/// Requer: dotnet tool install --global dotnet-stryker
/// </summary>
public class StrykerPlugin
{
    private readonly string _workingDirectory;
    private readonly ILogger<StrykerPlugin> _logger;

    public StrykerPlugin(string workingDirectory, ILogger<StrykerPlugin> logger)
    {
        _workingDirectory = workingDirectory;
        _logger           = logger;
    }

    public async Task<string> RunAsync(string projectPath)
    {
        _logger.LogInformation("Stryker.NET iniciando em {P}...", projectPath);

        var psi = new ProcessStartInfo("dotnet", "stryker --reporter progress --reporter json")
        {
            WorkingDirectory       = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi);
        if (process is null) return "0";

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return ExtractScore(output);
    }

    private static string ExtractScore(string output)
    {
        var scoreLine = output.Split('\n')
            .FirstOrDefault(l => l.Contains("Mutation score:") || l.Contains("All files"));

        if (scoreLine is null) return "0";

        foreach (var part in scoreLine.Split(new[] { '%', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                return score.ToString("F1", CultureInfo.InvariantCulture);

        return "0";
    }
}

/// <summary>
/// Consulta o git para identificar arquivos alterados desde o último commit.
/// Usado no modo incremental para processar apenas o que mudou.
/// </summary>
public class GitPlugin
{
    private readonly string _workingDirectory;
    private readonly ILogger<GitPlugin> _logger;

    public GitPlugin(string workingDirectory, ILogger<GitPlugin> logger)
    {
        _workingDirectory = workingDirectory;
        _logger           = logger;
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesAsync()
    {
        var psi = new ProcessStartInfo("git", "diff --name-only HEAD --diff-filter=ACM")
        {
            WorkingDirectory       = _workingDirectory,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi);
        if (process is null) return Array.Empty<string>();

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.EndsWith(".cs") &&
                        !f.Contains("/tests/") &&
                        !f.Contains("/obj/"))
            .ToList()
            .AsReadOnly();
    }
}
