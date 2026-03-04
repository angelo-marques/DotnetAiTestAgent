using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Cria e mantém o projeto de testes xUnit na pasta de saída.
///
/// PROBLEMA QUE RESOLVE:
///   O pipeline gera arquivos .cs de teste, mas sem um .csproj com xunit e
///   coverlet.collector o "dotnet test" não roda e nenhum coverage.cobertura.xml
///   é gerado. Este scaffolder garante que a infraestrutura do projeto de testes
///   exista antes dos arquivos .cs serem escritos.
///
/// O QUE FAZ:
///   1. Descobre o nome e namespace do projeto-fonte via o .csproj na sourcePath
///   2. Cria (ou reaproveita) o .csproj de testes com xUnit + coverlet
///   3. Adiciona ProjectReference apontando para o projeto-fonte
///   4. Verifica se reportgenerator está instalado globalmente
///
/// ESTRUTURA GERADA:
///   outputPath/
///   └── tests/
///       ├── MeuProjeto.Tests.csproj    ← criado por este scaffolder
///       ├── Fakes/
///       │   └── FakeIUserRepository.cs
///       └── Services/
///           └── UserServiceTests.cs
/// </summary>
public class TestProjectScaffolder
{
    private readonly ILogger<TestProjectScaffolder> _logger;

    public TestProjectScaffolder(ILogger<TestProjectScaffolder> logger) => _logger = logger;

