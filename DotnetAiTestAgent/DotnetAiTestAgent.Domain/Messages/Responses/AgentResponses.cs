using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.ValueObjects;

namespace DotnetAiTestAgent.Domain.Messages.Responses;

/// <summary>Mensagem base — todos os responses herdam dela.</summary>
public abstract record AgentResponse
{
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();
    public DateTime RespondedAt { get; init; } = DateTime.UtcNow;
}

public record ProjectDiscoveredResponse  (List<CSharpClassInfo> Classes, List<InterfaceInfo> Interfaces) : AgentResponse;
public record FakesGeneratedResponse     (List<string> GeneratedFakes) : AgentResponse;
public record TestsGeneratedResponse     (List<string> GeneratedTests) : AgentResponse;
public record CompileResultResponse      (bool Success, string Output) : AgentResponse;
public record TestsResultResponse        (bool AllPassed, string Output, List<LogicIssue> BugsFound) : AgentResponse;
public record CoverageResultResponse     (double Coverage, double BranchCoverage, List<CoverageGap> Gaps) : AgentResponse;
public record MutationResultResponse     (double Score) : AgentResponse;
public record LogicAnalysisResponse      (List<LogicIssue> Issues) : AgentResponse;
public record QualityAnalysisResponse    (List<QualityIssue> Issues) : AgentResponse;
public record ArchitectureReviewResponse (List<ArchitectureIssue> Issues) : AgentResponse;
public record ReportsGeneratedResponse   (List<string> ReportPaths) : AgentResponse;
