using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MattesAutoPerformance.Models;

namespace MattesAutoPerformance.Services;

public sealed record FixOutcome(bool Success, bool RestartRequired = false);

/// <summary>
/// Åtgärderna bakom knapparna i hälsokontrollen. Allt loggas till <c>log</c> så att man ser vad som händer.
/// </summary>
public static partial class FixService
{
    private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    static FixService()
    {
        // DISM och powercfg skriver med konsolens teckentabell (t.ex. 850), som .NET inte har inbyggt.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static FixOutcome Run(FixKind kind, IProgress<string> log) => kind switch
    {
        FixKind.InstallUpdates => InstallUpdates(log, driversOnly: false),
        FixKind.RepairDrivers => RepairDrivers(log),
        FixKind.RepairSystemFiles => RepairSystemFiles(log),
        FixKind.CleanTempFiles => CleanTempFiles(log),
        FixKind.HighPerformancePower => SetHighPerformance(log),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Åtgärden körs inte här."),
    };

    // ---- Återställningspunkt -------------------------------------------------------------

    public static bool CreateRestorePoint(IProgress<string> log)
    {
        log.Report("Skapar en återställningspunkt…");
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var args = restore.GetMethodParameters("CreateRestorePoint");
            args["Description"] = "Mattes AutoPerformance " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            args["RestorePointType"] = 12; // MODIFY_SETTINGS
            args["EventType"] = 100; // BEGIN_SYSTEM_CHANGE
            var result = restore.InvokeMethod("CreateRestorePoint", args, null);
            var code = Convert.ToUInt32(result["ReturnValue"]);
            if (code == 0)
            {
                // Windows skapar högst en punkt per dygn och hoppar tyst över resten.
                log.Report("Återställningspunkt klar (Windows återanvänder en punkt om det redan finns en från senaste dygnet).");
                return true;
            }

            log.Report($"Kunde inte skapa återställningspunkt (felkod {code}). Systemskydd är kanske avstängt.");
            return false;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            log.Report("Kunde inte skapa återställningspunkt: " + ex.Message);
            return false;
        }
    }

    // ---- Windows Update ------------------------------------------------------------------

