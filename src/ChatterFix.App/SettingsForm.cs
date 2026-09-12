using ChatterFix.Core;
using ChatterFix.Core.Configuration;
using ChatterFix.Core.Diagnostics;

namespace ChatterFix.App;

/// <summary>
/// Settings dialog. Every control is created in code rather than through a designer
/// file, which keeps the whole dialog reviewable in one place.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown _threshold = new();
    private readonly NumericUpDown _releaseDelay = new();
    private readonly CheckBox[] _buttons = new CheckBox[ClickStatistics.ButtonCount];
    private readonly CheckBox _notify = new();

    private readonly AppConfiguration _original;

    public SettingsForm(AppConfiguration configuration)
    {
        _original = configuration;
        Result = configuration;

        Text = "ChatterFix settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(480, 430);
        Padding = new Padding(16);

        BuildLayout();
        LoadFrom(configuration);
    }

    public AppConfiguration Result { get; private set; }

    private void BuildLayout()
    {
        int y = 12;

        Controls.Add(SectionTitle("Chatter threshold", ref y));
        Controls.Add(Explanation(
            "A press arriving sooner than this after a release is treated as a hardware\n"
            + "fault and blocked. Human clicking stays above 35 ms even with jitter, so\n"
            + "25 ms leaves a wide margin.", ref y));

        _threshold.Minimum = 1;
        _threshold.Maximum = 100;
        _threshold.Location = new Point(20, y);
        _threshold.Width = 80;
        Controls.Add(_threshold);
        Controls.Add(new Label { Text = "ms", Location = new Point(108, y + 3), AutoSize = true });
        y += 40;

        Controls.Add(SectionTitle("Drop repair window", ref y));
        Controls.Add(Explanation(
            "A release is held this long before being passed on. If a press arrives inside\n"
            + "the window the contact only bounced, so both events are dropped and the hold\n"
            + "continues unbroken. Set to 0 to switch this off.", ref y));

        _releaseDelay.Minimum = 0;
        _releaseDelay.Maximum = 50;
        _releaseDelay.Location = new Point(20, y);
        _releaseDelay.Width = 80;
        Controls.Add(_releaseDelay);
        Controls.Add(new Label { Text = "ms", Location = new Point(108, y + 3), AutoSize = true });
        y += 40;

        Controls.Add(SectionTitle("Buttons to filter", ref y));

        int column = 20;
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i] = new CheckBox
            {
                Text = ClickStatistics.ButtonName((MouseButton)i),
                Location = new Point(column, y),
                AutoSize = true,
            };
            Controls.Add(_buttons[i]);
            column += 86;
        }
        y += 34;

        _notify.Text = "Show a notification the first time a fault is blocked";
        _notify.Location = new Point(20, y);
        _notify.AutoSize = true;
        Controls.Add(_notify);
        y += 40;

        var ok = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 190, y),
            Width = 84,
        };
        ok.Click += (_, _) => Save();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 100, y),
            Width = 84,
        };

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static Label SectionTitle(string text, ref int y)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(16, y),
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };
        y += 24;
        return label;
    }

    private static Label Explanation(string text, ref int y)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        y += 56;
        return label;
    }

    private void LoadFrom(AppConfiguration configuration)
    {
        _threshold.Value = Math.Clamp(configuration.ChatterThresholdMs, (int)_threshold.Minimum, (int)_threshold.Maximum);
        _releaseDelay.Value = Math.Clamp(configuration.ReleaseDelayMs, (int)_releaseDelay.Minimum, (int)_releaseDelay.Maximum);
        _notify.Checked = configuration.NotifyOnFirstBlock;

        for (int i = 0; i < _buttons.Length; i++)
            _buttons[i].Checked = i < configuration.ButtonsEnabled.Length && configuration.ButtonsEnabled[i];
    }

    private void Save()
    {
        var buttons = new bool[_buttons.Length];
        for (int i = 0; i < _buttons.Length; i++) buttons[i] = _buttons[i].Checked;

        Result = new AppConfiguration
        {
            Enabled = _original.Enabled,
            StartWithWindows = _original.StartWithWindows,
            ChatterThresholdMs = (int)_threshold.Value,
            ReleaseDelayMs = (int)_releaseDelay.Value,
            ButtonsEnabled = buttons,
            NotifyOnFirstBlock = _notify.Checked,
        };
    }
}
