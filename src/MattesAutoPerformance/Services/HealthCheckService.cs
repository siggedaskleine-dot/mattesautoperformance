using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MattesAutoPerformance.Models;

namespace MattesAutoPerformance.Services;

/// <summary>
/// Letar efter problem på datorn. Allt här är skrivskyddat; åtgärderna finns i <see cref="FixService"/>.
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
                Fix = FixKind.RepairDrivers,
                FixLabel = "Reparera drivrutiner",
                FixHint = "Söker igenom enheterna igen och installerar drivrutiner från Windows Update.",
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
                Fix = FixKind.InstallUpdates,
                FixLabel = "Installera uppdateringar",
                FixHint = "Laddar ner och installerar alla uppdateringar.",
            };
        }
        finally
        {
            Marshal.FinalReleaseComObject(session);
        }
    }

    // ---- Autostart -----------------------------------------------------------------------

    /// <summary>Över så här många påslagna autostartappar blir det en varning.</summary>
    private const int StartupWarningLimit = 8;

    private static HealthResult CheckStartupApps(string title, string icon)
    {
        var entries = StartupService.GetAll();
        var enabled = entries.Where(e => e.Enabled).Select(e => e.Name).ToList();
        var disabledCount = entries.Count - enabled.Count;

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
            Fix = entries.Count > 0 ? FixKind.ManageStartup : null,
            FixLabel = "Välj appar",
            FixHint = "Välj själv vilka appar som ska starta med Windows. Det går att ändra tillbaka.",
        };
    }

    // ---- Systemfiler ---------------------------------------------------------------------

    private static HealthResult CheckSystemFiles(string title, string icon)
    {
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
                Fix = FixKind.RepairSystemFiles,
                FixLabel = "Kör SFC ändå",
                FixHint = "Vill du vara extra säker kan du ändå köra en full kontroll med DISM och SFC.",
            },
            "Repairable" => new HealthResult
            {
                Title = title,
                Icon = icon,
                Status = HealthStatus.Problem,
                Summary = "Windows har hittat skadade systemfiler som går att reparera.",
                Fix = FixKind.RepairSystemFiles,
                FixLabel = "Reparera systemfiler",
                FixHint = "Kör Windows egna reparationsverktyg DISM och SFC. Kan ta 10–30 minuter.",
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
                Fix = FixKind.RepairSystemFiles,
                FixLabel = "Reparera systemfiler",
                FixHint = "Kör Windows egna reparationsverktyg DISM och SFC. Kan ta 10–30 minuter.",
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
        };
    }

    // ---- Tillfälliga filer ---------------------------------------------------------------

    /// <summary>Över så här mycket tillfälliga filer blir det en varning.</summary>
    private const long TempWarningBytes = 1L << 30;

    private static HealthResult CheckTempFiles(string title, string icon)
    {
        var details = new List<string>();
        long total = 0;
        foreach (var folder in FixService.TempFolders)
        {
            var size = FixService.FolderSize(folder);
            total += size;
            details.Add($"{folder}: {Format.Bytes(size)}");
        }

        return new HealthResult
        {
            Title = title,
            Icon = icon,
            Status = total > TempWarningBytes ? HealthStatus.Warning : HealthStatus.Ok,
            Summary = $"Tillfälliga filer tar upp {Format.Bytes(total)}.",
            Details = details,
            Fix = total > 0 ? FixKind.CleanTempFiles : null,
            FixLabel = "Rensa",
            FixHint = "Tar bort tillfälliga filer som är äldre än ett dygn. Papperskorgen och dina egna filer rörs inte.",
        };
    }

    // ---- Energischema --------------------------------------------------------------------

    private static HealthResult CheckPowerPlan(string title, string icon)
    {
        var (guid, name) = FixService.GetActivePowerPlan();
        var fast = FixService.IsHighPerformancePlan(guid);
        var onBattery = FixService.HasBattery();

        return new HealthResult
        {
            Title = title,
            Icon = icon,
            Status = fast ? HealthStatus.Ok : HealthStatus.Info,
            Summary = fast
                ? $"Datorn använder energischemat \"{name}\" för bästa prestanda."
                : $"Datorn använder energischemat \"{name}\". Med \"Hög prestanda\" arbetar processorn snabbare.",
            Details = onBattery && !fast ? ["Datorn har ett batteri. Hög prestanda gör att batteriet tar slut fortare."] : [],
            Fix = fast ? null : FixKind.HighPerformancePower,
            FixLabel = "Byt till Hög prestanda",
            FixHint = "Byter energischema. Du kan byta tillbaka under Energialternativ i Kontrollpanelen.",
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
            Fix = reasons.Count > 0 ? FixKind.Restart : null,
            FixLabel = "Starta om nu",
            FixHint = "Sparar inget åt dig, så stäng dina program först.",
        };
    }
}
