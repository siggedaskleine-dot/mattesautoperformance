using System.Runtime.InteropServices;

namespace MattesAutoPerformance.Services;

/// <summary>Mäter processor- och minnesanvändning just nu, för mätarna i sidhuvudet.</summary>
public sealed class SystemMonitor
{
    private ulong _lastIdle, _lastTotal;

    /// <summary>Processoranvändning i procent sedan förra anropet.</summary>
    public double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        // Kerneltiden innehåller även vilotiden.
        var total = kernel + user;
        var idleDelta = idle - _lastIdle;
        var totalDelta = total - _lastTotal;
        var first = _lastTotal == 0;
        _lastIdle = idle;
        _lastTotal = total;
        return first || totalDelta == 0 ? 0 : 100.0 * (totalDelta - idleDelta) / totalDelta;
    }

    public static double MemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
