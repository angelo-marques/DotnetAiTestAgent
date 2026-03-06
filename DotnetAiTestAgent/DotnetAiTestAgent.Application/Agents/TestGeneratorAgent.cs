using DotnetAiTestAgent.Infrastructure.Services;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;

namespace DotnetAiTestAgent.Application.Agents;
public class TestGeneratorAgent
{
    private readonly IChatClient _chatClient;
    private readonly RoslynValidator _validator;

    public TestGeneratorAgent(IChatClient chatClient, RoslynValidator validator)
    {
        _chatClient = chatClient;
        _validator = validator;
    }

    public async Task<string> GenerateAndValidateTestsAsync(
        string targetClassCode,
        string targetInterfacesCode,
        IEnumerable<MetadataReference> projectReferences)
    {
        // 1. O Few-Shot Prompt: Ensinando a IA pelo exemplo
        var systemPrompt = @"
Você é um Engenheiro de Software Sênior especialista em .NET 10 e XUnit.
Sua tarefa é criar testes de unidade para a classe fornecida.
REGRA ABSOLUTA: NÃO utilize frameworks de Mocking (Moq, NSubstitute, etc). 
Você DEVE criar classes 'Fake' manuais que implementam as interfaces necessárias.

EXEMPLO DE COMO CRIAR UM FAKE:
Se a interface for:
public interface IEmailService { void Send(string msg); bool IsValid(string email); }

Seu Fake DEVE ser:
public class FakeEmailService : IEmailService 
{
    public bool SendWasCalled { get; private set; }
    public void Send(string msg) { SendWasCalled = true; }
    public bool IsValid(string email) => throw new NotImplementedException(); // Lança exceção se não for usado no teste
}

Retorne APENAS o código C# compilável, incluindo os usings. Não use blocos de markdown (```csharp) na resposta, apenas o texto do código.";

        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"Interfaces Disponíveis:\n{targetInterfacesCode}\n\nClasse Alvo:\n{targetClassCode}")
        };

        int maxAttempts = 3;

        // 2. O Feedback Loop em Memória
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Console.WriteLine($"\nGerando código (Tentativa {attempt}/{maxAttempts})...");
            var response = await _chatClient.GetResponseAsync(chatHistory);
            var generatedCode = response.Text?.Replace("```csharp", "").Replace("```", "").Trim();

            Console.WriteLine("Validando sintaxe em memória com Roslyn...");
            var (isValid, errors) = _validator.ValidateCode(generatedCode!, projectReferences);

            if (isValid)
            {
                Console.WriteLine("✓ Código validado com sucesso!");
                return generatedCode!;
            }

            Console.WriteLine($"✗ Falha na compilação. Alimentando a IA com os erros...");

            // Adiciona a tentativa falha e o erro do Roslyn no contexto para a IA consertar
            chatHistory.Add(new ChatMessage(ChatRole.Assistant, generatedCode));
            chatHistory.Add(new ChatMessage(ChatRole.User,
                $"O código gerado falhou na compilação com os seguintes erros:\n{errors}\n" +
                "Por favor, corrija APENAS os erros apontados e retorne o código completo atualizado."));
        }

        throw new Exception("A IA não conseguiu gerar um código compilável após o limite de tentativas.");
    }
}