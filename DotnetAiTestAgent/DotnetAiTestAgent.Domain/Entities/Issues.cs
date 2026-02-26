using DotnetAiTestAgent.Domain.Enums;

namespace DotnetAiTestAgent.Domain.Entities;

/// <summary>Problema de lógica detectado no código: null risks, race conditions, dead code, etc.</summary>
public class LogicIssue
{
    public string FilePath              { get; set; } = string.Empty;
    public string ClassName             { get; set; } = string.Empty;
    public string MethodName            { get; set; } = string.Empty;
    public string IssueType             { get; set; } = string.Empty;
    public string Description           { get; set; } = string.Empty;
    public string Suggestion            { get; set; } = string.Empty;
    public int Line                     { get; set; }
    public IssueSeverity Severity       { get; set; }
    public int EstimatedFixMinutes      { get; set; }
}

/// <summary>Problema de qualidade: SOLID, code smells, complexidade ciclomática.</summary>
public class QualityIssue
{
    public string FilePath              { get; set; } = string.Empty;
    public string ClassName             { get; set; } = string.Empty;
    public string IssueType             { get; set; } = string.Empty;
    public string Description           { get; set; } = string.Empty;
    public string Suggestion            { get; set; } = string.Empty;
    public IssueSeverity Severity       { get; set; }
    public int CyclomaticComplexity     { get; set; }
    public int EstimatedFixMinutes      { get; set; }
}

/// <summary>Problema de arquitetura: dependências circulares, violações de camadas, acoplamento.</summary>
public class ArchitectureIssue
{
    public string IssueType             { get; set; } = string.Empty;
    public string Description           { get; set; } = string.Empty;
    public string FromComponent         { get; set; } = string.Empty;
    public string ToComponent           { get; set; } = string.Empty;
    public IssueSeverity Severity       { get; set; }
    public string Suggestion            { get; set; } = string.Empty;
}

/// <summary>Erro de compilação ou runtime identificado por um agente.</summary>
public class AgentIssue
{
    public string AgentName             { get; set; } = string.Empty;
    public IssueSeverity Severity       { get; set; }
    public string Message               { get; set; } = string.Empty;
    public string? StackTrace           { get; set; }
    public string? FilePath             { get; set; }
    public int? Line                    { get; set; }
}
