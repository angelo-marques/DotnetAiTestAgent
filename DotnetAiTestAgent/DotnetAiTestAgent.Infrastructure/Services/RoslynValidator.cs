using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;


namespace DotnetAiTestAgent.Infrastructure.Services;
public class RoslynValidator
{
    public (bool IsValid, string Errors) ValidateCode(string generatedCode, IEnumerable<MetadataReference> references)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(generatedCode);

        // Criamos um assembly dinâmico em memória
        var compilation = CSharpCompilation.Create("DynamicTestAssembly")
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTree);

        var diagnostics = compilation.GetDiagnostics();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        if (errors.Count == 0)
            return (true, string.Empty);

        // Formata os erros do compilador para a IA entender o que consertar
        var errorMessages = errors.Select(e =>
        {
            var lineSpan = e.Location.GetLineSpan();
            return $"Linha {lineSpan.StartLinePosition.Line + 1}: {e.GetMessage()}";
        });

        return (false, string.Join("\n", errorMessages));
    }
}