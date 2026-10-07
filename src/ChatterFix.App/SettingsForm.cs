using System.Text.Json;
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
    private readonly ListBox _profileList = new();
    private readonly TextBox _name = new();
    private readonly TextBox _processes = new();
    private readonly NumericUpDown _threshold = new();
    private readonly NumericUpDown _releaseDelay = new();
    private readonly NumericUpDown _holdRepair = new();
    private readonly CheckBox[] _buttons = new CheckBox[ClickStatistics.ButtonCount];
    private readonly CheckBox _notify = new();
    private readonly Label _fallbackNote = new();

    private readonly List<FilterProfile> _profiles;
    private readonly bool _originalEnabled;
    private readonly bool _originalStartup;

    private int _loadedIndex = -1;
    private bool _loading;

    public SettingsForm(AppConfiguration configuration)
    {
        _originalEnabled = configuration.Enabled;
        _originalStartup = configuration.StartWithWindows;

        // Work on a copy so Cancel really cancels.
        _profiles = Clone(configuration.Profiles);
        Result = configuration;

        Text = "ChatterFix settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(660, 552);

        BuildLayout();

        _notify.Checked = configuration.NotifyOnFirstBlock;
        ReloadProfileList(0);
    }

    public AppConfiguration Result { get; private set; }

    private static List<FilterProfile> Clone(List<FilterProfile> profiles)
    {
        // A round trip through JSON is the cheapest honest deep copy here, and the
        // type is already designed to survive it.
        string json = JsonSerializer.Serialize(profiles);
        return JsonSerializer.Deserialize<List<FilterProfile>>(json) ?? FilterProfile.CreateDefaults();
    }

    private void BuildLayout()
    {
        var profilesLabel = new Label
        {
            Text = "Profiles",
            Location = new Point(16, 14),
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };

        _profileList.Location = new Point(16, 38);
        _profileList.Size = new Size(180, 300);
        _profileList.SelectedIndexChanged += (_, _) => OnProfileSelected();

        var add = new Button { Text = "Add", Location = new Point(16, 346), Width = 86 };
        add.Click += (_, _) => AddProfile();

        var remove = new Button { Text = "Remove", Location = new Point(110, 346), Width = 86 };
        remove.Click += (_, _) => RemoveProfile();

        int x = 220;
        int y = 38;

        Controls.Add(Bold("Applies while this application has focus", x, 14));

        Controls.Add(new Label { Text = "Name", Location = new Point(x, y + 4), AutoSize = true });
        _name.Location = new Point(x + 120, y);
        _name.Width = 290;
        _name.TextChanged += (_, _) => OnNameChanged();
        y += 34;

        Controls.Add(new Label { Text = "Process names", Location = new Point(x, y + 4), AutoSize = true });
        _processes.Location = new Point(x + 120, y);
        _processes.Width = 290;
        y += 28;

        Controls.Add(new Label
        {
            Text = "Comma separated, without .exe (for example: javaw, Minecraft).\n"
                 + "Leave empty to make this the profile used for everything else.",
            Location = new Point(x + 120, y),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        y += 44;

        Controls.Add(new Label { Text = "Chatter threshold", Location = new Point(x, y + 4), AutoSize = true });
        _threshold.Location = new Point(x + 120, y);
        _threshold.Width = 70;
        _threshold.Minimum = 1;
        _threshold.Maximum = 100;
        Controls.Add(new Label { Text = "ms", Location = new Point(x + 196, y + 4), AutoSize = true });
        y += 26;

        Controls.Add(new Label
        {
            Text = "A press this soon after a release is treated as a hardware fault.",
            Location = new Point(x + 120, y),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        y += 34;

        Controls.Add(new Label { Text = "Drop repair", Location = new Point(x, y + 4), AutoSize = true });
        _releaseDelay.Location = new Point(x + 120, y);
        _releaseDelay.Width = 70;
        _releaseDelay.Minimum = 0;
        _releaseDelay.Maximum = 50;
        Controls.Add(new Label { Text = "ms", Location = new Point(x + 196, y + 4), AutoSize = true });
        y += 26;

        Controls.Add(new Label
        {
            Text = "How long a release is held to see whether the contact only bounced.\n"
                 + "0 switches drop repair off for this profile.",
            Location = new Point(x + 120, y),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        y += 46;

        Controls.Add(new Label { Text = "Hold repair", Location = new Point(x, y + 4), AutoSize = true });
        _holdRepair.Location = new Point(x + 120, y);
        _holdRepair.Width = 70;
        _holdRepair.Minimum = 0;
        _holdRepair.Maximum = 500;
        Controls.Add(new Label { Text = "ms", Location = new Point(x + 196, y + 4), AutoSize = true });
        y += 26;

        Controls.Add(new Label
        {
            Text = "The same, once a press has been held longer than a click (150 ms).\n"
                 + "Covers long breaks while dragging or holding. 0 switches it off.",
            Location = new Point(x + 120, y),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        y += 46;

        Controls.Add(new Label { Text = "Buttons", Location = new Point(x, y + 2), AutoSize = true });
        int column = x + 120;
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i] = new CheckBox
            {
                Text = ClickStatistics.ButtonName((MouseButton)i),
                Location = new Point(column, y),
                AutoSize = true,
            };
            Controls.Add(_buttons[i]);
            column += 84;
        }
        y += 34;

        _fallbackNote.Location = new Point(x, y);
        _fallbackNote.AutoSize = true;
        _fallbackNote.ForeColor = SystemColors.GrayText;

        _notify.Text = "Show a notification the first time a fault is blocked";
        _notify.Location = new Point(16, 468);
        _notify.AutoSize = true;

        var save = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 200, 502),
            Width = 88,
        };
        save.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 104, 502),
            Width = 88,
        };

        Controls.AddRange(
        [
            profilesLabel, _profileList, add, remove,
            _name, _processes, _threshold, _releaseDelay, _holdRepair,
            _fallbackNote, _notify, save, cancel,
        ]);

        AcceptButton = save;
        CancelButton = cancel;
    }

    private static Label Bold(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
    };

    private void ReloadProfileList(int selectedIndex)
    {
        _loading = true;
        _profileList.Items.Clear();

        foreach (var profile in _profiles)
            _profileList.Items.Add(profile.IsFallback ? $"{profile.Name}  (everything else)" : profile.Name);

        _loading = false;

        if (_profiles.Count > 0)
            _profileList.SelectedIndex = Math.Clamp(selectedIndex, 0, _profiles.Count - 1);
    }

    private void OnProfileSelected()
    {
        if (_loading) return;

        StoreCurrentEdits();
        LoadProfile(_profileList.SelectedIndex);
    }

    private void LoadProfile(int index)
    {
        if (index < 0 || index >= _profiles.Count) return;

        _loading = true;
        var profile = _profiles[index];

        _name.Text = profile.Name;
        _processes.Text = string.Join(", ", profile.ProcessNames);
        _threshold.Value = Math.Clamp(profile.ChatterThresholdMs, (int)_threshold.Minimum, (int)_threshold.Maximum);
        _releaseDelay.Value = Math.Clamp(profile.ReleaseDelayMs, (int)_releaseDelay.Minimum, (int)_releaseDelay.Maximum);
        _holdRepair.Value = Math.Clamp(profile.HoldRepairMs, (int)_holdRepair.Minimum, (int)_holdRepair.Maximum);

        for (int i = 0; i < _buttons.Length; i++)
            _buttons[i].Checked = i >= profile.ButtonsEnabled.Length || profile.ButtonsEnabled[i];

        _fallbackNote.Text = profile.IsFallback
            ? "This is the fallback profile: it covers every application not named above."
            : string.Empty;

        _loadedIndex = index;
        _loading = false;
    }

    /// <summary>Writes the controls back into the profile they were loaded from.</summary>
    private void StoreCurrentEdits()
    {
        if (_loadedIndex < 0 || _loadedIndex >= _profiles.Count) return;

        var profile = _profiles[_loadedIndex];
        profile.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Unnamed" : _name.Text.Trim();
        profile.ProcessNames = ParseProcessNames(_processes.Text);
        profile.ChatterThresholdMs = (int)_threshold.Value;
        profile.ReleaseDelayMs = (int)_releaseDelay.Value;
        profile.HoldRepairMs = (int)_holdRepair.Value;

        var buttons = new bool[_buttons.Length];
        for (int i = 0; i < _buttons.Length; i++) buttons[i] = _buttons[i].Checked;
        profile.ButtonsEnabled = buttons;
    }

    private static string[] ParseProcessNames(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return [.. text
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name)];
    }

    private void OnNameChanged()
    {
        if (_loading || _loadedIndex < 0 || _loadedIndex >= _profileList.Items.Count) return;

        _loading = true;
        var profile = _profiles[_loadedIndex];
        _profileList.Items[_loadedIndex] = profile.IsFallback && _processes.Text.Trim().Length == 0
            ? $"{_name.Text}  (everything else)"
            : _name.Text;
        _loading = false;
    }

    private void AddProfile()
    {
        StoreCurrentEdits();

        _profiles.Add(new FilterProfile
        {
            Name = "New profile",
            ProcessNames = ["application"],
            ChatterThresholdMs = 25,
            ReleaseDelayMs = 12,
        });

        _loadedIndex = -1;
        ReloadProfileList(_profiles.Count - 1);
    }

    private void RemoveProfile()
    {
        int index = _profileList.SelectedIndex;
        if (index < 0 || index >= _profiles.Count) return;

        if (_profiles[index].IsFallback)
        {
            MessageBox.Show(
                "The fallback profile cannot be removed: without it every application "
                + "outside the named ones would go unfiltered.",
                "ChatterFix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        _profiles.RemoveAt(index);
        _loadedIndex = -1;
        ReloadProfileList(Math.Max(0, index - 1));
    }

    private void Commit()
    {
        StoreCurrentEdits();

        Result = new AppConfiguration
        {
            Enabled = _originalEnabled,
            StartWithWindows = _originalStartup,
            NotifyOnFirstBlock = _notify.Checked,
            Profiles = _profiles,
        };
    }
}
