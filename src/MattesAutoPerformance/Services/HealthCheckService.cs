using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MattesAutoPerformance.Models;

namespace MattesAutoPerformance.Services;

/// <summary>
/// Letar efter problem på datorn. Allt här är skrivskyddat; åtgärderna kommer i steg 3.
/// </summary>
public sealed class HealthCheckService
{
    public sealed record Check(string Title, string Icon, Func<string, string, HealthResult> Run);

    public IReadOnlyList<Check> Checks { get; } =
    [
        new("Enheter och drivrutiner", "", CheckDevices),
        new("Windows Update och drivrutinsuppdateringar", "", CheckUpdates),
        new("Autostartappar", "", CheckStartupApps),
        new("Systemfiler", "", CheckSystemFiles),
        new("Diskar och lagringsutrymme", "", CheckDisks),
        new("Väntande omstart", "", CheckPendingRestart),
    ];

    public static HealthResult RunSafe(Check check)
    {
        try
        {
            return check.Run(check.Title, check.Icon);
        }
        catch (Exception ex)
        {
            return new HealthResult
            {
                Title = check.Title,
                Icon = check.Icon,
                Status = HealthStatus.Info,
                Summary = "Kontrollen kunde inte köras: " + ex.Message,
            };
        }
    }

    // ---- Enheter -------------------------------------------------------------------------

    private static readonly Dictionary<uint, string> DeviceErrors = new()
    {
        [1] = "är inte rätt konfigurerad",
        [3] = "har en skadad drivrutin",
        [10] = "kan inte starta",
        [12] = "hittar inte tillräckligt med resurser",
        [14] = "behöver en omstart",
        [18] = "behöver installera om drivrutinen",
        [19] = "har felaktiga registerinställningar",
        [21] = "håller på att tas bort",
        [24] = "saknas eller fungerar inte",
        [28] = "saknar drivrutin",
        [31] = "fungerar inte, drivrutinen kunde inte läsas in",
        [32] = "har en avstängd drivrutin",
        [37] = "drivrutinen kunde inte startas",
        [39] = "drivrutinen saknas eller är skadad",
        [43] = "har rapporterat ett fel och stoppats",
        [48] = "drivrutinen är blockerad",
        [52] = "drivrutinen är inte signerad",
    };

    private static HealthResult CheckDevices(string title, string icon)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        var broken = new List<string>();
        var disabled = new List<string>();

        foreach (var device in searcher.Get().Cast<ManagementBaseObject>())
        {
            var code = Convert.ToUInt32(device["ConfigManagerErrorCode"]);
            var name = device["Name"]?.ToString() ?? "Okänd enhet";
            if (code == 22)
                disabled.Add($"{name} (avstängd)");
            else if (code != 45) // 45 = enheten är inte ansluten just nu
                broken.Add($"{name}: {DeviceErrors.GetValueOrDefault(code, $"felkod {code}")}");
        }

