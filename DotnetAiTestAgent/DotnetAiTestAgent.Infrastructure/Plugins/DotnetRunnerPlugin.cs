using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Executa comandos dotnet (build, test, test com cobertura).
/// Encapsula Process para facilitar testes e mocking futuro.
/// </summary>
public class DotnetRunnerPlugin
{
    private readonly string _workingDirectory;
    private readonly ILogger<DotnetRunnerPlugin> _logger;

    public DotnetRunnerPlugin(string workingDirectory, ILogger<DotnetRunnerPlugin> logger)
    {
        _workingDirectory = workingDirectory;
        _logger           = logger;
    }

    public Task<string> BuildAsync(string projectPath) =>
        RunAsync("dotnet", $"build \"{projectPath}\" --no-restore -v minimal");

    public Task<string> RunTestsAsync(string projectPath) =>
        RunAsync("dotnet", $"test \"{projectPath}\" --no-build -v normal");

    public Task<string> RunTestsWithCoverageAsync(string projectPath)
    {
        var resultsDir = Path.Combine(projectPath, "TestResults");
        return RunAsync("dotnet",
            $"test \"{projectPath}\" " +
            $"--collect:\"XPlat Code Coverage\" " +
            $"--results-directory \"{resultsDir}\" " +
            $"-- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura");
    }

    private async Task<string> RunAsync(string command, string arguments)
    {
        _logger.LogDebug("$ {Cmd} {Args}", command, arguments);

        var psi = new ProcessStartInfo(command, arguments)
        {
            WorkingDirectory       = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Falha ao iniciar: {command}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";
    }
}
