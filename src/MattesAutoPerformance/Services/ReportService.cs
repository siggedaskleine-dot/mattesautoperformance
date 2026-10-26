using System.Text;
using MattesAutoPerformance.Models;

namespace MattesAutoPerformance.Services;

/// <summary>Skriver översikten och hälsokontrollen som en textrapport.</summary>
public static class ReportService
{
    public static string Build(IEnumerable<HardwareSection> hardware, IEnumerable<HealthResult> health)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mattes AutoPerformance – rapport");
        sb.AppendLine($"Skapad {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var section in hardware)
        {
            sb.AppendLine($"== {section.Title} ==");
            if (section.Headline != "") sb.AppendLine(section.Headline);
            AppendRows(sb, section, "  ");
            foreach (var item in section.Items)
            {
                sb.AppendLine($"  {item.Title}");
                AppendRows(sb, item, "    ");
            }
            sb.AppendLine();
        }

        var results = health.Where(r => r.Status != HealthStatus.Running).ToList();
        if (results.Count > 0)
        {
            sb.AppendLine("== Hälsokontroll ==");
            foreach (var r in results)
            {
                sb.AppendLine($"[{r.StatusText}] {r.Title}: {r.Summary}");
                foreach (var d in r.Details) sb.AppendLine("    • " + d);
            }
        }

        return sb.ToString();
    }

    private static void AppendRows(StringBuilder sb, HardwareSection section, string indent)
    {
        foreach (var row in section.Rows) sb.AppendLine($"{indent}{row.Label}: {row.Value}");
    }
}