        if (broken.Count > 0)
        {
            return new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Problem,
                Summary = $"{broken.Count} enhet(er) fungerar inte som de ska.",
                Details = [.. broken, .. disabled],
                FixHint = "Söka efter och installera om drivrutiner för enheterna.",
            };
        }

        return new HealthResult
        {
            Title = title,
            Icon = icon,
            Status = HealthStatus.Ok,
            Summary = disabled.Count > 0
                ? $"Alla enheter fungerar. {disabled.Count} är avstängda med flit."
                : "Alla enheter fungerar och har drivrutiner.",
            Details = disabled,
        };
    }

    // ---- Windows Update ------------------------------------------------------------------

    private static HealthResult CheckUpdates(string title, string icon)
    {
        // Windows Update Agent har ett COM-gränssnitt som även hittar drivrutiner från tillverkarna.
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                          ?? throw new InvalidOperationException("Windows Update finns inte på datorn.");
        dynamic session = Activator.CreateInstance(sessionType)!;
        try
        {
            session.ClientApplicationID = "Mattes AutoPerformance";
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;

            dynamic result;
            try
            {
                result = searcher.Search("IsInstalled=0 and IsHidden=0");
            }
            catch (COMException ex)
            {
                return new HealthResult
                {
                    Title = title,
                    Icon = icon,
                    Status = HealthStatus.Info,
                    Summary = "Kunde inte nå Windows Update. Är datorn ansluten till internet?",
                    Details = [$"Felkod 0x{ex.HResult:X8}"],
                };
            }

            var drivers = new List<string>();
            var software = new List<string>();
            int count = result.Updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic update = result.Updates.Item(i);
                string updateTitle = update.Title;
                // Type 2 = drivrutin, 1 = programvara (Windows, Office, Defender m.m.)
                if ((int)update.Type == 2) drivers.Add(updateTitle);
                else software.Add(updateTitle);
            }

            if (count == 0)
            {
                return new HealthResult
                {
                    Title = title,
                    Icon = icon,
                    Status = HealthStatus.Ok,
                    Summary = "Windows och drivrutinerna är uppdaterade.",
                };
            }

            var parts = new List<string>();
            if (software.Count > 0) parts.Add($"{software.Count} Windows-uppdatering(ar)");
            if (drivers.Count > 0) parts.Add($"{drivers.Count} drivrutinsuppdatering(ar)");

            return new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Warning,
                Summary = string.Join(" och ", parts) + " väntar på att installeras.",
                Details = [.. drivers.Select(d => "Drivrutin: " + d), .. software],
                FixHint = "Ladda ner och installera alla uppdateringar.",
            };
        }
        finally
        {
            Marshal.FinalReleaseComObject(session);
        }
    }

    // ---- Autostart -----------------------------------------------------------------------

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Över så här många påslagna autostartappar blir det en varning.</summary>
    private const int StartupWarningLimit = 8;

    private static HealthResult CheckStartupApps(string title, string icon)
    {
        var enabled = new List<string>();
        var disabledCount = 0;

        void Collect(RegistryKey root, string runKey, string approvedName)
        {
            using var run = root.OpenSubKey(runKey);
            if (run is null) return;
            using var approved = root.OpenSubKey(ApprovedKey + approvedName);
            foreach (var name in run.GetValueNames().Where(n => n != ""))
            {
                if (IsApproved(approved, name)) enabled.Add(name);
                else disabledCount++;
            }
        }

        void CollectFolder(string folder, RegistryKey root)
        {
            if (!Directory.Exists(folder)) return;
            using var approved = root.OpenSubKey(ApprovedKey + "StartupFolder");
            foreach (var file in Directory.GetFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var fileName = Path.GetFileName(file);
                if (IsApproved(approved, fileName)) enabled.Add(Path.GetFileNameWithoutExtension(file));
                else disabledCount++;
            }
        }

        Collect(Registry.CurrentUser, RunKey, "Run");
        Collect(Registry.LocalMachine, RunKey, "Run");
        Collect(Registry.LocalMachine, Run32Key, "Run32");
        CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser);
        CollectFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine);

        enabled.Sort(StringComparer.CurrentCultureIgnoreCase);
        var tooMany = enabled.Count > StartupWarningLimit;
        var summary = $"{enabled.Count} app(ar) startar automatiskt med Windows";
        summary += disabledCount > 0 ? $", {disabledCount} är redan avstängda." : ".";
        if (tooMany) summary += " Många autostartappar gör att datorn startar långsammare.";

        return new HealthResult
        {
            Title = title,
            Icon = icon,
            Status = tooMany ? HealthStatus.Warning : HealthStatus.Ok,
            Summary = summary,
            Details = enabled,
            FixHint = enabled.Count > 0 ? "Välja vilka appar som inte ska starta automatiskt." : null,
        };
    }

    /// <summary>
    /// Aktivitetshanteraren sparar av/på i StartupApproved. Första byten är jämn när appen är påslagen
    /// och udda när den är avstängd. Saknas värdet räknas appen som påslagen.
    /// </summary>
    private static bool IsApproved(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } data || data[0] % 2 == 0;

    // ---- Systemfiler ---------------------------------------------------------------------

    private static HealthResult CheckSystemFiles(string title, string icon)
    {
        if (!Elevation.IsAdministrator)
        {
            return new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Info,
                Summary = "Kräver administratör. Starta om appen som administratör för att kontrollera systemfilerna.",
            };
        }

        // Repair-WindowsImage svarar med samma engelska ord oavsett vilket språk Windows har.
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"(Repair-WindowsImage -Online -CheckHealth).ImageHealthState\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return output switch
        {
            "Healthy" => new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Ok,
                Summary = "Windows systemavbildning är hel. Inga skadade systemfiler har hittats.",
            },
            "Repairable" => new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Problem,
                Summary = "Windows har hittat skadade systemfiler som går att reparera.",
                FixHint = "Reparera systemfilerna med DISM och SFC.",
            },
            "NonRepairable" => new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Problem,
                Summary = "Windows systemavbildning är skadad och kan inte repareras automatiskt.",
                Details = ["En ominstallation eller återställning av Windows kan behövas."],
            },
            _ => new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Info,
                Summary = "Kunde inte avgöra systemfilernas status.",
                Details = output.Length > 0 ? [output] : [],
            },
        };
    }

    // ---- Diskar --------------------------------------------------------------------------

    /// <summary>Under så här stor andel ledigt utrymme blir det en varning.</summary>
    private const double LowSpaceShare = 0.10;

    private static HealthResult CheckDisks(string title, string icon)
    {
        var problems = new List<string>();
        var warnings = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, HealthStatus FROM MSFT_PhysicalDisk");
            foreach (var disk in searcher.Get().Cast<ManagementBaseObject>())
            {
                var health = Convert.ToInt32(disk["HealthStatus"]);
                var name = disk["FriendlyName"]?.ToString() ?? "Disk";
                if (health == 2) problems.Add($"{name} rapporterar att den mår dåligt. Säkerhetskopiera dina filer!");
                else if (health == 1) warnings.Add($"{name} rapporterar en varning om sin hälsa.");
            }
        }
        catch (ManagementException)
        {
            // Lagrings-WMI finns inte på alla datorer. Utrymmeskontrollen nedan fungerar ändå.
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0))
        {
            var share = (double)drive.TotalFreeSpace / drive.TotalSize;
            if (share < LowSpaceShare)
                warnings.Add($"{drive.Name} har bara {Format.Bytes(drive.TotalFreeSpace)} ledigt ({share:P0}).");
        }

        var status = problems.Count > 0 ? HealthStatus.Problem
            : warnings.Count > 0 ? HealthStatus.Warning
            : HealthStatus.Ok;

        return new HealthResult
        {
            Title = title,
            Icon = icon,
            Status = status,
            Summary = status == HealthStatus.Ok
                ? "Alla diskar mår bra och har gott om ledigt utrymme."
                : "Något behöver ses över på dina diskar.",
            Details = [.. problems, .. warnings],
            FixHint = warnings.Any(w => w.Contains("ledigt")) ? "Rensa tillfälliga filer för att frigöra utrymme." : null,
        };
    }

    // ---- Omstart -------------------------------------------------------------------------

    private static HealthResult CheckPendingRestart(string title, string icon)
    {
        var reasons = new List<string>();
        using (var key = Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
        {
            if (key is not null) reasons.Add("Windows Update har installerat uppdateringar som kräver omstart.");
        }
        using (var key = Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            if (key is not null) reasons.Add("Windows-komponenter väntar på att installeras klart vid omstart.");
        }

        return new HealthResult
        {
            Title = title,
            Icon = icon,
            Status = reasons.Count > 0 ? HealthStatus.Warning : HealthStatus.Ok,
            Summary = reasons.Count > 0
                ? "Datorn behöver startas om för att slutföra uppdateringar."
                : "Ingen omstart behövs.",
            Details = reasons,
        };
    }
}
