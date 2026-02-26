using DotnetAiTestAgent.Domain.Enums;

namespace DotnetAiTestAgent.Domain.ValueObjects;

/// <summary>Lacuna de cobertura identificada em uma classe ou método específico.</summary>
public class CoverageGap
{
    public string FilePath              { get; set; } = string.Empty;
    public string ClassName             { get; set; } = string.Empty;
    public string MethodName            { get; set; } = string.Empty;
    public double Coverage              { get; set; }
    public List<int> UncoveredLines     { get; set; } = new();
    public List<string> UncoveredBranches { get; set; } = new();
    public IssueSeverity Priority       { get; set; }
}

/// <summary>Resultado completo da análise de cobertura do coverlet.</summary>
public class CoverageResult
{
    public double AverageCoverage       { get; set; }
    public double BranchCoverage        { get; set; }
    public List<CoverageGap> Gaps       { get; set; } = new();
}

/// <summary>Estado persistido entre execuções do pipeline (modo incremental).</summary>
public class PipelineState
{
    public DateTime LastRunAt           { get; set; }
    public List<string> ProcessedFiles  { get; set; } = new();
    public Dictionary<string, double> FileCoverage { get; set; } = new();
    public double MutationScore         { get; set; }
}