    private static FixOutcome InstallUpdates(IProgress<string> log, bool driversOnly)
    {
        log.Report(driversOnly ? "Söker efter drivrutiner i Windows Update…" : "Söker efter uppdateringar…");

        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        try
        {
            session.ClientApplicationID = "Mattes AutoPerformance";
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            var criteria = driversOnly ? "IsInstalled=0 and IsHidden=0 and Type='Driver'" : "IsInstalled=0 and IsHidden=0";
            dynamic found = searcher.Search(criteria);

            dynamic toInstall = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            int count = found.Updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic update = found.Updates.Item(i);
                string title = update.Title;
                // Uppdateringar som vill fråga användaren om något (t.ex. nya Windows-versioner) hoppas över.
                if ((bool)update.InstallationBehavior.CanRequestUserInput)
                {
                    log.Report($"Hoppar över (måste installeras via Inställningar): {title}");
                    continue;
                }
                if (!(bool)update.EulaAccepted) update.AcceptEula();
                toInstall.Add(update);
            }

            int selected = toInstall.Count;
            if (selected == 0)
            {
                log.Report("Inga uppdateringar att installera.");
                return new FixOutcome(true);
            }

            log.Report($"Laddar ner {selected} uppdatering(ar). Det kan ta en stund…");
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = toInstall;
            downloader.Download();

            log.Report("Installerar…");
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = toInstall;
            dynamic result = installer.Install();

            var failed = 0;
            for (var i = 0; i < selected; i++)
            {
                int code = result.GetUpdateResult(i).ResultCode;
                string title = toInstall.Item(i).Title;
                // 2 = lyckades, 3 = lyckades med fel, 4 = misslyckades, 5 = avbröts
                var ok = code is 2 or 3;
                if (!ok) failed++;
                log.Report((ok ? "✓ " : "✗ ") + title);
            }

            bool restart = result.RebootRequired;
            log.Report(failed == 0
                ? $"Klart! {selected} uppdatering(ar) installerade."
                : $"{selected - failed} av {selected} uppdateringar installerades.");
            return new FixOutcome(failed == 0, restart);
        }
        catch (COMException ex)
        {
            log.Report($"Windows Update svarade med fel 0x{ex.HResult:X8}. Är datorn ansluten till internet?");
            return new FixOutcome(false);
        }
        finally
        {
            Marshal.FinalReleaseComObject(session);
        }
    }

    private static FixOutcome RepairDrivers(IProgress<string> log)
    {
        log.Report("Söker igenom enheterna igen…");
        RunProcess("pnputil.exe", "/scan-devices", log, OemEncoding);
        return InstallUpdates(log, driversOnly: true);
    }

    // ---- Systemfiler ---------------------------------------------------------------------

    private static FixOutcome RepairSystemFiles(IProgress<string> log)
    {
        log.Report("Steg 1 av 2: DISM reparerar Windows systemavbildning. Det kan ta 10–20 minuter…");
        var dism = RunProcess("dism.exe", "/Online /Cleanup-Image /RestoreHealth", log, OemEncoding);
        if (dism != 0) log.Report($"DISM avslutades med kod {dism}. Fortsätter med SFC ändå.");

        log.Report("Steg 2 av 2: SFC kontrollerar och reparerar systemfilerna…");
        // SFC skriver UTF-16 när utdata skickas vidare till ett annat program.
        var sfc = RunProcess("sfc.exe", "/scannow", log, Encoding.Unicode);

        log.Report(dism == 0 && sfc == 0 ? "Reparationen är klar." : "Reparationen är klar, men något verktyg rapporterade fel. Se loggen ovan.");
        return new FixOutcome(dism == 0 && sfc == 0, RestartRequired: true);
    }

    // ---- Tillfälliga filer ---------------------------------------------------------------

    public static IReadOnlyList<string> TempFolders { get; } =
        new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        }
        .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(Directory.Exists)
        .ToList();

    private static readonly System.IO.EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static long FolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", AllFiles).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static FixOutcome CleanTempFiles(IProgress<string> log)
    {
        // Filer från senaste dygnet kan användas av program som körs just nu.
        var cutoff = DateTime.Now.AddDays(-1);
        long freed = 0;
        var removed = 0;
        var skipped = 0;

        foreach (var folder in TempFolders)
        {
            log.Report($"Rensar {folder}…");
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", AllFiles))
            {
                try
                {
                    if (file.LastWriteTime > cutoff) continue;
                    var size = file.Length;
                    file.Attributes = FileAttributes.Normal;
                    file.Delete();
                    freed += size;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++; // Används av ett program just nu.
                }
            }

            // Tomma mappar tas bort inifrån och ut.
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories("*", AllFiles)
                         .OrderByDescending(d => d.FullName.Length))
            {
                try
                {
                    if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        log.Report($"Klart! Tog bort {removed} filer och frigjorde {Format.Bytes(freed)}." +
                   (skipped > 0 ? $" {skipped} filer användes och fick vara kvar." : ""));
        return new FixOutcome(true);
    }

    // ---- Energischema --------------------------------------------------------------------

    public static (string Guid, string Name) GetActivePowerPlan()
    {
        var output = RunProcessForOutput("powercfg.exe", "/getactivescheme");
        var match = PowerSchemeRegex().Match(output);
        return match.Success ? (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value) : ("", "Okänt");
    }

    public static bool IsHighPerformancePlan(string guid) =>
        guid is HighPerformanceGuid or UltimatePerformanceGuid;

    public static bool HasBattery()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
        return searcher.Get().Count > 0;
    }

    private static FixOutcome SetHighPerformance(IProgress<string> log)
    {
        if (RunProcess("powercfg.exe", "/setactive " + HighPerformanceGuid, log, OemEncoding) != 0)
        {
            // På många nyare datorer är schemat dolt, men det går att skapa en kopia av det.
            log.Report("Hög prestanda saknas, skapar det…");
            var created = RunProcessForOutput("powercfg.exe", "/duplicatescheme " + HighPerformanceGuid);
            var match = GuidRegex().Match(created);
            if (!match.Success || RunProcess("powercfg.exe", "/setactive " + match.Value, log, OemEncoding) != 0)
            {
                log.Report("Kunde inte byta energischema.");
                return new FixOutcome(false);
            }
        }

        log.Report("Klart! Energischemat är nu Hög prestanda.");
        return new FixOutcome(true);
    }

    // ---- Hjälpmetoder --------------------------------------------------------------------

    private static Encoding OemEncoding =>
        Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    /// <summary>Kör ett program, skriver dess utdata till loggen och returnerar slutkoden.</summary>
    private static int RunProcess(string file, string arguments, IProgress<string> log, Encoding encoding)
    {
        using var process = Start(file, arguments, encoding);
        process.OutputDataReceived += (_, e) => LogLine(e.Data, log);
        process.ErrorDataReceived += (_, e) => LogLine(e.Data, log);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string RunProcessForOutput(string file, string arguments)
    {
        using var process = Start(file, arguments, OemEncoding);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static Process Start(string file, string arguments, Encoding encoding) =>
        Process.Start(new ProcessStartInfo(file, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        })!;

    private static void LogLine(string? line, IProgress<string> log)
    {
        line = line?.Replace("\0", "").Trim();
        // Förloppsrader ("[==== 45.0% ====]") skulle fylla loggen, så de hoppas över.
        if (string.IsNullOrEmpty(line) || line.Contains('%')) return;
        log.Report("   " + line);
    }

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+)\)")]
    private static partial Regex PowerSchemeRegex();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();
}
