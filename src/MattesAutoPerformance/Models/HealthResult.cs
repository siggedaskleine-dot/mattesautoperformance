namespace MattesAutoPerformance.Models;

public enum HealthStatus
{
    Running,
    Ok,
    Info,
    Warning,
    Problem,
}

/// <summary>Resultatet av en kontroll i hälsokontrollen.</summary>
public sealed class HealthResult
{
    public required string Title { get; init; }

    /// <summary>Tecken ur typsnittet Segoe Fluent Icons.</summary>
    public required string Icon { get; init; }

    public HealthStatus Status { get; init; }

    public string Summary { get; init; } = "";

    public List<string> Details { get; init; } = new();

    /// <summary>Vad som kommer att kunna fixas med ett klick i steg 3.</summary>
    public string? FixHint { get; init; }

    public bool HasDetails => Details.Count > 0;

    public bool HasFixHint => !string.IsNullOrEmpty(FixHint);

    public string StatusText => Status switch
    {
        HealthStatus.Running => "Kontrollerar…",
        HealthStatus.Ok => "Bra",
        HealthStatus.Info => "Info",
        HealthStatus.Warning => "Bör åtgärdas",
        HealthStatus.Problem => "Problem",
        _ => "",
    };

    public static HealthResult Running(string title, string icon) =>
        new() { Title = title, Icon = icon, Status = HealthStatus.Running, Summary = "Kontrollerar…" };
}