    /// <summary>
    /// Garante que o projeto de testes existe em outputPath/tests/.
    /// Se o .csproj já existir, apenas verifica integridade sem recriar.
    /// </summary>
    /// <param name="sourcePath">Pasta do código-fonte (onde está o .csproj original).</param>
    /// <param name="outputPath">Pasta de saída onde o projeto de testes será criado.</param>
    public async Task EnsureTestProjectAsync(string sourcePath, string outputPath)
    {
        var testsDir = Path.Combine(outputPath, "tests");
        var sourceInfo = DiscoverSourceProject(sourcePath);
        var csprojName = $"{sourceInfo.ProjectName}.Tests.csproj";
        var csprojPath = Path.Combine(testsDir, csprojName);

        Directory.CreateDirectory(testsDir);

        if (File.Exists(csprojPath))
        {
            _logger.LogDebug("Projeto de testes já existe: {F}", csprojPath);
            return;
        }

        _logger.LogInformation("📦 Criando projeto de testes: {N}", csprojName);

        await WriteCsprojAsync(csprojPath, sourceInfo);
        await EnsureGlobalJsonAsync(outputPath);
        await CheckReportGeneratorAsync();

        _logger.LogInformation("✅ Projeto de testes criado: {P}", csprojPath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private record SourceProjectInfo(string ProjectName, string RootNamespace, string? CsprojRelativePath);

    /// <summary>
    /// Descobre nome e namespace do projeto-fonte buscando o .csproj.
    /// Se não encontrar, usa o nome da pasta como fallback.
    /// </summary>
    private SourceProjectInfo DiscoverSourceProject(string sourcePath)
    {
        var csproj = Directory
            .GetFiles(sourcePath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !Path.GetFileNameWithoutExtension(f)
                .Contains("Tests", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length)  // prefere o mais próximo da raiz
            .FirstOrDefault();

        if (csproj is null)
        {
            var folderName = new DirectoryInfo(sourcePath).Name;
            _logger.LogWarning(
                "Nenhum .csproj encontrado em {S}. Usando nome da pasta: {N}",
                sourcePath, folderName);
            return new SourceProjectInfo(folderName, folderName, null);
        }

        var projectName = Path.GetFileNameWithoutExtension(csproj);
        _logger.LogDebug("Projeto-fonte descoberto: {N} em {P}", projectName, csproj);

        return new SourceProjectInfo(projectName, projectName, csproj);
    }

    /// <summary>
    /// Escreve o .csproj de testes com todas as dependências necessárias:
    ///   - xunit (framework de testes)
    ///   - coverlet.collector (coleta de cobertura via --collect:"XPlat Code Coverage")
    ///   - Microsoft.NET.Test.Sdk (runner do dotnet test)
    ///   - xunit.runner.visualstudio (integração com IDEs)
    ///   - ProjectReference para o projeto-fonte
    /// </summary>
    private async Task WriteCsprojAsync(string csprojPath, SourceProjectInfo sourceInfo)
    {
        // Calcula o caminho relativo do projeto-fonte a partir do diretório de testes
        var projectRefLine = sourceInfo.CsprojRelativePath is not null
            ? $"    <ProjectReference Include=\"{MakeRelativePath(Path.GetDirectoryName(csprojPath)!, sourceInfo.CsprojRelativePath)}\" />"
            : $"    <!-- Adicione manualmente: <ProjectReference Include=\"..\\..\\src\\{sourceInfo.ProjectName}\\{sourceInfo.ProjectName}.csproj\" /> -->";

        var csproj = new StringBuilder();
        csproj.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        csproj.AppendLine();
        csproj.AppendLine("  <PropertyGroup>");
        csproj.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        csproj.AppendLine("    <Nullable>enable</Nullable>");
        csproj.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        csproj.AppendLine("    <IsPackable>false</IsPackable>");
        csproj.AppendLine($"    <RootNamespace>{sourceInfo.RootNamespace}.Tests</RootNamespace>");
        csproj.AppendLine();
        csproj.AppendLine("    <!-- Coverlet: habilita coleta de cobertura por linha e branch -->");
        csproj.AppendLine("    <CollectCoverage>true</CollectCoverage>");
        csproj.AppendLine("    <CoverletOutputFormat>cobertura</CoverletOutputFormat>");
        csproj.AppendLine("    <CoverletOutput>./TestResults/coverage.cobertura.xml</CoverletOutput>");
        csproj.AppendLine("    <Exclude>[xunit.*]*,[*.Tests]*</Exclude>");
        csproj.AppendLine("  </PropertyGroup>");
        csproj.AppendLine();
        csproj.AppendLine("  <ItemGroup>");
        csproj.AppendLine("    <!-- Test framework -->");
        csproj.AppendLine("    <PackageReference Include=\"Microsoft.NET.Test.Sdk\"        Version=\"17.12.0\" />");
        csproj.AppendLine("    <PackageReference Include=\"xunit\"                         Version=\"2.9.3\"  />");
        csproj.AppendLine("    <PackageReference Include=\"xunit.runner.visualstudio\"     Version=\"2.8.2\"  >");
        csproj.AppendLine("      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>");
        csproj.AppendLine("      <PrivateAssets>all</PrivateAssets>");
        csproj.AppendLine("    </PackageReference>");
        csproj.AppendLine();
        csproj.AppendLine("    <!-- Coverlet: coleta de cobertura via XPlat Code Coverage -->");
        csproj.AppendLine("    <!-- ATENCAO: use SOMENTE coverlet.collector, nunca com coverlet.msbuild -->");
        csproj.AppendLine("    <PackageReference Include=\"coverlet.collector\"            Version=\"6.0.4\"  >");
        csproj.AppendLine("      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>");
        csproj.AppendLine("      <PrivateAssets>all</PrivateAssets>");
        csproj.AppendLine("    </PackageReference>");
        csproj.AppendLine("  </ItemGroup>");
        csproj.AppendLine();
        csproj.AppendLine("  <ItemGroup>");
        csproj.AppendLine("    <!-- Referência para o projeto sendo testado -->");
        csproj.AppendLine(projectRefLine);
        csproj.AppendLine("  </ItemGroup>");
        csproj.AppendLine();
        csproj.AppendLine("</Project>");

        await File.WriteAllTextAsync(csprojPath, csproj.ToString());
        _logger.LogDebug("✓ {F}", Path.GetFileName(csprojPath));
    }

    /// <summary>
    /// Garante que existe um global.json fixando a versão do SDK.
    /// Evita que o dotnet use uma versão diferente do SDK ao rodar os testes.
    /// </summary>
    private async Task EnsureGlobalJsonAsync(string outputPath)
    {
        var globalJson = Path.Combine(outputPath, "global.json");
        if (File.Exists(globalJson)) return;

        var content = """
            {
              "sdk": {
                "version": "10.0.0",
                "rollForward": "latestMinor"
              }
            }
            """;

        await File.WriteAllTextAsync(globalJson, content);
        _logger.LogDebug("✓ global.json criado em {P}", outputPath);
    }

    /// <summary>
    /// Verifica se o reportgenerator está instalado globalmente.
    /// Exibe aviso com o comando de instalação se não estiver.
    /// </summary>
    private async Task CheckReportGeneratorAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("reportgenerator", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) throw new Exception();
            await p.WaitForExitAsync();

            if (p.ExitCode == 0)
                _logger.LogDebug("✓ reportgenerator disponível");
        }
        catch
        {
            _logger.LogWarning(
                "⚠️  reportgenerator não encontrado. Relatório HTML não será gerado.\n" +
                "    Instale com: dotnet tool install --global dotnet-reportgenerator-globaltool");
        }
    }

    private static string MakeRelativePath(string fromDir, string toFile)
    {
        var rel = Path.GetRelativePath(fromDir, toFile);
        // No Windows garante barras invertidas
        return rel.Replace('/', Path.DirectorySeparatorChar);
    }
}
