using DotnetAiTestAgent.Application.Abstractions;
using DotnetAiTestAgent.Application.Messages.Requests;
using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.Messages.Responses;
using DotnetAiTestAgent.Infrastructure.Configuration;
using DotnetAiTestAgent.Infrastructure.Plugins;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DotnetAiTestAgent.Application.Agents;
/// <summary>
/// Descobre a estrutura do projeto via Roslyn (análise sintática).
/// Extrai todas as classes e interfaces públicas sem abrir MSBuildWorkspace.
/// </summary>
public class OrchestratorAgent : BaseAgent<DiscoverProjectRequest, ProjectDiscoveredResponse>
{
    private readonly RoslynPlugin _roslyn;

    public override string Name => "OrchestratorAgent";

    public OrchestratorAgent(IChatClient chat, PromptRepository prompts, RoslynPlugin roslyn, ILogger<OrchestratorAgent> logger)
        : base(chat, prompts, logger) => _roslyn = roslyn;

    public override async Task<ProjectDiscoveredResponse> HandleAsync(
        DiscoverProjectRequest request, AgentThread thread, CancellationToken ct = default)
    {
        Logger.LogInformation("[{A}] Varrendo projeto em {P}", Name, request.ProjectPath);

        var classesJson = await _roslyn.ExtractPublicClassesAsync(request.ProjectPath);
        var interfacesJson = await _roslyn.ExtractInterfacesAsync(request.ProjectPath);

        var classes = JsonSerializer.Deserialize<List<CSharpClassInfo>>(classesJson) ?? new();
        var interfaces = JsonSerializer.Deserialize<List<InterfaceInfo>>(interfacesJson) ?? new();

        // Persiste no estado da thread — agentes subsequentes podem consultar
        thread.SetState("classes", classes);
        thread.SetState("interfaces", interfaces);

        Logger.LogInformation("[{A}] {C} classes | {I} interfaces descobertas",
            Name, classes.Count, interfaces.Count);

        return new ProjectDiscoveredResponse(classes, interfaces);
    }
}