namespace MattesAutoPerformance.Models;

/// <summary>Ett kort i översikten, t.ex. "Processor" eller "Grafikkort".</summary>
public sealed class HardwareSection
{
    public required string Title { get; init; }

    /// <summary>Tecken ur typsnittet Segoe Fluent Icons.</summary>
    public required string Icon { get; init; }

    /// <summary>Huvudrad med det viktigaste, t.ex. processorns namn.</summary>
    public string Headline { get; set; } = "";

    public List<InfoRow> Rows { get; } = new();

    /// <summary>Delkomponenter, t.ex. varje disk eller RAM-modul.</summary>
    public List<HardwareSection> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public HardwareSection Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Rows.Add(new InfoRow(label, value.Trim()));
        return this;
    }

    public HardwareSection AddUsage(string label, string value, double percent)
    {
        Rows.Add(new InfoRow(label, value, Math.Clamp(percent, 0, 100)));
        return this;
    }
}

/// <summary>En rad i ett kort. Percent är satt när raden ska visa en stapel.</summary>
public sealed record InfoRow(string Label, string Value, double? Percent = null)
{
    public bool HasBar => Percent.HasValue;

    /// <summary>Rött över 90 %, gult över 75 %, annars grönt.</summary>
    public string Level => Percent switch
    {
        > 90 => "High",
        > 75 => "Medium",
        _ => "Low",
    };
}
