using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using DotnetAiTestAgent.Domain.Enums;
using DotnetAiTestAgent.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DotnetAiTestAgent.Infrastructure.Plugins;

/// <summary>
/// Parseia o XML no formato Cobertura gerado pelo coverlet.
/// Extrai cobertura por linha e por branch, identificando
/// os gaps priorizados para retroalimentar o TestWriterAgent.
/// </summary>
public class CoverageParserPlugin
{
    private readonly ILogger<CoverageParserPlugin> _logger;

    public CoverageParserPlugin(ILogger<CoverageParserPlugin> logger) => _logger = logger;

    public string ParseCoverage(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return EmptyResult();

        try
        {
            var doc        = XDocument.Parse(xmlContent);
            var root       = doc.Root!;
            var lineRate   = ParseRate(root.Attribute("line-rate")?.Value);
            var branchRate = ParseRate(root.Attribute("branch-rate")?.Value);

            var gaps = doc.Descendants("class")
                .Select(cls => new
                {
                    ClassName  = cls.Attribute("name")?.Value ?? "",
                    FilePath   = cls.Attribute("filename")?.Value ?? "",
                    LineRate   = ParseRate(cls.Attribute("line-rate")?.Value),
                    Lines      = cls.Descendants("line")
                        .Where(l => l.Attribute("hits")?.Value == "0")
                        .Select(l => int.TryParse(l.Attribute("number")?.Value, out var n) ? n : 0)
                        .Where(n => n > 0)
                        .ToList()
                })
                .Where(c => c.LineRate < 0.8 && c.Lines.Any())
                .Select(c => new CoverageGap
                {
                    ClassName      = c.ClassName,
                    FilePath       = c.FilePath,
                    Coverage       = c.LineRate * 100,
                    UncoveredLines = c.Lines,
                    Priority       = c.LineRate < 0.4 ? IssueSeverity.High : IssueSeverity.Medium
                })
                .OrderBy(g => g.Coverage)
                .ToList();

            var result = new CoverageResult
            {
                AverageCoverage = lineRate * 100,
                BranchCoverage  = branchRate * 100,
                Gaps            = gaps
            };

            _logger.LogDebug("Cobertura: {L:F1}% linha | {B:F1}% branch | {G} gaps",
                result.AverageCoverage, result.BranchCoverage, gaps.Count);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parsear XML de cobertura");
            return EmptyResult();
        }
    }

    private static double ParseRate(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    private static string EmptyResult() =>
        JsonSerializer.Serialize(new CoverageResult
        {
            AverageCoverage = 0,
            BranchCoverage  = 0,
            Gaps            = new()
        });
}
