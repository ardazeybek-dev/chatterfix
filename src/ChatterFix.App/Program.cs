using System.Runtime;

namespace ChatterFix.App;

internal static class Program
{
    private const string SingleInstanceName = "Global\\ChatterFix.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Two copies would each install a hook and fight over the same events,
        // so the second one steps aside.
        using var instanceLock = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "ChatterFix is already running. Look for it in the notification area.",
                "ChatterFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // Garbage collector pauses would show up as click latency.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        ApplicationConfiguration.Initialize();

        using var tray = new TrayApplication();
        if (!tray.Start()) return;

        Application.Run();
    }
}
