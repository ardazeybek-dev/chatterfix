using ChatterFix.Core;
using ChatterFix.Core.Configuration;
using ChatterFix.Core.Filtering;
using ChatterFix.Core.Native;

namespace ChatterFix.App;

/// <summary>
/// Owns the notification-area icon and the filtering engine behind it.
/// There is no main window: the application is meant to be forgotten about,
/// so everything happens through the tray icon and its menu.
/// </summary>
internal sealed class TrayApplication : IDisposable
{
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _countsItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _enabledItem = new("Enabled") { CheckOnClick = true };
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };

    private readonly Icon _activeIcon = TrayIcons.Create(active: true);
    private readonly Icon _pausedIcon = TrayIcons.Create(active: false);

    private AppConfiguration _config = new();
    private FilterProfile _activeProfile = new();
    private ClickFilter? _filter;
    private ReleaseScheduler? _scheduler;
    private LowLevelMouseHook? _hook;
    private ForegroundWatcher? _watcher;
    private StatisticsForm? _statisticsForm;

    private bool _announcedFirstBlock;
    private bool _disposed;

    public bool Start()
    {
        _config = AppConfiguration.Load();
        _activeProfile = _config.ResolveProfile(null);

        if (!StartEngine()) return false;

        BuildMenu();

        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => ShowStatistics();

        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        RefreshStatus();
        return true;
    }

    private bool StartEngine()
    {
        _scheduler = new ReleaseScheduler(
            MonotonicClock.NowMicroseconds,
            button => InputInjector.SendButton(button, MouseEventKind.Up));

        _filter = new ClickFilter(_activeProfile.ToFilterSettings(_config.Enabled), releaseGate: _scheduler);
        _hook = new LowLevelMouseHook(_filter);

        try
        {
            _hook.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "ChatterFix could not install its mouse hook, so it cannot filter anything.\n\n"
                + ex.Message,
                "ChatterFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        _watcher = new ForegroundWatcher();
        _watcher.ForegroundChanged += OnForegroundChanged;
        return true;
    }

    /// <summary>
    /// Runs on the watcher's thread. It only swaps the settings the hook reads, which
    /// is an atomic reference assignment; the menu catches up on its own timer.
    /// </summary>
    private void OnForegroundChanged(string? processName)
    {
        var profile = _config.ResolveProfile(processName);
        if (ReferenceEquals(profile, _activeProfile)) return;

        _activeProfile = profile;
        ApplyConfiguration();
    }

    private void BuildMenu()
    {
        _enabledItem.Checked = _config.Enabled;
        _enabledItem.CheckedChanged += (_, _) => SetEnabled(_enabledItem.Checked);

        _startupItem.Checked = StartupRegistration.IsEnabled();
        _startupItem.CheckedChanged += (_, _) => SetStartWithWindows(_startupItem.Checked);

        var settingsItem = new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings());
        var statisticsItem = new ToolStripMenuItem("Live statistics...", null, (_, _) => ShowStatistics());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Application.Exit());

        _menu.Items.AddRange(
        [
            _statusItem,
            _countsItem,
            new ToolStripSeparator(),
            _enabledItem,
            settingsItem,
            statisticsItem,
            new ToolStripSeparator(),
            _startupItem,
            new ToolStripSeparator(),
            exitItem,
        ]);
    }

    private void SetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        ApplyConfiguration();
        SaveConfiguration();
        RefreshStatus();
    }

    private void SetStartWithWindows(bool enabled)
    {
        string executablePath = Environment.ProcessPath ?? Application.ExecutablePath;

        if (!StartupRegistration.SetEnabled(enabled, executablePath))
        {
            MessageBox.Show(
                "Could not change the startup setting. Windows refused access to the registry key.",
                "ChatterFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            _startupItem.Checked = StartupRegistration.IsEnabled();
            return;
        }

        _config.StartWithWindows = enabled;
        SaveConfiguration();
    }

    private void ApplyConfiguration()
        => _filter?.UpdateSettings(_activeProfile.ToFilterSettings(_config.Enabled));

    private void SaveConfiguration()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Settings could not be saved, so they will be lost when ChatterFix closes.\n\n" + ex.Message,
                "ChatterFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_config);
        if (form.ShowDialog() != DialogResult.OK) return;

        _config = form.Result.Sanitised();
        _activeProfile = _config.ResolveProfile(_watcher?.CurrentProcessName);
        ApplyConfiguration();
        SaveConfiguration();
        RefreshStatus();
    }

    private void ShowStatistics()
    {
        if (_filter is null) return;

        if (_statisticsForm is null || _statisticsForm.IsDisposed)
        {
            _statisticsForm = new StatisticsForm(_filter);
            _statisticsForm.Show();
        }
        else
        {
            // Closing the window only hides it, so bring the same instance back.
            _statisticsForm.Show();
            _statisticsForm.WindowState = FormWindowState.Normal;
            _statisticsForm.Activate();
        }
    }

    private void RefreshStatus()
    {
        if (_filter is null) return;

        bool enabled = _config.Enabled;
        var profile = _activeProfile;
        long blocked = _filter.Statistics.TotalChatterSuppressed;
        long repaired = _filter.Statistics.TotalReleaseRepairs;

        _notifyIcon.Icon = enabled ? _activeIcon : _pausedIcon;

        _statusItem.Text = enabled
            ? $"Protecting - {profile.Name} profile ({profile.ChatterThresholdMs} ms)"
            : "Paused - nothing is being filtered";
        _countsItem.Text = $"Blocked {blocked} faults, repaired {repaired} drops";

        // Windows caps the tooltip at 63 characters.
        _notifyIcon.Text = enabled
            ? $"ChatterFix - {profile.Name} - {blocked} blocked"
            : "ChatterFix - paused";

        if (enabled && blocked > 0 && !_announcedFirstBlock && _config.NotifyOnFirstBlock)
        {
            _announcedFirstBlock = true;
            _notifyIcon.BalloonTipTitle = "ChatterFix caught a faulty click";
            _notifyIcon.BalloonTipText =
                "Your mouse sent a second click no hand could have produced, and it was blocked.";
            _notifyIcon.ShowBalloonTip(5000);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refreshTimer.Stop();
        _refreshTimer.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();

        _watcher?.Dispose();

        // Stop filtering first, then flush: a release still held back would otherwise
        // leave the button pressed for every application on the system.
        _hook?.Dispose();
        _scheduler?.Dispose();

        _statisticsForm?.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }
}
