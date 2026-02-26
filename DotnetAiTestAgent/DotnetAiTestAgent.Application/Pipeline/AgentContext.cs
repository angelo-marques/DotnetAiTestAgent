using DotnetAiTestAgent.Domain.Entities;
using DotnetAiTestAgent.Domain.ValueObjects;

namespace DotnetAiTestAgent.Application.Pipeline;

/// <summary>
/// Estado de negócio compartilhado pelo pipeline.
///
/// SourcePath  → onde estão os arquivos .cs a analisar (o projeto real)
/// OutputPath  → onde os testes, fakes e relatórios serão gerados
///
/// Podem ser a mesma pasta (comportamento anterior) ou pastas separadas.
/// Exemplo:
///   SourcePath = /projetos/MinhaApi/src
///   OutputPath = /projetos/MinhaApi/tests-gerados
/// </summary>
public class AgentContext
{
    /// <summary>Pasta raiz do código-fonte a analisar.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Pasta onde testes, fakes e relatórios serão escritos.</summary>
    public string OutputPath { get; set; } = string.Empty;

    public PipelineOptions Options { get; set; } = new();
    public PipelineState State { get; set; } = new();

    // Descoberta
    public List<CSharpClassInfo> DiscoveredClasses { get; set; } = new();
    public List<InterfaceInfo> DiscoveredInterfaces { get; set; } = new();

    // Geração
    public List<string> GeneratedFakes { get; set; } = new();
    public List<string> GeneratedTests { get; set; } = new();

    // Cobertura
    public List<CoverageGap> CoverageGaps { get; set; } = new();
    public double CurrentCoverage { get; set; }
    public double MutationScore { get; set; }

    // Issues
    public List<LogicIssue> LogicIssues { get; set; } = new();
    public List<QualityIssue> QualityIssues { get; set; } = new();
    public List<ArchitectureIssue> ArchitectureIssues { get; set; } = new();
    public List<AgentIssue> PipelineErrors { get; set; } = new();
}

/// <summary>Opções configuráveis via CLI.</summary>
public class PipelineOptions
{
    public int CoverageThreshold { get; set; } = 80;
    public int MaxRetriesPerAgent { get; set; } = 3;
    public int ParallelWorkers { get; set; } = 4;
    public bool IncrementalMode { get; set; } = true;
}
