using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Detecta erros de compilação e aplica correções automáticas de infraestrutura:
///   - Pacotes NuGet faltando → dotnet add package
///   - Pacotes conflitantes   → dotnet remove package
///   - Restore necessário     → dotnet restore
///
/// FLUXO DE CORREÇÃO:
///   1. Lê o output do MSBuild/dotnet build
///   2. Detecta erros pelo código (CS0246, NETSDK, NU*, etc.)
///   3. Mapeia o tipo ausente para o pacote NuGet correto
///   4. Executa dotnet add package via ShellExecutorPlugin
///   5. Retorna lista de pacotes adicionados para o log
///
/// SEPARAÇÃO DE RESPONSABILIDADES:
///   - AutoFixPlugin  → decide O QUE corrigir (lógica de diagnóstico)
///   - ShellExecutorPlugin → decide COMO executar (segurança, OS, timeout)
///   - CompileFixAgent → orquestra tudo e chama o LLM para o restante
/// </summary>
public class AutoFixPlugin
{
    private readonly ShellExecutorPlugin _shell;
    private readonly ILogger<AutoFixPlugin> _logger;

    public AutoFixPlugin(ShellExecutorPlugin shell, ILogger<AutoFixPlugin> logger)
    {
        _shell  = shell;
        _logger = logger;
    }

    /// <summary>
    /// Analisa o output de build e aplica todas as correções de infraestrutura possíveis.
    /// Retorna true se pelo menos uma correção foi aplicada (sinaliza para novo build).
    /// </summary>
    public async Task<AutoFixResult> FixAsync(
        string buildOutput, string csprojPath, CancellationToken ct = default)
    {
        var applied = new List<string>();
        var failed  = new List<string>();

        // ── Detecta tipos ausentes e adiciona pacotes ──────────────────────────
        var missingTypes = ExtractMissingTypes(buildOutput);
        foreach (var typeName in missingTypes.Distinct())
        {
            var package = MapTypeToPackage(typeName);
            if (package is null)
            {
                _logger.LogDebug("[AutoFix] Tipo '{T}' sem mapeamento de pacote conhecido", typeName);
                continue;
            }

            _logger.LogInformation("[AutoFix] Adicionando pacote '{P}' para tipo '{T}'",
                package.Name, typeName);

            var result = await _shell.AddPackageAsync(csprojPath, package.Name, package.Version);

            if (result.Success)
                applied.Add($"dotnet add package {package.Name}");
            else
                failed.Add($"Falhou ao adicionar {package.Name}: {result.Output.Trim()}");
        }

        // ── Detecta conflito coverlet.msbuild + coverlet.collector ────────────
        if (buildOutput.Contains("InstrumentationTask") ||
            buildOutput.Contains("coverlet.msbuild.targets"))
        {
            _logger.LogWarning("[AutoFix] Conflito coverlet detectado — removendo coverlet.msbuild");
            var result = await _shell.RemovePackageAsync(csprojPath, "coverlet.msbuild");
            if (result.Success)
                applied.Add("dotnet remove package coverlet.msbuild");
        }

        // ── Restore após alterações ────────────────────────────────────────────
        if (applied.Count > 0)
        {
            _logger.LogInformation("[AutoFix] Restaurando pacotes após {N} correções...", applied.Count);
            await _shell.RestoreAsync(csprojPath);
        }

        // ── Detecta erros de restore (NU* errors) ─────────────────────────────
        var nugetErrors = ExtractNugetErrors(buildOutput);
        foreach (var error in nugetErrors)
        {
            _logger.LogWarning("[AutoFix] Erro NuGet: {E}", error);
            failed.Add(error);
        }

        return new AutoFixResult(
            AppliedFixes:  applied,
            FailedFixes:   failed,
            NeedsRebuild:  applied.Count > 0);
    }

    // ── Extração de erros ─────────────────────────────────────────────────────

