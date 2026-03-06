using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Text;

namespace DotnetAiTestAgent.Infrastructure.Services;
public class InterfaceExtractorService
{
    public async Task<string> ExtractInterfacesFromConstructorAsync(Project targetProject, string classFilePath)
    {
        // 1. Encontra o arquivo da classe dentro do projeto carregado pelo MSBuildWorkspace
        var document = targetProject.Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, classFilePath, StringComparison.OrdinalIgnoreCase));

        if (document == null)
            throw new FileNotFoundException($"O arquivo {classFilePath} não foi encontrado no projeto.");

        // 2. Pega a árvore de sintaxe e o modelo semântico (o "cérebro" do compilador)
        var syntaxTree = await document.GetSyntaxTreeAsync();
        var semanticModel = await document.GetSemanticModelAsync();
        var root = await syntaxTree!.GetRootAsync();

        // 3. Encontra o primeiro construtor da classe
        var constructor = root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault();

        if (constructor == null)
            return string.Empty; // Não tem construtor, logo não tem injeção via construtor

        var interfaceCodes = new List<string>();

        // 4. Analisa os parâmetros do construtor
        foreach (var parameter in constructor.ParameterList.Parameters)
        {
            if (parameter.Type == null) continue;

            // Usa o modelo semântico para descobrir qual é o TIPO real do parâmetro
            var typeSymbol = semanticModel!.GetSymbolInfo(parameter.Type).Symbol as INamedTypeSymbol;

            // Se for uma interface...
            if (typeSymbol != null && typeSymbol.TypeKind == TypeKind.Interface)
            {
                // Busca onde essa interface foi escrita no código-fonte
                var syntaxReference = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();

                if (syntaxReference != null)
                {
                    var interfaceSyntax = await syntaxReference.GetSyntaxAsync();

                    // Adiciona o código puro da interface na nossa lista
                    interfaceCodes.Add(interfaceSyntax.NormalizeWhitespace().ToFullString());
                }
            }
        }

        // Junta tudo em uma única string separada por quebras de linha
        return string.Join("\n\n", interfaceCodes);
    }
}
