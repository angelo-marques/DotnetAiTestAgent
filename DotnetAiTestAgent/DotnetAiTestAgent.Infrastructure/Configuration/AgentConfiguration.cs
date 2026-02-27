namespace DotnetAiTestAgent.Infrastructure.Configuration;

/// <summary>
/// Configuração completa lida do ai-test-agent.json.
/// Cada propriedade aqui corresponde a uma chave no JSON —
/// se não baterem, o valor fica com o default definido abaixo.
/// </summary>
public class AgentConfiguration
{
    public PathsConfig Paths { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
    public PipelineConfig Pipeline { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public FeatureFlags Features { get; set; } = new();
}

/// <summary>
/// Caminhos de origem e destino configurados no JSON.
/// Servem como fallback quando --source / --output não são passados na CLI.
///
/// Prioridade de resolução:
///   1. Argumento CLI  (--source, --output)       ← maior prioridade
///   2. Variável de ambiente (AITA_SOURCE, AITA_OUTPUT)
///   3. ai-test-agent.json → seção "paths"        ← fallback padrão
///
/// Suporta caminhos relativos — serão resolvidos a partir do diretório
/// onde o ai-test-agent.json está localizado (Directory.GetCurrentDirectory()).
///
/// Exemplos válidos no JSON:
///   "sourcePath": "C:\\Projetos\\MinhaApi\\src"        (absoluto Windows)
///   "sourcePath": "/home/user/projetos/MinhaApi/src"  (absoluto Linux/Mac)
///   "sourcePath": "../MinhaApi/src"                   (relativo ao cwd)
///   "sourcePath": ""                                  (não configurado — CLI obrigatória)
/// </summary>
public class PathsConfig
{
    /// <summary>
    /// Pasta raiz do código-fonte a ser analisado (onde estão os .cs).
    /// Equivalente ao --source da CLI.
    /// </summary>
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// Pasta de destino onde testes, fakes e relatórios serão escritos.
    /// Equivalente ao --output da CLI.
    /// Se vazio, usa o mesmo valor de SourcePath.
    /// </summary>
    public string OutputPath { get; set; } = "";
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
    public int ParallelWorkers { get; set; } = 2;
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