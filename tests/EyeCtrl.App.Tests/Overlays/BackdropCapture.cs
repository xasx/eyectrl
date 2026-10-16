namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// The screen-capture plumbing the backdrop live checks share (rounds 1-2, retired 2026-10-14, and
/// the live look): the primary screen into one bitmap, sampled centrally (every 2nd px) and
/// full-screen (every 16th), the statistics, the human-readable description, the machine-line
/// writing (invariant, ';'-separated — this machine's culture writes decimal commas), and the
/// session state that can suppress materials.
/// </summary>
internal static class BackdropCapture
{
    /// <summary>One capture pair: the central fifth and the sparse full-screen sample.</summary>
    internal sealed record ScreenSample(SampleStats Centre, SampleStats Full);

    /// <summary>
    /// One region's statistics over the sampled pixels. <see cref="Dark8Fraction"/> is the share of
    /// pixels with luma below 8 — a near-black surface reads about 100 %, the glass looks like the
    /// baseline (sweep 3b's gate).
    /// </summary>
    internal sealed record SampleStats(
        double MeanR, double MeanG, double MeanB, double LumaMean, double LumaStdDev, double BlackFraction,
        int Pixels, double Dark8Fraction = 0.0);

    /// <summary>The primary screen into one bitmap, sampled centrally (every 2nd px) and full-screen (every 16th).</summary>
    internal static ScreenSample Grab()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left, bounds.Top, 0, 0,
                new System.Drawing.Size(bounds.Width, bounds.Height), System.Drawing.CopyPixelOperation.SourceCopy);
        }

        var width = bounds.Width / 5;
        var height = bounds.Height / 5;
        var centre = Stats(bitmap, (bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height, step: 2);
        var full = Stats(bitmap, 0, 0, bounds.Width, bounds.Height, step: 16);
        return new ScreenSample(centre, full);
    }

    internal static SampleStats Stats(
        System.Drawing.Bitmap bitmap, int x0, int y0, int width, int height, int step)
    {
        long n = 0;
        long black = 0;
        long dark8 = 0;
        double sum = 0, sumSq = 0, sumR = 0, sumG = 0, sumB = 0;
        for (var y = y0; y < y0 + height; y += step)
        {
            for (var x = x0; x < x0 + width; x += step)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luma = 0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B;
                n++;
                sum += luma;
                sumSq += luma * luma;
                sumR += pixel.R;
                sumG += pixel.G;
                sumB += pixel.B;
                if (luma < 8.0) dark8++;
                if (pixel.R == 0 && pixel.G == 0 && pixel.B == 0) black++;
            }
        }

        var mean = sum / n;
        var stdDev = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        return new SampleStats(
            sumR / n, sumG / n, sumB / n, mean, stdDev, (double)black / n, (int)n, (double)dark8 / n);
    }

    internal static string Describe(SampleStats stats) =>
        $"mean RGB ({stats.MeanR:0.0}, {stats.MeanG:0.0}, {stats.MeanB:0.0}), luma mean {stats.LumaMean:0.00}, "
        + $"stddev {stats.LumaStdDev:0.00}, exactly black {stats.BlackFraction:P1} over {stats.Pixels} px";

    /// <summary>
    /// Writes the case readout so it is in the run's log directory whatever the runner does with
    /// captured console output: <c>EYECTRL_AB_LOG_DIR</c> (the script sets it) or a temp folder.
    /// </summary>
    internal static void WriteLog(int caseNumber, string tag, string text)
    {
        var directory = Environment.GetEnvironmentVariable("EYECTRL_AB_LOG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "eyectrl-backdrop-ab");
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ab-{caseNumber}-{tag}.txt");
        File.WriteAllText(path, text);
        Console.WriteLine($"[backdrop A/B] case {caseNumber} readout written to {path}");
        Console.WriteLine(text);
    }

    /// <summary>
    /// The HKCU transparency-effects setting plus the energy-saver state. A material that is
    /// unavailable for the session would otherwise read as black or flat too.
    /// </summary>
    internal static string EnvironmentState()
    {
        string transparency;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            transparency = $"EnableTransparency (HKCU Personalize): {key?.GetValue("EnableTransparency") ?? "missing"}";
        }
        catch
        {
            transparency = "EnableTransparency (HKCU Personalize): unreadable";
        }

        var status = default(SystemPowerStatus);
        if (!GetSystemPowerStatus(ref status))
            return $"{transparency}; power state unreadable";
        var saver = status.SystemStatusFlag == 1 ? "energy saver ON" : "energy saver off";
        var line = status.ACLineStatus == 1 ? "AC" : "battery";
        var battery = status.BatteryLifePercent <= 100 ? $"battery {status.BatteryLifePercent}%" : "battery ?";
        return $"{transparency}; {saver}, {line}, {battery}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);
}
