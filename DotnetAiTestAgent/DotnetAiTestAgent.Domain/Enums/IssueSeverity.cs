namespace DotnetAiTestAgent.Domain.Enums;

public enum IssueSeverity { Info, Low, Medium, High, Critical }

public enum IssueCategory { Logic, Quality, Architecture, Coverage, Compilation, Runtime }

public enum LlmProvider { Ollama, OpenAI, Azure }
