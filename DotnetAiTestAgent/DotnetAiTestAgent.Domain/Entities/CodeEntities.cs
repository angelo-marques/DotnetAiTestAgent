namespace DotnetAiTestAgent.Domain.Entities;

/// <summary>Representa uma classe C# pública descoberta pelo Roslyn.</summary>
public class CSharpClassInfo
{
    public string FilePath            { get; set; } = string.Empty;
    public string ClassName           { get; set; } = string.Empty;
    public string Namespace           { get; set; } = string.Empty;
    public string SourceCode          { get; set; } = string.Empty;
    public List<string> Dependencies  { get; set; } = new();
    public List<MethodInfo> PublicMethods { get; set; } = new();
    public int CyclomaticComplexity   { get; set; }
    public double TestabilityScore    { get; set; }
}

/// <summary>Representa uma interface C# pública descoberta pelo Roslyn.</summary>
public class InterfaceInfo
{
    public string InterfaceName       { get; set; } = string.Empty;
    public string Namespace           { get; set; } = string.Empty;
    public string FilePath            { get; set; } = string.Empty;
    public string SourceCode          { get; set; } = string.Empty;
    public List<MethodInfo> Methods   { get; set; } = new();
}

/// <summary>Representa um método público de uma classe ou interface.</summary>
public class MethodInfo
{
    public string Name                { get; set; } = string.Empty;
    public string ReturnType          { get; set; } = string.Empty;
    public List<string> Parameters    { get; set; } = new();
    public bool IsAsync               { get; set; }
    public int CyclomaticComplexity   { get; set; }
}
