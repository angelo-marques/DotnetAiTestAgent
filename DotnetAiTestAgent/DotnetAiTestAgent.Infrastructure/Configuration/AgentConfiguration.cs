namespace DotnetAiTestAgent.Infrastructure.Configuration;


/// <summary>
/// Configuração completa lida do ai-test-agent.json.
/// Cada propriedade aqui corresponde a uma chave no JSON —
/// se não baterem, o valor fica com o default definido abaixo.
/// </summary>
public class AgentConfiguration
{
    public LlmConfig Llm { get; set; } = new();
    public PipelineConfig Pipeline { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public FeatureFlags Features { get; set; } = new();
}

public class LlmConfig
{
    public string Provider { get; set; } = "ollama";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public ModelNames Models { get; set; } = new();
}

/// <summary>
/// Modelo usado por cada agente.
/// Com Ollama local, o mesmo modelo pode ser usado para todos.
/// Com OpenAI/Azure é possível usar modelos diferentes por custo/velocidade.
/// </summary>
public class ModelNames
{
    public string TestWriter { get; set; } = "falcon3:7b";
    public string FakeGenerator { get; set; } = "falcon3:7b";
    public string CompileFix { get; set; } = "falcon3:7b";
    public string TestDebug { get; set; } = "falcon3:7b";
    public string LogicAnalysis { get; set; } = "falcon3:7b";
    public string QualityAnalysis { get; set; } = "falcon3:7b";
    public string ArchitectureReview { get; set; } = "falcon3:7b";
    public string ReportGenerator { get; set; } = "falcon3:7b";
}

public class PipelineConfig
{
    public int CoverageThreshold { get; set; } = 80;
    public int MutationThreshold { get; set; } = 60;
    public int MaxRetriesPerAgent { get; set; } = 3;
    public int ParallelWorkers { get; set; } = 2;   // conservador para 7b local
    public bool IncrementalMode { get; set; } = true;
}

public class OutputConfig
{
    public string TestsFolder { get; set; } = "tests";
    public string ReportsFolder { get; set; } = "ai-test-reports";
    public string FakesSubfolder { get; set; } = "Fakes";
}

public class FeatureFlags
{
    public bool MutationTesting { get; set; } = true;
    public bool ArchitectureReview { get; set; } = true;
    public bool GenerateFakes { get; set; } = true;
    public bool WatchMode { get; set; } = false;
    public bool UseMocks { get; set; } = false;
}