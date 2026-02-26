# 🤖 DotnetAiTestAgent

> **Um exército de agentes de IA especializados que escreve, corrige, analisa e documenta testes para o seu projeto .NET — do zero, em minutos, sem você tocar em uma linha.**

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Microsoft.Extensions.AI](https://img.shields.io/badge/Microsoft.Extensions.AI-9.3.0_preview-0078D4?logo=microsoft)](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/)
[![Semantic Kernel](https://img.shields.io/badge/Semantic_Kernel-1.30.0-5C2D91?logo=microsoft)](https://github.com/microsoft/semantic-kernel)
[![.NET Aspire](https://img.shields.io/badge/.NET_Aspire-observability-512BD4?logo=dotnet)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![Ollama](https://img.shields.io/badge/Ollama-local%20LLM-black?logo=ollama)](https://ollama.ai)
[![OpenAI](https://img.shields.io/badge/OpenAI-compatible-412991?logo=openai)](https://openai.com)
[![Azure OpenAI](https://img.shields.io/badge/Azure-OpenAI-0078D4?logo=microsoftazure)](https://azure.microsoft.com/en-us/products/ai-services/openai-service)
[![xUnit](https://img.shields.io/badge/xUnit-tests-green)](https://xunit.net)
[![Stryker](https://img.shields.io/badge/Stryker.NET-mutation-red)](https://stryker-mutator.io)

---

## O que é este projeto?

**DotnetAiTestAgent** é uma ferramenta de linha de comando que aponta para a pasta do seu projeto .NET e, de forma completamente autônoma, executa um pipeline de **11 agentes de IA especializados** que trabalham em sequência para:

1. Entender a estrutura do código via **análise sintática com Roslyn**
2. Gerar **Fakes realistas** das suas interfaces — com estado interno real, sem Moq
3. Escrever **testes xUnit completos** com padrão AAA para cada classe pública
4. **Compilar e corrigir automaticamente** erros de compilação
5. Executar os testes e **debugar falhas**, distinguindo bug no teste vs. bug na aplicação
6. Medir e **iterar sobre a cobertura** até atingir o threshold configurado
7. Rodar **mutation testing com Stryker.NET** para validar a qualidade real
8. Detectar **problemas de lógica, qualidade e arquitetura** no código-fonte
9. Gerar **7 relatórios** em Markdown e JSON

Tudo isso com **um único comando**.

---

## Por que isso importa?

Escrever testes de unidade é a tarefa mais importante e mais negligenciada no desenvolvimento de software. Times atrasam deploys, acumulam dívida técnica e entregam bugs em produção — não por falta de conhecimento, mas por **falta de tempo**.

**DotnetAiTestAgent resolve isso.**

Você configura uma vez e passa a ter cobertura de testes gerada, revisada e documentada de forma contínua — seja no fluxo local ou integrado a um pipeline de CI/CD.

---

## 🧠 Microsoft.Extensions.AI — o novo padrão da Microsoft para IA em .NET

Este projeto é uma das primeiras implementações open-source a adotar integralmente o **`Microsoft.Extensions.AI`**, o pacote lançado pela Microsoft em 2024 que define o padrão oficial de integração de LLMs em aplicações .NET.

### O que é o Microsoft.Extensions.AI?

É uma camada de abstração sobre qualquer provedor de LLM, equivalente ao que `Microsoft.Extensions.Logging` fez pelo logging no .NET — uma interface única que funciona com qualquer implementação por baixo.

```
Microsoft.Extensions.AI.Abstractions   ← contratos (IChatClient, ChatMessage, ChatRole)
Microsoft.Extensions.AI                ← middleware pipeline (UseLogging, UseOpenTelemetry)
Microsoft.Extensions.AI.OpenAI         ← adapter para OpenAI e Azure OpenAI
Microsoft.Extensions.AI.Ollama         ← adapter para Ollama local
```

### Como está estruturado no projeto

Todo agente recebe um `IChatClient` — ele não sabe nem se importa se o modelo é o `falcon3:7b` local via Ollama, o `gpt-4o` via OpenAI ou um deployment via Azure. **O código dos agentes nunca muda quando você troca de provedor.**

```csharp
// BaseAgent.cs — todos os 11 agentes herdam disso
public abstract class BaseAgent<TRequest, TResponse>
{
    protected readonly IChatClient ChatClient;  // ← abstração, não implementação

    protected async Task<string> CompleteAsync(string system, string user, AgentThread thread, ...)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            // injeta histórico da thread automaticamente
            ...thread.History,
            new(ChatRole.User, user)
        };

        var response = await ChatClient.CompleteAsync(messages, cancellationToken: ct);
        return response.Message.Text ?? string.Empty;
    }
}
```

### O pipeline de middleware do IChatClient

O `Microsoft.Extensions.AI` funciona com um **builder pattern de middleware**, similar ao pipeline de middleware do ASP.NET Core. Cada chamada ao LLM passa pelos middlewares em cadeia:

```csharp
// ServiceCollectionExtensions.cs
IChatClient baseClient = provider switch
{
    "ollama" => new OllamaChatClient(new Uri(config.Llm.BaseUrl), modelId),
    "openai" => new OpenAIChatClient(openAiClient, modelId),
    "azure"  => new OpenAIChatClient(azureClient, modelId),
};

return baseClient
    .AsBuilder()
    .UseLogging(loggerFactory)          // loga todas as chamadas automaticamente
    .UseOpenTelemetry(loggerFactory,    // emite spans para distributed tracing
        "dotnet-ai-test-agent",
        b => b.EnableSensitiveData = false)
    .Build();                           // retorna IChatClient com middleware aplicado
```

Resultado: **log estruturado e trace de observabilidade** em toda chamada ao LLM — sem instrumentação manual nos agentes.

### Factory de IChatClient por agente

Cada agente pode usar um modelo diferente, configurado no `ai-test-agent.json`. A `IChatClientFactory` cria instâncias sob demanda:

```csharp
// Cada agente recebe o modelo configurado para ele
runtime
    .Register(new TestWriterAgent(factory.Create(m.TestWriter), ...))      // ex: falcon3:7b
    .Register(new LogicAnalysisAgent(factory.Create(m.LogicAnalysis), ...)) // ex: falcon3:7b
    .Register(new ReportGeneratorAgent(factory.Create(m.ReportGenerator), ...));
```

---

## 📊 .NET Aspire — Observabilidade de produção

O projeto integra com o ecossistema do **.NET Aspire** para observabilidade completa das chamadas aos agentes e ao LLM.

### O que o Aspire adiciona aqui

O `.NET Aspire` define padrões de observabilidade para aplicações .NET distribuídas. Mesmo rodando como CLI, o DotnetAiTestAgent emite:

- **Distributed Traces** via OpenTelemetry — cada chamada ao LLM gera um span com duração, modelo usado e prompt (sem dados sensíveis)
- **Logs estruturados** via Serilog integrado ao `ILogger<T>` do .NET — todos os agentes logam com contexto (CorrelationId, nome do agente, tentativa)
- **Métricas** de tempo de execução por etapa do pipeline

### Configuração OpenTelemetry

```csharp
services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddSource("Microsoft.Extensions.AI")      // traces das chamadas ao LLM
        .AddSource("dotnet-ai-test-agent")          // traces do pipeline de agentes
        .AddConsoleExporter());                     // saída local; substituível por Jaeger, OTLP, etc.
```

### Integrar com o Dashboard do Aspire

```bash
# Subir o Dashboard do Aspire localmente
dotnet tool install --global aspirate

# A ferramenta emite traces compatíveis — basta apontar o OTLP exporter
# para o endpoint do dashboard
```

Substitua o `AddConsoleExporter()` pelo exporter OTLP e todos os traces aparecem visualmente no dashboard do Aspire com correlação entre agentes, tempos de resposta e chamadas ao modelo.

---

## 🔁 Memória e Reforço nos Agentes de IA

Este é o diferencial técnico central do projeto. Os agentes não fazem chamadas isoladas ao LLM — eles têm **memória de contexto** e usam **reforço por tentativa** para melhorar seus resultados progressivamente.

### AgentThread — memória por execução

Cada execução do pipeline recebe um `CorrelationId` único. O `AgentRuntime` cria uma `AgentThread` por `CorrelationId` e a mantém viva durante toda a execução:

```csharp
public class AgentThread
{
    private readonly List<ChatMessage>           _history = new();
    private readonly Dictionary<string, object>  _state   = new();

    public int RetryCount { get; set; }

    public void AddMessage(ChatMessage msg)  => _history.Add(msg);
    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();
    public void SetState<T>(string key, T v) => _state[key] = v;
    public T?   GetState<T>(string key)      => ...;
}
```

Toda resposta do LLM é adicionada ao histórico. Na próxima chamada do **mesmo agente**, o histórico completo é enviado — o modelo vê o que já tentou e o resultado.

### Reforço por retry com contexto acumulado

Quando um agente falha (teste não compila, cobertura insuficiente), o `AgentRuntime` incrementa o `RetryCount` na thread e reenvia a mensagem. O agente **vê o histórico de tentativas anteriores** e o número da tentativa atual:

```csharp
// CompileFixAgent — aprende com erros anteriores na mesma thread
public override async Task<CompileResultResponse> HandleAsync(
    CompileFixRequest request, AgentThread thread, ...)
{
    Logger.LogWarning("Corrigindo erros (tentativa {R})", thread.RetryCount + 1);

    // O CompleteAsync injeta automaticamente o histórico de tentativas anteriores
    // O modelo vê: tentativa 1 → erro X → correção Y → ainda com erro Z → nova correção
    var fixesJson = await CompleteAsync(SystemPrompt,
        $"ERROS DE COMPILAÇÃO:\n{request.BuildOutput}", thread, ct);
}
```

O efeito é um **ciclo de aprendizado em tempo de execução**: cada tentativa mal sucedida enriquece o contexto e aumenta a probabilidade de acerto na tentativa seguinte — sem fine-tuning, sem treinamento extra.

### Memória de estado entre agentes

Os agentes podem compartilhar estado estruturado via `AgentThread.SetState` / `GetState`. O `OrchestratorAgent`, por exemplo, armazena as classes e interfaces descobertas na thread:

```csharp
// OrchestratorAgent
thread.SetState("classes",    classes);
thread.SetState("interfaces", interfaces);

// Agentes subsequentes podem recuperar sem nova chamada ao Roslyn
var classes = thread.GetState<List<CSharpClassInfo>>("classes");
```

### Loop de cobertura com retroalimentação (feedback loop)

O pipeline implementa um **ciclo de retroalimentação** entre o `CoverageReviewAgent` e o `TestWriterAgent`:

```
TestWriterAgent
      ↓ escreve testes
      ↓ compila e executa
CoverageReviewAgent
      ↓ analisa gaps (classes com < threshold%)
      ↓ prioriza por severidade
TestWriterAgent  ← recebe os gaps como contexto
      ↓ complementa testes especificamente para os gaps
      ↓ re-executa cobertura
      ↑ repete até threshold atingido ou max_retries esgotado
```

Cada ciclo passa os `CoverageGap` (classe, método, linhas não cobertas) como contexto para o TestWriterAgent. O modelo não está gerando testes genéricos — está gerando testes **especificamente para cobrir as lacunas identificadas**.

### Janela de contexto por agente (prompt especializado)

Cada agente tem um `SystemPrompt` especializado que define sua "personalidade" e restrições. Isso garante que o mesmo modelo gere outputs completamente diferentes por agente — o `FakeGeneratorAgent` nunca usa Moq, o `LogicAnalysisAgent` foca em bugs lógicos e não em estilo:

```
OrchestratorAgent      → "Você é um analisador de código C# especializado em Roslyn..."
FakeGeneratorAgent     → "NUNCA use Moq, NSubstitute. Implemente com List<T>, Dictionary..."
TestWriterAgent        → "Padrão AAA obrigatório. Arrange/Act/Assert em blocos separados..."
CompileFixAgent        → "Corrija SOMENTE a sintaxe. NUNCA altere a lógica dos testes..."
LogicAnalysisAgent     → "Detecte: null risks, race conditions, dead code, missing Dispose..."
ArchitectureReviewAgent → "Analise grafo de dependências. Detecte: dependências circulares..."
```

---

## 🏛️ Padrões de Projeto Aplicados

### Agent Pattern (Microsoft Agent Framework)

O núcleo do projeto implementa o **Agent Pattern** seguindo as convenções do `Microsoft.SemanticKernel.Agents.Core`:

- **`IAgent<TRequest, TResponse>`** — contrato tipado por mensagem. Cada agente é um handler especializado.
- **`IAgentRuntime`** — roteador central que desacopla quem envia de quem processa.
- **`AgentThread`** — contexto de conversa isolado por execução (CorrelationId).
- **Mensagens imutáveis** — `record` C# para requests e responses garante imutabilidade.

```
Produtor → IAgentRuntime.SendAsync<TReq, TRes>() → Agente correto
                     ↑
            Dicionário Type → Handler (O(1), sem switch)
```

### Pipeline Pattern

O `AgentPipeline` orquestra as 10 etapas em sequência com passagem de estado via `AgentContext`. Cada etapa lê e escreve no contexto compartilhado:

```csharp
// AgentPipeline.cs — cada etapa é um método privado isolado
await StepDiscoverAsync(context, id, ct);
await StepGenerateFakesAsync(context, id, ct);
await StepGenerateTestsAsync(context, id, gaps, ct);
await StepCompileAsync(context, id, maxRetries, ct);
await StepDebugTestsAsync(context, id, maxRetries, ct);
await StepCoverageLoopAsync(context, id, options, ct);
await StepMutationAsync(context, id, ct);
await StepLogicAnalysisAsync(context, id, ct);
await StepQualityAndArchitectureAsync(context, id, ct);
await StepGenerateReportsAsync(context, id, ct);
```

### Chain of Responsibility

O `AgentRuntime` implementa Chain of Responsibility via dicionário `Type → Handler`. Registrar um novo agente não modifica nenhuma classe existente:

```csharp
_handlers[typeof(TReq)] = async (req, thread, ct) =>
    await agent.HandleAsync((TReq)req, thread, ct);
```

### Factory Method

A `IChatClientFactory` / `OllamaOrRemoteChatClientFactory` encapsula a criação de `IChatClient` por provedor e modelo. O `ServiceCollectionExtensions` chama `factory.Create(modelId)` sem conhecer a implementação:

```csharp
public interface IChatClientFactory
{
    IChatClient Create(string modelId);
}
```

### Strategy

O provedor LLM é uma estratégia intercambiável em runtime via `--provider`. A mesma base de código suporta Ollama, OpenAI e Azure sem `if/else` nos agentes.

### Repository + State Pattern

O `PipelineStateManager` persiste e recupera o `PipelineState` em JSON no diretório do projeto. O estado inclui quais arquivos foram processados — base para o modo incremental.

### Observer (Watch Mode)

O `ProjectWatcher` usa `FileSystemWatcher` com debounce de 2 segundos para observar mudanças em `.cs`. Cada mudança dispara o pipeline em modo incremental — padrão Observer aplicado ao filesystem.

### Decorator (Middleware Pipeline)

O `IChatClient.AsBuilder().UseLogging().UseOpenTelemetry().Build()` é um Decorator em cadeia — cada middleware decora o cliente anterior sem modificar seu comportamento base.

### Clean Architecture (separação estrita de camadas)

```
Domain        → zero dependências externas — entidades puras C#
Application   → depende só do Domain — regras de negócio, contratos IAgent
Infrastructure → implementa contratos — detalhes técnicos (Roslyn, plugins)
CLI           → entry point — orquestra via DI, não contém lógica
```

---

## 🚀 Início rápido

### Pré-requisitos

```bash
# .NET 10 SDK
dotnet --version  # >= 10.0

# Ollama (para modelos locais)
# https://ollama.ai
ollama pull falcon3:7b

# Stryker.NET (mutation testing)
dotnet tool install --global dotnet-stryker
```

### Build

```bash
git clone https://github.com/angelo-marques/DotnetAiTestAgent
cd DotnetAiTestAgent
dotnet build
```

### Executar

```bash
# Pastas separadas (recomendado)
dotnet run --project src/DotnetAiTestAgent.Cli -- analyze \
  --source  ./MeuProjeto/src \
  --output  ./MeuProjeto/tests-gerados

# Ou instalar como ferramenta global
dotnet pack src/DotnetAiTestAgent.Cli -o ./dist
dotnet tool install --global --add-source ./dist dotnet-ai-test-agent
dotnet-ai-test-agent analyze --source ./MeuProjeto/src --output ./MeuProjeto/tests
```

---

## ⚙️ Comandos

### `analyze`

```bash
dotnet-ai-test-agent analyze \
  --source      ./MinhaApi/src \    # pasta do código-fonte (obrigatório)
  --output      ./MinhaApi/tests \  # pasta de saída (default = source)
  --threshold   80 \                # cobertura alvo em % (default: 80)
  --workers      2 \                # classes em paralelo (default: 2)
  --max-retries  3 \                # retries por agente (default: 3)
  --incremental  true \             # só arquivos alterados via git diff (default: true)
  --provider     ollama             # ollama | openai | azure (default: ollama)
```

### `watch`

```bash
dotnet-ai-test-agent watch \
  --source  ./MinhaApi/src \
  --output  ./MinhaApi/tests \
  --provider ollama
# Regenera automaticamente ao salvar qualquer .cs
```

---

## 🔌 Provedores LLM suportados

| Provedor | Configuração | Variáveis de ambiente |
|---|---|---|
| **Ollama** (padrão) | `"provider": "ollama"`, `"baseUrl": "http://localhost:11434"` | — |
| **OpenAI** | `"provider": "openai"` | `OPENAI_API_KEY` |
| **Azure OpenAI** | `"provider": "azure"`, `"baseUrl": "https://...openai.azure.com"` | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_KEY` |

---

## 🛠️ Configuração (`ai-test-agent.json`)

```json
{
  "llm": {
    "provider": "ollama",
    "baseUrl": "http://localhost:11434",
    "models": {
      "testWriter":         "falcon3:7b",
      "fakeGenerator":      "falcon3:7b",
      "compileFix":         "falcon3:7b",
      "testDebug":          "falcon3:7b",
      "logicAnalysis":      "falcon3:7b",
      "qualityAnalysis":    "falcon3:7b",
      "architectureReview": "falcon3:7b",
      "reportGenerator":    "falcon3:7b"
    }
  },
  "pipeline": {
    "coverageThreshold":  80,
    "mutationThreshold":  60,
    "maxRetriesPerAgent":  3,
    "parallelWorkers":     2,
    "incrementalMode":  true
  },
  "output": {
    "testsFolder":    "tests",
    "reportsFolder":  "ai-test-reports",
    "fakesSubfolder": "Fakes"
  },
  "features": {
    "mutationTesting":    true,
    "architectureReview": true,
    "generateFakes":      true
  }
}
```

---

## 🏗️ Estrutura do projeto

```
src/
├── DotnetAiTestAgent.Cli/              # Entry point (Console App / Web App)
│   ├── Commands/                       # AnalyzeCommand, WatchCommand
│   ├── DependencyInjection/            # ServiceCollectionExtensions + IChatClientFactory
│   ├── Program.cs                      # Bootstrap Serilog + WebApplication
│   └── ai-test-agent.json
│
└── DotnetAiTestAgent.Core/
    ├── Domain/                         # Entidades, enums, mensagens, value objects
    │   ├── Entities/                   # CSharpClassInfo, LogicIssue, QualityIssue...
    │   ├── Enums/                      # IssueSeverity, LlmProvider
    │   ├── Messages/                   # AgentRequests, AgentResponses (11 pares)
    │   └── ValueObjects/               # CoverageGap, CoverageResult, PipelineState
    │
    ├── Application/                    # Regras de negócio
    │   ├── Abstractions/               # IAgent<TReq,TRes>, IAgentRuntime, BaseAgent, AgentThread
    │   ├── Agents/                     # 11 agentes especializados (1 arquivo por agente)
    │   └── Pipeline/                   # AgentPipeline, AgentContext, PipelineStateManager, ProjectWatcher
    │
    └── Infrastructure/                 # Detalhes técnicos
        ├── Configuration/              # AgentConfiguration (mapeada do JSON)
        ├── Plugins/                    # FileSystem, Roslyn, DotnetRunner, Coverage, Stryker, Git
        ├── Reports/                    # ReportBuilder (7 relatórios)
        └── Runtime/                    # AgentRuntime (roteamento O(1) por Type)
```

### Os 11 agentes

| Agente | Responsabilidade |
|---|---|
| `OrchestratorAgent` | Descobre classes e interfaces via Roslyn (análise sintática) |
| `FakeGeneratorAgent` | Gera Fakes com estado real + FakeBuilders com Bogus |
| `TestWriterAgent` | Escreve testes xUnit AAA, paralelo por classe |
| `CompileFixAgent` | Corrige erros de compilação com histórico de tentativas |
| `TestDebugAgent` | Classifica falhas: bug no teste vs. bug na aplicação |
| `CoverageReviewAgent` | Analisa XML do coverlet, prioriza gaps por severidade |
| `MutationTestAgent` | Executa Stryker.NET para mutation score |
| `LogicAnalysisAgent` | Detecta null risks, race conditions, dead code, missing Dispose |
| `QualityAnalysisAgent` | Detecta violações SOLID e code smells |
| `ArchitectureReviewAgent` | Detecta dependências circulares e violações de camadas |
| `ReportGeneratorAgent` | Gera 7 relatórios Markdown + JSON |

---

## 📦 Stack tecnológica

| Pacote | Versão | Propósito |
|---|---|---|
| `Microsoft.Extensions.AI` | 9.3.0-preview | Abstração de LLM — `IChatClient` universal |
| `Microsoft.Extensions.AI.OpenAI` | 9.3.0-preview | Adapter OpenAI + Azure OpenAI |
| `Microsoft.Extensions.AI.Ollama` | 9.3.0-preview | Adapter Ollama local |
| `Microsoft.SemanticKernel` | 1.30.0 | Framework de agentes e plugins |
| `Microsoft.SemanticKernel.Agents.Core` | 1.30.0-alpha | Contratos do Agent Framework |
| `Microsoft.CodeAnalysis.CSharp` | 4.12.0 | Roslyn — análise sintática do código-fonte |
| `OpenTelemetry` | 1.11.1 | Distributed tracing — compatível com Aspire |
| `Serilog.AspNetCore` | 9.0.0 | Logging estruturado |
| `System.CommandLine` | 2.0.3 | CLI parser |
| `Bogus` | 35.6.1 | Dados fake realistas nos FakeBuilders |
| `Polly` | 8.5.1 | Resiliência e retry com backoff exponencial |
| `Azure.AI.OpenAI` | 2.1.0 | Cliente Azure OpenAI |

---

## 🗺️ Roadmap

- [ ] Integração com GitHub Actions (action oficial)
- [ ] Dashboard web com histórico de execuções via .NET Aspire
- [ ] Suporte a projetos F#
- [ ] Geração de testes para endpoints de API (controllers)
- [ ] Suporte a NUnit e MSTest além de xUnit
- [ ] Plugin para Visual Studio e VS Code
- [ ] Análise de segurança (OWASP Top 10 para código .NET)
- [ ] Exportação de relatórios para Azure DevOps e GitHub

---

## 🤝 Contribuindo

Pull requests são bem-vindos. Para mudanças grandes, abra uma issue primeiro.

1. Fork o projeto
2. Crie sua branch (`git checkout -b feature/nova-funcionalidade`)
3. Commit (`git commit -m 'feat: adiciona nova funcionalidade'`)
4. Push (`git push origin feature/nova-funcionalidade`)
5. Abra um Pull Request

---

## 📄 Licença

Distribuído sob a licença GPL-3.0. Veja [`LICENSE`](LICENSE) para mais informações.

---

<div align="center">

**Feito com ☕ e muito `await` no Brasil**

[⭐ Dê uma estrela se esse projeto te ajudou!](https://github.com/angelo-marques/DotnetAiTestAgent)

</div>
