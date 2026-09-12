using System.Globalization;
using ChatterFix.Core;
using ChatterFix.Core.Diagnostics;
using ChatterFix.Core.Filtering;

namespace ChatterFix.App;

/// <summary>
/// Live view of what the filter is doing. It doubles as evidence: the histogram
/// shows whether a switch is deteriorating, which no amount of software can fix.
/// </summary>
internal sealed class StatisticsForm : Form
{
    private readonly IStatisticsSession _session;
    private readonly ClickFilter _filter;
    private readonly ListView _buttonList = new();
    private readonly ListView _histogram = new();
    private readonly Label _summary = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public StatisticsForm(IStatisticsSession session)
    {
        _session = session;
        _filter = session.Filter;

        Text = "ChatterFix statistics";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 560);
        MinimumSize = new Size(560, 480);
        AutoScaleMode = AutoScaleMode.Font;
        Padding = new Padding(12);

        BuildLayout();

        _timer.Tick += (_, _) => UpdateView(force: false);
        _timer.Start();
        UpdateView(force: true);
    }

    private void BuildLayout()
    {
        _summary.Dock = DockStyle.Top;
        _summary.Height = 46;
        _summary.Padding = new Padding(4, 6, 4, 6);

        var buttonsLabel = new Label
        {
            Text = "Per button",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };

        _buttonList.View = View.Details;
        _buttonList.FullRowSelect = true;
        _buttonList.GridLines = true;
        _buttonList.Dock = DockStyle.Top;
        _buttonList.Height = 150;
        _buttonList.Columns.Add("Button", 90);
        _buttonList.Columns.Add("Presses", 70, HorizontalAlignment.Right);
        _buttonList.Columns.Add("Blocked", 70, HorizontalAlignment.Right);
        _buttonList.Columns.Add("Repaired", 75, HorizontalAlignment.Right);
        _buttonList.Columns.Add("Shortest gap", 100, HorizontalAlignment.Right);
        _buttonList.Columns.Add("Fault rate", 90, HorizontalAlignment.Right);

        var histogramLabel = new Label
        {
            Text = "Left button - gap between a release and the next press",
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };

        _histogram.View = View.Details;
        _histogram.FullRowSelect = true;
        _histogram.GridLines = true;
        _histogram.Dock = DockStyle.Fill;
        _histogram.Columns.Add("Interval", 110);
        _histogram.Columns.Add("Count", 70, HorizontalAlignment.Right);
        _histogram.Columns.Add("", 300);

        var reset = new Button { Text = "Reset counters", Dock = DockStyle.Bottom, Height = 30 };
        reset.Click += (_, _) =>
        {
            var answer = MessageBox.Show(
                "Clear every recorded click and start again from now?" + Environment.NewLine + Environment.NewLine
                + "The record so far is what shows whether the switch is getting worse, "
                + "so it is worth keeping unless you have just changed mice.",
                "ChatterFix",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            _session.ResetCounters();
            UpdateView(force: true);
        };

        // Added back to front: each Dock=Top control sits below the previous one.
        Controls.Add(_histogram);
        Controls.Add(histogramLabel);
        Controls.Add(_buttonList);
        Controls.Add(buttonsLabel);
        Controls.Add(_summary);
        Controls.Add(reset);
    }

    private void UpdateView(bool force)
    {
        if (!force && (WindowState == FormWindowState.Minimized || !Visible)) return;

        var stats = _filter.Statistics;
        var settings = _filter.Settings;

        var recordedFor = DateTimeOffset.Now - _session.RecordingSince;
        string period = recordedFor.TotalDays >= 1
            ? $"{recordedFor.TotalDays:F1} days"
            : recordedFor.TotalHours >= 1
                ? $"{recordedFor.TotalHours:F1} hours"
                : $"{recordedFor.TotalMinutes:F0} minutes";

        _summary.Text =
            $"Threshold {settings.Buttons[0].ChatterThresholdMs} ms   ·   "
            + $"drop repair {settings.Buttons[0].ReleaseDelayMs} ms   ·   "
            + (settings.Enabled ? "protecting" : "paused")
            + Environment.NewLine
            + $"Blocked {stats.TotalChatterSuppressed} faulty clicks and repaired "
            + $"{stats.TotalReleaseRepairs} dropped connections over {period} of recording.";

        UpdateButtonList(stats);
        UpdateHistogram(stats[MouseButton.Left]);
    }

    private void UpdateButtonList(ClickStatistics stats)
    {
        _buttonList.BeginUpdate();
        _buttonList.Items.Clear();

        for (int i = 0; i < ClickStatistics.ButtonCount; i++)
        {
            var button = (MouseButton)i;
            var s = stats[button];
            if (s.Downs == 0) continue;

            var item = new ListViewItem(ClickStatistics.ButtonName(button));
            item.SubItems.Add(s.Downs.ToString(CultureInfo.CurrentCulture));
            item.SubItems.Add(s.ChatterSuppressed.ToString(CultureInfo.CurrentCulture));
            item.SubItems.Add(s.ReleaseRepairs.ToString(CultureInfo.CurrentCulture));
            item.SubItems.Add($"{s.ReleaseGap.MinMs:F1} ms");
            item.SubItems.Add($"{s.ChatterRatePercent:F1}%");

            if (s.ChatterSuppressed > 0) item.ForeColor = Color.FromArgb(180, 95, 6);

            _buttonList.Items.Add(item);
        }

        if (_buttonList.Items.Count == 0)
            _buttonList.Items.Add(new ListViewItem("No clicks recorded yet"));

        _buttonList.EndUpdate();
    }

    private void UpdateHistogram(ButtonStatistics stats)
    {
        var counts = stats.ReleaseGap.Snapshot();
        long max = 0;
        foreach (long count in counts) max = Math.Max(max, count);

        _histogram.BeginUpdate();
        _histogram.Items.Clear();

        for (int i = 0; i < IntervalHistogram.UpperBoundsMs.Length; i++)
        {
            if (counts[i] == 0) continue;

            int lower = i == 0 ? 0 : IntervalHistogram.UpperBoundsMs[i - 1];
            int upper = IntervalHistogram.UpperBoundsMs[i];
            string range = upper == int.MaxValue ? $"{lower}+ ms" : $"{lower} - {upper} ms";

            var item = new ListViewItem(range);
            item.SubItems.Add(counts[i].ToString(CultureInfo.CurrentCulture));
            item.SubItems.Add(new string('█', (int)Math.Max(1, counts[i] * 40 / Math.Max(max, 1))));

            // Anything inside the threshold is beyond what a hand can do.
            if (upper <= _filter.Settings.Buttons[(int)MouseButton.Left].ChatterThresholdMs)
                item.ForeColor = Color.FromArgb(200, 40, 40);

            _histogram.Items.Add(item);
        }

        if (_histogram.Items.Count == 0)
            _histogram.Items.Add(new ListViewItem("No data yet - click a few times"));

        _histogram.EndUpdate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window should not stop the filter, only hide the view.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
