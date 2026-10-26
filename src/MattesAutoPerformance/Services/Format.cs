using System.Globalization;
using System.Management;

namespace MattesAutoPerformance.Services;

internal static class Format
{
    private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return bytes.ToString(bytes >= 100 || unit == 0 ? "0" : "0.#", Sv) + " " + units[unit];
    }

    public static string MHz(uint mhz) =>
        mhz >= 1000 ? (mhz / 1000.0).ToString("0.00", Sv) + " GHz" : mhz + " MHz";

    public static string Number(double value, string format = "0.#") => value.ToString(format, Sv);

    /// <summary>WMI-datum ser ut som "20240131000000.000000+000".</summary>
    public static string WmiDate(object? value)
    {
        if (value is not string s || s.Length < 8) return "";
        try
        {
            return ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd", Sv);
        }
        catch (FormatException)
        {
            return s[..4] + "-" + s[4..6] + "-" + s[6..8];
        }
    }

    public static string Uptime(TimeSpan t) =>
        t.Days > 0 ? $"{t.Days} d {t.Hours} h {t.Minutes} min" : $"{t.Hours} h {t.Minutes} min";
}