    /// <summary>
    /// Extrai nomes de tipos ausentes de erros CS0246 e CS0234.
    ///
    /// Exemplos de linhas que gera:
    ///   error CS0246: O nome do tipo 'Faker' não pode ser encontrado
    ///   error CS0246: O nome do tipo 'MailAddress' não pode ser encontrado
    ///   error CS0246: The type or namespace name 'Bogus' could not be found
    /// </summary>
    private static IEnumerable<string> ExtractMissingTypes(string buildOutput)
    {
        // PT-BR: "O nome do tipo ou do namespace 'Xxx'"
        // EN:    "The type or namespace name 'Xxx'"
        var pattern = @"(?:nome do tipo ou do namespace|type or namespace name)\s+['""](\w+)['""]";
        var matches = Regex.Matches(buildOutput, pattern, RegexOptions.IgnoreCase);
        return matches.Select(m => m.Groups[1].Value);
    }

    private static IEnumerable<string> ExtractNugetErrors(string buildOutput)
    {
        var pattern = @"(NU\d{4}[^\n]*)";
        return Regex.Matches(buildOutput, pattern)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct();
    }

    // ── Mapeamento tipo → pacote NuGet ────────────────────────────────────────

    /// <summary>
    /// Mapeia um nome de tipo C# para o pacote NuGet que o fornece.
    ///
    /// Cobre os tipos mais comuns que o LLM usa nos testes gerados.
    /// Adicione novos mapeamentos conforme necessário — sem recompilar,
    /// basta adicionar no agent-prompts.json (futuro: externalizar este dicionário).
    /// </summary>
    private static NuGetPackage? MapTypeToPackage(string typeName) => typeName switch
    {
        // ── Bogus (dados de teste) ────────────────────────────────────────────
        "Faker"          => new("Bogus"),
        "Bogus"          => new("Bogus"),
        "FakerHub"       => new("Bogus"),
        "RuleSet"        => new("Bogus"),

        // ── FluentAssertions ──────────────────────────────────────────────────
        "FluentAssertions"  => new("FluentAssertions"),
        "AssertionOptions"  => new("FluentAssertions"),

        // ── xUnit extras ─────────────────────────────────────────────────────
        "AutoData"          => new("AutoFixture.Xunit2"),
        "AutoFixture"       => new("AutoFixture"),
        "Fixture"           => new("AutoFixture"),

        // ── Polly (resiliência) ───────────────────────────────────────────────
        "Policy"         => new("Polly"),
        "AsyncPolicy"    => new("Polly"),
        "ResiliencePipeline" => new("Polly", "8.0.0"),

        // ── MediatR ───────────────────────────────────────────────────────────
        "IMediator"      => new("MediatR"),
        "IRequest"       => new("MediatR"),
        "IRequestHandler"=> new("MediatR"),

        // ── Entity Framework ──────────────────────────────────────────────────
        "DbContext"      => new("Microsoft.EntityFrameworkCore"),
        "DbSet"          => new("Microsoft.EntityFrameworkCore"),
        "InMemoryDatabase" => new("Microsoft.EntityFrameworkCore.InMemory"),

        // ── ASP.NET Core (testes) ─────────────────────────────────────────────
        "WebApplicationFactory" => new("Microsoft.AspNetCore.Mvc.Testing"),
        "HttpClient"     => null,  // já está em System.Net.Http (BCL)
        "JsonContent"    => new("System.Net.Http.Json"),

        // ── Serialização ──────────────────────────────────────────────────────
        "JsonSerializer"    => null,  // System.Text.Json (BCL)
        "JsonConvert"       => new("Newtonsoft.Json"),
        "JObject"           => new("Newtonsoft.Json"),

        // ── Logging ───────────────────────────────────────────────────────────
        "ILogger"           => null,  // Microsoft.Extensions.Logging (BCL)
        "NullLogger"        => null,  // Microsoft.Extensions.Logging.Abstractions (BCL)
        "LoggerFactory"     => null,

        // ── Mappers ───────────────────────────────────────────────────────────
        "IMapper"           => new("AutoMapper"),
        "MapperConfiguration" => new("AutoMapper"),

        // ── Outros tipos BCL que não precisam de pacote ────────────────────────
        "MailAddress"       => null,  // System.Net.Mail (BCL) — só falta o using
        "SmtpClient"        => null,  // System.Net.Mail (BCL)

        _ => null
    };

    private record NuGetPackage(string Name, string? Version = null);
}

/// <summary>
/// Resultado do processo de auto-fix de infraestrutura.
/// </summary>
public record AutoFixResult(
    List<string> AppliedFixes,
    List<string> FailedFixes,
    bool NeedsRebuild);
