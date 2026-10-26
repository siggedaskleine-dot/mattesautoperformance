using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using MattesAutoPerformance.Models;

namespace MattesAutoPerformance.Services;

/// <summary>
/// Läser datorns hårdvara via WMI, registret och .NET. Allt här är skrivskyddat.
/// </summary>
public sealed class HardwareService
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";
    private const string DisplayAdapterClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public List<HardwareSection> ReadAll()
    {
        // Varje del läses för sig så att ett fel i en del inte stoppar resten.
        return
        [
            Safe("Datorn", "", ReadSystem),
            Safe("Processor", "", ReadCpu),
            Safe("Grafikkort", "", ReadGpu),
            Safe("Minne (RAM)", "", ReadMemory),
            Safe("Moderkort och BIOS", "", ReadMotherboard),
            Safe("Lagring", "", ReadStorage),
            Safe("Nätverk", "", ReadNetwork),
        ];
    }

    private static HardwareSection Safe(string title, string icon, Func<string, string, HardwareSection> read)
    {
        try
        {
            return read(title, icon);
        }
        catch (Exception ex)
        {
            return new HardwareSection { Title = title, Icon = icon, Headline = "Kunde inte läsas" }
                .Add("Fel", ex.Message);
        }
    }

    private static HardwareSection ReadSystem(string title, string icon)
    {
        var cs = QueryFirst("SELECT Manufacturer, Model, Name FROM Win32_ComputerSystem");
        var os = QueryFirst("SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, " +
                            "InstallDate FROM Win32_OperatingSystem");

        var displayVersion = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
        var osName = Str(os, "Caption").Replace("Microsoft ", "");
        if (!string.IsNullOrEmpty(displayVersion)) osName += $" ({displayVersion})";

        var section = new HardwareSection { Title = title, Icon = icon, Headline = Str(cs, "Name") }
            .Add("Tillverkare", Str(cs, "Manufacturer"))
            .Add("Modell", Str(cs, "Model"))
            .Add("Windows", osName)
            .Add("Version", $"{Str(os, "Version")} ({Str(os, "OSArchitecture")})")
            .Add("Installerad", Format.WmiDate(os?["InstallDate"]));

        if (os?["LastBootUpTime"] is string boot)
            section.Add("Igång sedan", Format.Uptime(DateTime.Now - ManagementDateTimeConverter.ToDateTime(boot)));

        return section;
    }

    private static HardwareSection ReadCpu(string title, string icon)
    {
        var cpus = Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, " +
                         "MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, LoadPercentage " +
                         "FROM Win32_Processor");
        var cpu = cpus.FirstOrDefault();

        var section = new HardwareSection { Title = title, Icon = icon, Headline = Str(cpu, "Name") }
            .Add("Kärnor / trådar", $"{Num(cpu, "NumberOfCores")} / {Num(cpu, "NumberOfLogicalProcessors")}")
            .Add("Maxfrekvens", Format.MHz((uint)Num(cpu, "MaxClockSpeed")))
            .Add("L2-cache", Num(cpu, "L2CacheSize") > 0 ? Format.Bytes(Num(cpu, "L2CacheSize") * 1024) : null)
            .Add("L3-cache", Num(cpu, "L3CacheSize") > 0 ? Format.Bytes(Num(cpu, "L3CacheSize") * 1024) : null)
            .Add("Sockel", Str(cpu, "SocketDesignation"));

        if (cpus.Count > 1) section.Add("Antal processorer", cpus.Count.ToString());
        if (cpu?["LoadPercentage"] is ushort load)
            section.AddUsage("Belastning just nu", $"{load} %", load);

        return section;
    }

    private static HardwareSection ReadGpu(string title, string icon)
    {
        var gpus = Query("SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoProcessor, " +
                         "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate " +
                         "FROM Win32_VideoController");
        var vram = ReadVramFromRegistry();

        var section = new HardwareSection
        {
            Title = title,
            Icon = icon,
            Headline = string.Join(" + ", gpus.Select(g => Str(g, "Name")).Where(n => n != "")),
        };

        foreach (var gpu in gpus)
        {
            var name = Str(gpu, "Name");
            // AdapterRAM i WMI är bara 32 bitar och visar max 4 GB, så registret används i första hand.
            var memory = vram.TryGetValue(name, out var bytes) ? bytes : Num(gpu, "AdapterRAM");
            var item = new HardwareSection { Title = name, Icon = icon }
                .Add("Grafikminne", memory > 0 ? Format.Bytes(memory) : null)
                .Add("Drivrutin", Str(gpu, "DriverVersion"))
                .Add("Drivrutinsdatum", Format.WmiDate(gpu["DriverDate"]));

            if (Num(gpu, "CurrentHorizontalResolution") > 0)
                item.Add("Upplösning", $"{Num(gpu, "CurrentHorizontalResolution")} × " +
                                       $"{Num(gpu, "CurrentVerticalResolution")} @ {Num(gpu, "CurrentRefreshRate")} Hz");

            section.Items.Add(item);
        }

        return section;
    }

    private static Dictionary<string, double> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClass);
        if (classKey is null) return result;

        foreach (var subName in classKey.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = classKey.OpenSubKey(subName);
                if (key?.GetValue("DriverDesc") is not string desc) continue;
                var size = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => 0L,
                };
                if (size > 0) result[desc] = size;
            }
            catch (System.Security.SecurityException)
            {
                // Vissa undernycklar är låsta för vanliga användare.
            }
        }

        return result;
    }

    private static HardwareSection ReadMemory(string title, string icon)
    {
        var modules = Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, " +
                            "DeviceLocator FROM Win32_PhysicalMemory");
        var arrays = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
        var os = QueryFirst("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");

        var installed = modules.Sum(m => Num(m, "Capacity"));
        var slots = arrays.Sum(a => Num(a, "MemoryDevices"));
        var speed = modules.Select(m => Num(m, "ConfiguredClockSpeed") > 0
            ? Num(m, "ConfiguredClockSpeed") : Num(m, "Speed")).DefaultIfEmpty(0).Max();

        var section = new HardwareSection { Title = title, Icon = icon, Headline = $"{Format.Bytes(installed)} installerat" }
            .Add("Hastighet", speed > 0 ? $"{speed} MT/s" : null)
            .Add("Moduler", slots > 0 ? $"{modules.Count} av {slots} platser används" : modules.Count.ToString());

        var totalKb = Num(os, "TotalVisibleMemorySize");
        var freeKb = Num(os, "FreePhysicalMemory");
        if (totalKb > 0)
        {
            var used = totalKb - freeKb;
            section.AddUsage("Används just nu", $"{Format.Bytes(used * 1024)} av {Format.Bytes(totalKb * 1024)}",
                used / totalKb * 100);
        }

        foreach (var m in modules)
        {
            section.Items.Add(new HardwareSection { Title = Str(m, "DeviceLocator"), Icon = icon }
                .Add("Storlek", Format.Bytes(Num(m, "Capacity")))
                .Add("Tillverkare", Str(m, "Manufacturer"))
                .Add("Artikelnummer", Str(m, "PartNumber")));
        }

        return section;
    }

    private static HardwareSection ReadMotherboard(string title, string icon)
    {
        var board = QueryFirst("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
        var bios = QueryFirst("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        var secureBoot = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
        var tpm = TryQueryFirst(@"root\CIMV2\Security\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm");

        return new HardwareSection
            {
                Title = title,
                Icon = icon,
                Headline = $"{Str(board, "Manufacturer")} {Str(board, "Product")}".Trim(),
            }
            .Add("Revision", Str(board, "Version"))
            .Add("BIOS-tillverkare", Str(bios, "Manufacturer"))
            .Add("BIOS-version", Str(bios, "SMBIOSBIOSVersion"))
            .Add("BIOS-datum", Format.WmiDate(bios?["ReleaseDate"]))
            .Add("Secure Boot", secureBoot is int sb ? (sb == 1 ? "På" : "Av") : null)
            .Add("TPM", tpm is null ? null : "Version " + Str(tpm, "SpecVersion").Split(',')[0].Trim());
    }

    private static HardwareSection ReadStorage(string title, string icon)
    {
        var section = new HardwareSection { Title = title, Icon = icon };

        var disks = TryQuery(StorageScope,
            "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
        if (disks.Count > 0)
        {
            foreach (var d in disks)
            {
                section.Items.Add(new HardwareSection { Title = Str(d, "FriendlyName"), Icon = icon }
                    .Add("Typ", $"{MediaType(Num(d, "MediaType"))} ({BusType(Num(d, "BusType"))})")
                    .Add("Storlek", Format.Bytes(Num(d, "Size")))
                    .Add("Hälsa", Health(Num(d, "HealthStatus"))));
            }
        }
        else
        {
            foreach (var d in Query("SELECT Model, Size, InterfaceType, Status FROM Win32_DiskDrive"))
            {
                section.Items.Add(new HardwareSection { Title = Str(d, "Model"), Icon = icon }
                    .Add("Anslutning", Str(d, "InterfaceType"))
                    .Add("Storlek", Format.Bytes(Num(d, "Size")))
                    .Add("Status", Str(d, "Status")));
            }
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 0))
        {
            var used = drive.TotalSize - drive.TotalFreeSpace;
            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.Name} {drive.VolumeLabel}";
            section.AddUsage(label, $"{Format.Bytes(drive.TotalFreeSpace)} ledigt av {Format.Bytes(drive.TotalSize)}",
                (double)used / drive.TotalSize * 100);
        }

        section.Headline = section.Items.Count == 1 ? section.Items[0].Title : $"{section.Items.Count} diskar";
        return section;
    }

    private static HardwareSection ReadNetwork(string title, string icon)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        var section = new HardwareSection
        {
            Title = title,
            Icon = icon,
            Headline = adapters.Count == 0 ? "Inte ansluten" : $"{adapters.Count} aktiv(a) anslutning(ar)",
        };

        foreach (var n in adapters)
        {
            var ipv4 = n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Kabel";
            section.Items.Add(new HardwareSection { Title = n.Description, Icon = icon }
                .Add("Typ", kind)
                .Add("Hastighet", n.Speed > 0 ? LinkSpeed(n.Speed) : null)
                .Add("IP-adress", ipv4)
                .Add("MAC-adress", MacAddress(n)));
        }

        return section;
    }

    private static string LinkSpeed(long bitsPerSecond) =>
        bitsPerSecond >= 1_000_000_000
            ? Format.Number(bitsPerSecond / 1e9) + " Gbit/s"
            : Format.Number(bitsPerSecond / 1e6, "0") + " Mbit/s";

    private static string MacAddress(NetworkInterface n) =>
        string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));

    private static string MediaType(double v) => v switch
    {
        3 => "Hårddisk (HDD)",
        4 => "SSD",
        5 => "SCM",
        _ => "Okänd",
    };

    private static string BusType(double v) => v switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        11 => "SATA",
        12 => "SD",
        17 => "NVMe",
        _ => "annan anslutning",
    };

    private static string Health(double v) => v switch
    {
        0 => "Bra",
        1 => "Varning",
        2 => "Dålig",
        _ => "Okänd",
    };

    private static List<ManagementBaseObject> Query(string wql) => Query(@"root\CIMV2", wql);

    private static List<ManagementBaseObject> Query(string scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static List<ManagementBaseObject> TryQuery(string scope, string wql)
    {
        try
        {
            return Query(scope, wql);
        }
        catch (ManagementException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static ManagementBaseObject? QueryFirst(string wql) => Query(wql).FirstOrDefault();

    private static ManagementBaseObject? TryQueryFirst(string scope, string wql) => TryQuery(scope, wql).FirstOrDefault();

    private static string Str(ManagementBaseObject? obj, string property) =>
        obj?[property]?.ToString()?.Trim() ?? "";

    private static double Num(ManagementBaseObject? obj, string property) =>
        obj?[property] is { } v ? Convert.ToDouble(v) : 0;
}
