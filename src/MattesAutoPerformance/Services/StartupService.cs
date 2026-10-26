using System.IO;
using Microsoft.Win32;

namespace MattesAutoPerformance.Services;

/// <summary>En app som startar med Windows.</summary>
public sealed class StartupEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Source { get; init; }
    public bool Enabled { get; set; }

    internal RegistryKey Root { get; init; } = null!;
    internal string ApprovedKeyPath { get; init; } = "";
    internal string ApprovedValueName { get; init; } = "";
}

/// <summary>
/// Läser och ändrar autostartappar på samma sätt som Aktivitetshanteraren: appen tas inte bort,
/// utan markeras som avstängd under StartupApproved. Det går därför alltid att slå på den igen.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> GetAll()
    {
        var entries = new List<StartupEntry>();
        ReadRunKey(entries, Registry.CurrentUser, RunKey, "Run", "Din användare");
        ReadRunKey(entries, Registry.LocalMachine, RunKey, "Run", "Alla användare");
        ReadRunKey(entries, Registry.LocalMachine, Run32Key, "Run32", "Alla användare (32-bitars)");
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser,
            "Autostartmappen");
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine,
            "Autostartmappen (alla)");
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        using var key = entry.Root.CreateSubKey(entry.ApprovedKeyPath, writable: true);
        // 12 byte: första byten 02 = på, 03 = av. Resten är tidpunkten då appen stängdes av.
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(entry.ApprovedValueName, data, RegistryValueKind.Binary);
        entry.Enabled = enabled;
    }

    private static void ReadRunKey(List<StartupEntry> entries, RegistryKey root, string runKey, string approvedName,
        string source)
    {
        using var run = root.OpenSubKey(runKey);
        if (run is null) return;
        using var approved = root.OpenSubKey(ApprovedKey + approvedName);
        foreach (var name in run.GetValueNames().Where(n => n != ""))
        {
            entries.Add(new StartupEntry
            {
                Name = name,
                Command = run.GetValue(name)?.ToString() ?? "",
                Source = source,
                Enabled = IsApproved(approved, name),
                Root = root,
                ApprovedKeyPath = ApprovedKey + approvedName,
                ApprovedValueName = name,
            });
        }
    }

    private static void ReadFolder(List<StartupEntry> entries, string folder, RegistryKey root, string source)
    {
        if (!Directory.Exists(folder)) return;
        using var approved = root.OpenSubKey(ApprovedKey + "StartupFolder");
        foreach (var file in Directory.GetFiles(folder)
                     .Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(file);
            entries.Add(new StartupEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                Source = source,
                Enabled = IsApproved(approved, fileName),
                Root = root,
                ApprovedKeyPath = ApprovedKey + "StartupFolder",
                ApprovedValueName = fileName,
            });
        }
    }

    /// <summary>Första byten är jämn när appen är påslagen och udda när den är avstängd.</summary>
    private static bool IsApproved(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } data || data[0] % 2 == 0;
}
