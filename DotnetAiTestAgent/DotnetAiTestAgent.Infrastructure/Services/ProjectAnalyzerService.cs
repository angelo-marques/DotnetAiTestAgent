using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotnetAiTestAgent.Infrastructure.Services;

public class ProjectAnalyzerService
{
    public async Task<IEnumerable<MetadataReference>> GetProjectReferencesAsync(string csprojPath)
    {
        using var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (o, e) =>
            Console.WriteLine($"[Aviso Workspace] {e.Diagnostic.Message}");

        Console.WriteLine($"Carregando projeto e resolvendo dependências: {csprojPath}...");
        var project = await workspace.OpenProjectAsync(csprojPath);

        var compilation = await project.GetCompilationAsync();

        if (compilation == null)
            throw new InvalidOperationException("Falha ao obter a compilação do projeto alvo.");

        return compilation.References;
    }
}
