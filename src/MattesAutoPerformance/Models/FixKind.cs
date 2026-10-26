namespace MattesAutoPerformance.Models;

/// <summary>Åtgärderna som kan köras med ett klick.</summary>
public enum FixKind
{
    InstallUpdates,
    RepairDrivers,
    RepairSystemFiles,
    CleanTempFiles,
    ManageStartup,
    HighPerformancePower,
    Restart,
}

public static class FixKindExtensions
{
    /// <summary>Åtgärder som "Fixa allt" kör. Övriga kräver ett eget val av användaren.</summary>
    public static bool IncludedInFixAll(this FixKind kind) =>
        kind is FixKind.InstallUpdates or FixKind.RepairDrivers or FixKind.RepairSystemFiles or FixKind.CleanTempFiles;

    /// <summary>Åtgärder som ändrar systemet och därför föregås av en återställningspunkt.</summary>
    public static bool NeedsRestorePoint(this FixKind kind) =>
        kind is FixKind.InstallUpdates or FixKind.RepairDrivers or FixKind.RepairSystemFiles;
}
