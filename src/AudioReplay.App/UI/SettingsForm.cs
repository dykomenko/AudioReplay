using AudioReplay.Core.Config;
using AudioReplay.Core.Input;

namespace AudioReplay.App.UI;

/// <summary>The only window of the app: a flat list of options, OK / Cancel. Built in code, no designer.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _original;
    private readonly TextBox _folder = new();
    private readonly NumericUpDown _seconds = new() { Minimum = AppSettings.MinReplaySeconds, Maximum = AppSettings.MaxReplaySeconds };
    private readonly Label _memoryHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly NumericUpDown _merge = new() { Minimum = 0, Maximum = AppSettings.MaxReplaySeconds };
    private readonly Label _mergeHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0), Text = "Нажали хоткей снова в течение этого времени: звук после прошлого сохранения допишется в тот же клип. 0 — не склеивать." };
    private readonly CheckBox _bufferOn = new() { Text = "Держать буфер отката в памяти", AutoSize = true };
    private readonly HotkeyBox _replayKey = new();
    private readonly HotkeyBox _recordKey = new();
    private readonly CheckBox _micOn = new() { Text = "Записывать микрофон отдельной дорожкой", AutoSize = true };
    private readonly ComboBox _micDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _mix = new() { Text = "Дополнительно сохранять общий микс (mix.wav)", AutoSize = true };
    private readonly CheckBox _tray = new() { Text = "Показывать значок в трее", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "Показывать уведомления о сохранении", AutoSize = true };
    private readonly CheckBox _sound = new() { Text = "Короткий звук при сохранении и записи", AutoSize = true };
    private readonly CheckBox _streamer = new() { Text = "Режим стримера: без уведомлений, пока запущена OBS (звук остаётся)", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Запускать вместе с Windows", AutoSize = true };
    private readonly TextBox _excluded = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 60, PlaceholderText = "по одному имени в строке, например: game.exe" };

    public SettingsForm(AppSettings current, IReadOnlyList<(string Id, string Name)> microphones)
    {
        _original = current;

        Text = "AudioReplay — настройки";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Icon = Platform.TrayIcons.Create(recording: false);

        _micDevice.Items.Add(new MicItem(string.Empty, "Устройство по умолчанию"));
        foreach (var (id, name) in microphones) _micDevice.Items.Add(new MicItem(id, name));

        LoadValues(current);
        BuildLayout();

        _seconds.ValueChanged += (_, _) => UpdateHint();
        _micOn.CheckedChanged += (_, _) => _micDevice.Enabled = _micOn.Checked;
        _bufferOn.CheckedChanged += (_, _) => { _seconds.Enabled = _merge.Enabled = _replayKey.Enabled = _bufferOn.Checked; };
        _micDevice.Enabled = _micOn.Checked;
        _seconds.Enabled = _merge.Enabled = _replayKey.Enabled = _bufferOn.Checked;
        UpdateHint();
    }

    /// <summary>The edited settings; valid only after the dialog returned <see cref="DialogResult.OK"/>.</summary>
    public AppSettings Result { get; private set; } = new();

    private void LoadValues(AppSettings s)
    {
        _folder.Text = s.SaveDirectory;
        _seconds.Value = Math.Clamp(s.ReplaySeconds, AppSettings.MinReplaySeconds, AppSettings.MaxReplaySeconds);
        _merge.Value = Math.Clamp(s.MergeWindowSeconds, 0, AppSettings.MaxReplaySeconds);
        _bufferOn.Checked = s.ReplayBufferEnabled;
        _replayKey.Value = s.ReplayHotkeyValue;
        _recordKey.Value = s.RecordHotkeyValue;
        _micOn.Checked = s.CaptureMicrophone;
        _mix.Checked = s.WriteMixTrack;
        _tray.Checked = s.ShowTrayIcon;
        _notify.Checked = s.ShowNotifications;
        _sound.Checked = s.PlaySound;
        _streamer.Checked = s.StreamerMode;
        _autostart.Checked = s.StartWithWindows;
        _excluded.Text = string.Join(Environment.NewLine, s.ExcludedApps);

        var selected = _micDevice.Items.Cast<MicItem>().FirstOrDefault(m => m.Id == s.MicrophoneDeviceId);
        _micDevice.SelectedItem = selected ?? _micDevice.Items[0];
    }

    private void BuildLayout()
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(14),
            MinimumSize = new Size(480, 0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(string label, Control control)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(0, 3, 0, 3);
            table.Controls.Add(control);
        }

        void Wide(Control control)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 3, 0, 3);
            table.Controls.Add(control);
            table.SetColumnSpan(control, 2);
        }

        var browse = new Button { Text = "…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        browse.Click += (_, _) => BrowseFolder();
        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folder.Dock = DockStyle.Fill;
        folderRow.Controls.Add(_folder);
        folderRow.Controls.Add(browse);

        Row("Папка для записей", folderRow);
        Wide(_bufferOn);
        Row("Длина отката, сек", _seconds);
        Wide(_memoryHint);
        Row("Склейка повторных нажатий, сек", _merge);
        Wide(_mergeHint);
        Row("Хоткей: сохранить откат", _replayKey);
        Row("Хоткей: запись вкл/выкл", _recordKey);
        Wide(_micOn);
        Row("Микрофон", _micDevice);
        Wide(_mix);
        Wide(_tray);
        Wide(_notify);
        Wide(_sound);
        Wide(_streamer);
        Wide(_autostart);
        Row("Не записывать приложения", _excluded);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, AutoSize = true, MinimumSize = new Size(88, 0) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 0) };
        ok.Click += (_, _) => TryAccept();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Wide(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(table);
    }

    private void UpdateHint() =>
        _memoryHint.Text = $"≈ {(double)_seconds.Value * 0.192:0} МБ памяти на каждое приложение, которое играет звук (тишина не занимает места)";

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Куда сохранять записи" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private void TryAccept()
    {
        if (string.IsNullOrWhiteSpace(_folder.Text))
        {
            Warn("Укажите папку для записей.");
            return;
        }

        if (!_replayKey.Value.IsValid || !_recordKey.Value.IsValid)
        {
            Warn("Задайте оба хоткея: щёлкните в поле и нажмите нужное сочетание.");
            return;
        }

        if (_replayKey.Value == _recordKey.Value)
        {
            Warn("Хоткеи должны отличаться.");
            return;
        }

        var s = _original.Clone();
        s.SaveDirectory = _folder.Text.Trim();
        s.ReplayBufferEnabled = _bufferOn.Checked;
        s.ReplaySeconds = (int)_seconds.Value;
        s.MergeWindowSeconds = (int)_merge.Value;
        s.ReplayHotkey = _replayKey.Value.ToString();
        s.RecordHotkey = _recordKey.Value.ToString();
        s.CaptureMicrophone = _micOn.Checked;
        s.MicrophoneDeviceId = (_micDevice.SelectedItem as MicItem)?.Id ?? string.Empty;
        s.WriteMixTrack = _mix.Checked;
        s.ShowTrayIcon = _tray.Checked;
        s.ShowNotifications = _notify.Checked;
        s.PlaySound = _sound.Checked;
        s.StreamerMode = _streamer.Checked;
        s.StartWithWindows = _autostart.Checked;
        s.ExcludedApps = [.. _excluded.Lines];
        s.Normalize();

        Result = s;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private sealed record MicItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
