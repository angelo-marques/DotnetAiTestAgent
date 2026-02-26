using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.ValueObjects;

namespace DotnetAiTestAgent.Domain.Messages.Requests;

/// <summary>Mensagem base — todos os requests herdam dela.</summary>
public abstract record AgentRequest
{
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();
    public DateTime SentAt      { get; init; } = DateTime.UtcNow;
}

public record DiscoverProjectRequest   (string ProjectPath) : AgentRequest;
public record GenerateFakesRequest     (string ProjectPath, List<InterfaceInfo> Interfaces) : AgentRequest;
public record GenerateTestsRequest     (string ProjectPath, List<CSharpClassInfo> Classes, List<CoverageGap> Gaps) : AgentRequest;
public record CompileFixRequest        (string ProjectPath, string BuildOutput) : AgentRequest;
public record DebugTestsRequest        (string ProjectPath, string TestOutput) : AgentRequest;
public record ReviewCoverageRequest    (string ProjectPath, int Threshold) : AgentRequest;
public record RunMutationRequest       (string ProjectPath) : AgentRequest;
public record AnalyzeLogicRequest      (string ProjectPath, List<CSharpClassInfo> Classes) : AgentRequest;
public record AnalyzeQualityRequest    (string ProjectPath, List<CSharpClassInfo> Classes) : AgentRequest;
public record ReviewArchitectureRequest(string ProjectPath, List<CSharpClassInfo> Classes) : AgentRequest;
