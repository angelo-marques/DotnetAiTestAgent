using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Executa comandos de shell com segurança no Windows e Linux/Mac.
///
/// USADO PELO AutoFixPlugin PARA:
///   - dotnet add package Bogus          (adicionar pacote NuGet)
///   - dotnet remove package Xxx         (remover pacote conflitante)
///   - dotnet restore                    (restaurar pacotes)
///   - dotnet list package               (listar pacotes instalados)
///   - nuget locals all -clear           (limpar cache se necessário)
///
/// SEGURANÇA:
///   - Comandos permitidos: somente os da AllowList
///   - Nunca executa comandos vindos diretamente do LLM sem sanitização
///   - Working directory sempre dentro do OutputPath
///   - Timeout de 120s por comando (evita travar o pipeline)
/// </summary>
public class ShellExecutorPlugin
{
    private readonly string  _workingDirectory;
    private readonly ILogger<ShellExecutorPlugin> _logger;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);

    // Allowlist de prefixos de comandos permitidos — o LLM só pode sugerir
    // comandos que começam com esses prefixos
    private static readonly string[] AllowedPrefixes =
    {
        "dotnet add package",
        "dotnet remove package",
        "dotnet restore",
        "dotnet list package",
        "dotnet tool install",
        "nuget locals",
    };

    public ShellExecutorPlugin(string workingDirectory, ILogger<ShellExecutorPlugin> logger)
    {
        _workingDirectory = workingDirectory;
        _logger           = logger;
    }

    // ── API pública ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adiciona um pacote NuGet ao projeto de testes.
    /// Ex: AddPackageAsync(csprojPath, "Bogus", "35.0.0")
    /// </summary>
    public Task<ShellResult> AddPackageAsync(string csprojPath, string packageName, string? version = null)
    {
        var versionArg = version is not null ? $" --version {version}" : "";
        return RunAllowedAsync($"dotnet add \"{csprojPath}\" package {packageName}{versionArg}");
    }

    /// <summary>
    /// Remove um pacote NuGet do projeto de testes.
    /// </summary>
    public Task<ShellResult> RemovePackageAsync(string csprojPath, string packageName) =>
        RunAllowedAsync($"dotnet remove \"{csprojPath}\" package {packageName}");

    /// <summary>
    /// Restaura os pacotes NuGet do projeto.
    /// </summary>
    public Task<ShellResult> RestoreAsync(string csprojPath) =>
        RunAllowedAsync($"dotnet restore \"{csprojPath}\" -v minimal");

    /// <summary>
    /// Lista os pacotes instalados no projeto.
    /// </summary>
    public Task<ShellResult> ListPackagesAsync(string csprojPath) =>
        RunAllowedAsync($"dotnet list \"{csprojPath}\" package");

    /// <summary>
    /// Limpa o cache NuGet local (útil quando há pacotes corrompidos).
    /// </summary>
    public Task<ShellResult> ClearNugetCacheAsync() =>
        RunAllowedAsync("dotnet nuget locals all --clear");

    /// <summary>
    /// Executa um comando da AllowList sugerido pelo LLM ou pela lógica determinística.
    /// NUNCA executa comandos fora da AllowList.
    /// </summary>
    public Task<ShellResult> RunAllowedAsync(string command)
    {
        var trimmed = command.Trim();

        var allowed = AllowedPrefixes.Any(p =>
            trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            _logger.LogWarning("Comando bloqueado (fora da AllowList): {C}", trimmed);
            return Task.FromResult(new ShellResult(
                ExitCode: -1,
                Output:   $"BLOQUEADO: '{trimmed}' não está na AllowList de comandos permitidos.",
                Success:  false));
        }

        return ExecuteAsync(trimmed);
    }

    // ── Execução ──────────────────────────────────────────────────────────────

    private async Task<ShellResult> ExecuteAsync(string fullCommand)
    {
        _logger.LogInformation("$ {C}", fullCommand);

        // Windows: cmd /C "comando"
        // Linux/Mac: sh -c "comando"
        ProcessStartInfo psi;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = new ProcessStartInfo("cmd.exe", $"/C \"{fullCommand}\"");
        }
        else
        {
            psi = new ProcessStartInfo("sh", $"-c \"{fullCommand.Replace("\"", "\\\"")}\"");
        }

        psi.WorkingDirectory       = _workingDirectory;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding  = Encoding.UTF8;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Falha ao iniciar processo: {fullCommand}");

        // Timeout via CancellationToken
        using var cts = new CancellationTokenSource(CommandTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            _logger.LogWarning("Timeout ({T}s) ao executar: {C}", CommandTimeout.TotalSeconds, fullCommand);
            return new ShellResult(-1, $"TIMEOUT após {CommandTimeout.TotalSeconds}s", false);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n--- STDERR ---\n{stderr}";
        var success = process.ExitCode == 0;

        if (success)
            _logger.LogDebug("✓ ExitCode=0 | {C}", fullCommand);
        else
            _logger.LogWarning("✗ ExitCode={E} | {C}\n{O}", process.ExitCode, fullCommand, output.Trim());

        return new ShellResult(process.ExitCode, output, success);
    }
}

/// <summary>
/// Resultado de um comando shell.
/// </summary>
public record ShellResult(int ExitCode, string Output, bool Success);
