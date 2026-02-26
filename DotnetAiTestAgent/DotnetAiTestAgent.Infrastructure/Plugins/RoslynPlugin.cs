using System.Text.Json;
using DotnetAiTestAgent.Domain.Entities;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using MethodInfo = DotnetAiTestAgent.Domain.Entities.MethodInfo;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Analisa código C# via Roslyn usando ParseText (análise sintática).
/// Não abre MSBuildWorkspace por padrão — mais rápido e sem conflito de MSBuild.
/// Use OpenMSBuildWorkspaceAsync() somente quando precisar de análise semântica.
///
/// NOTA sobre MSBuildLocator:
///   RegisterDefaults() deve ser chamado UMA VEZ em todo o processo.
///   O Lazy<bool> com guard garante isso mesmo em cenários multi-thread.
/// </summary>
public class RoslynPlugin
{
    private readonly ILogger<RoslynPlugin> _logger;

    private static readonly Lazy<bool> _msBuildInit = new(() =>
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        return true;
    });

    public RoslynPlugin(ILogger<RoslynPlugin> logger) => _logger = logger;

    public async Task<string> ExtractPublicClassesAsync(string projectPath)
    {
        var classes = new List<CSharpClassInfo>();

        foreach (var file in GetSourceFiles(projectPath))
        {
            try
            {
                var code = await File.ReadAllTextAsync(file);
                var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
                var ns   = ExtractNamespace(root);

                foreach (var cls in root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(IsPublic))
                {
                    classes.Add(new CSharpClassInfo
                    {
                        FilePath           = Path.GetRelativePath(projectPath, file),
                        ClassName          = cls.Identifier.Text,
                        Namespace          = ns,
                        SourceCode         = code,
                        PublicMethods      = ExtractMethods(cls),
                        Dependencies       = ExtractConstructorDeps(cls),
                        CyclomaticComplexity = ExtractMethods(cls).Sum(m => m.CyclomaticComplexity)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Erro ao analisar {F}: {E}", file, ex.Message);
            }
        }

        _logger.LogInformation("Roslyn: {N} classes extraídas", classes.Count);
        return JsonSerializer.Serialize(classes, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> ExtractInterfacesAsync(string projectPath)
    {
        var interfaces = new List<InterfaceInfo>();

        foreach (var file in GetSourceFiles(projectPath))
        {
            try
            {
                var code = await File.ReadAllTextAsync(file);
                var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
                var ns   = ExtractNamespace(root);

                foreach (var iface in root.DescendantNodes()
                    .OfType<InterfaceDeclarationSyntax>()
                    .Where(IsPublic))
                {
                    interfaces.Add(new InterfaceInfo
                    {
                        InterfaceName = iface.Identifier.Text,
                        Namespace     = ns,
                        FilePath      = Path.GetRelativePath(projectPath, file),
                        SourceCode    = code,
                        Methods       = iface.Members
                            .OfType<MethodDeclarationSyntax>()
                            .Select(m => new MethodInfo
                            {
                                Name       = m.Identifier.Text,
                                ReturnType = m.ReturnType.ToString(),
                                Parameters = m.ParameterList.Parameters
                                    .Select(p => p.ToString()).ToList(),
                                IsAsync    = m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword))
                            }).ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Erro ao analisar {F}: {E}", file, ex.Message);
            }
        }

        _logger.LogInformation("Roslyn: {N} interfaces extraídas", interfaces.Count);
        return JsonSerializer.Serialize(interfaces, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Abre workspace MSBuild para análise semântica (resolução de tipos).
    /// Use somente quando ParseText não for suficiente — mais lento.
    /// </summary>
    public async Task<Workspace> OpenMSBuildWorkspaceAsync(string projectPath)
    {
        _ = _msBuildInit.Value; // garante RegisterDefaults uma única vez

        var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();

        var sln = Directory.GetFiles(projectPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sln is not null)
        {
            await workspace.OpenSolutionAsync(sln);
        }
        else
        {
            var csproj = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains("Tests") && !f.Contains("tests"));
            if (csproj is not null)
                await workspace.OpenProjectAsync(csproj);
        }

        return workspace;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> GetSourceFiles(string projectPath)
    {
        var src = Path.Combine(projectPath, "src");
        var root = Directory.Exists(src) ? src : projectPath;

        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"));
    }

    private static bool IsPublic(MemberDeclarationSyntax m) =>
        m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword));

    private static string ExtractNamespace(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()
            ?.Name.ToString() ?? "Global";

    private static List<MethodInfo> ExtractMethods(ClassDeclarationSyntax cls) =>
        cls.Members.OfType<MethodDeclarationSyntax>()
            .Where(IsPublic)
            .Select(m => new MethodInfo
            {
                Name                 = m.Identifier.Text,
                ReturnType           = m.ReturnType.ToString(),
                Parameters           = m.ParameterList.Parameters.Select(p => p.ToString()).ToList(),
                IsAsync              = m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)),
                CyclomaticComplexity = CalculateCyclomaticComplexity(m)
            }).ToList();

    private static List<string> ExtractConstructorDeps(ClassDeclarationSyntax cls) =>
        cls.Members.OfType<ConstructorDeclarationSyntax>()
            .SelectMany(c => c.ParameterList.Parameters)
            .Select(p => p.Type?.ToString() ?? "")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

    private static int CalculateCyclomaticComplexity(MethodDeclarationSyntax m)
    {
        var n = 1;
        n += m.DescendantNodes().OfType<IfStatementSyntax>().Count();
        n += m.DescendantNodes().OfType<WhileStatementSyntax>().Count();
        n += m.DescendantNodes().OfType<ForStatementSyntax>().Count();
        n += m.DescendantNodes().OfType<ForEachStatementSyntax>().Count();
        n += m.DescendantNodes().OfType<SwitchSectionSyntax>().Count();
        n += m.DescendantNodes().OfType<CatchClauseSyntax>().Count();
        n += m.DescendantNodes().OfType<ConditionalExpressionSyntax>().Count();
        n += m.DescendantNodes().OfType<BinaryExpressionSyntax>()
               .Count(b => b.IsKind(SyntaxKind.LogicalAndExpression)
                        || b.IsKind(SyntaxKind.LogicalOrExpression));
        return n;
    }
}
