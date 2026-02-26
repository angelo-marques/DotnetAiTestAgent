using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Application.Agents;


/// <summary>
/// Gera Fakes realistas para todas as interfaces do projeto.
/// NUNCA usa Moq ou NSubstitute — implementações com estado interno real (List, Dictionary).
/// Também gera FakeBuilders com Bogus para dados de teste realistas.
/// </summary>
public class FakeGeneratorAgent : BaseAgent<GenerateFakesRequest, FakesGeneratedResponse>
{
    private readonly FileSystemPlugin _fileSystem;

    public override string Name => "FakeGeneratorAgent";

    public FakeGeneratorAgent(IChatClient chat, FileSystemPlugin fileSystem, ILogger<FakeGeneratorAgent> logger)
        : base(chat, logger) => _fileSystem = fileSystem;

    public override async Task<FakesGeneratedResponse> HandleAsync(
        GenerateFakesRequest request, AgentThread thread, CancellationToken ct = default)
    {
        var generated = new List<string>();

        foreach (var iface in request.Interfaces)
        {
            Logger.LogInformation("[{A}] Gerando Fake para {I}", Name, iface.InterfaceName);

            // Thread compartilhada entre interfaces: o modelo mantém consistência
            // de namespace e estilo entre os fakes gerados na mesma execução
            var response = await CompleteAsync(
                system: SystemPrompt,
                user: $"Interface:\n```csharp\n{iface.SourceCode}\n```",
                thread, ct);

            var parts = response.Split("===SEPARATOR===");
            var fakeName = $"Fake{iface.InterfaceName.TrimStart('I')}.cs";

            if (parts.Length >= 1)
            {
                await _fileSystem.WriteFakeFileAsync(fakeName, parts[0].Trim());
                generated.Add(fakeName);
            }

            if (parts.Length >= 2)
            {
                var builderName = $"FakeBuilders/{iface.InterfaceName.TrimStart('I')}FakeBuilder.cs";
                await _fileSystem.WriteFakeFileAsync(builderName, parts[1].Trim());
            }
        }

        Logger.LogInformation("[{A}] {N} fakes gerados", Name, generated.Count);
        return new FakesGeneratedResponse(generated);
    }

    private const string SystemPrompt = """
        Você é especialista em testes .NET que cria Fakes realistas.

        REGRAS ABSOLUTAS:
        - NUNCA use Moq, NSubstitute ou qualquer mock framework
        - Implemente a interface com estado interno real usando List<T> ou Dictionary
        - Construtor: (IEnumerable<T>? seed = null) para dados iniciais opcionais
        - Implemente TODOS os métodos da interface com lógica real
        - Gere também um FakeBuilder usando a biblioteca Bogus para dados realistas
        - Separe os dois arquivos com exatamente: ===SEPARATOR===
        - Retorne APENAS código C# sem explicações, sem markdown
        """;
}
