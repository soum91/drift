using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Win32;

namespace Drift.Windows;

internal sealed class MainForm : Form, IDriftUi
{
    private static readonly Color Back = Color.FromArgb(15, 20, 31);
    private static readonly Color Panel = Color.FromArgb(27, 36, 50);
    private static readonly Color Ink = Color.FromArgb(225, 239, 244);
    private static readonly Color Mint = Color.FromArgb(111, 232, 185);
    private readonly DriftService _service;
    private readonly DriftBluetoothReceiver _bluetooth;
    private readonly DriftSender _sender = new();
    private readonly ComboBox _interface = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _code = new() { AutoSize = true, ForeColor = Mint, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
    private readonly System.Windows.Forms.Timer _pairingCodeTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _receiverHealthTimer = new() { Interval = 15000 };
    private readonly Button _listen = new() { Text = "Start receiving", Width = 154 };
    private readonly TextBox _peerIp = new() { Width = 175, PlaceholderText = "192.168.1.20" };
    private readonly TextBox _peerCode = new() { Width = 104, MaxLength = 6, PlaceholderText = "6-digit code" };
    private readonly Button _pair = new() { Text = "Pair", Width = 94 };
    private readonly Button _send = new() { Text = "Send file to receiver", Width = 210 };
    private readonly Button _queue = new() { Text = "Share file with iPhone", Width = 210 };
    private readonly CheckBox _shareClipboard = new() { Text = "Let paired iPhone read Windows clipboard", AutoSize = true };
    private readonly CheckBox _receiveClipboard = new() { Text = "Accept clipboard copied from paired iPhone", AutoSize = true };
    private readonly CheckBox _autoReceive = new() { Text = "Start receiving automatically", AutoSize = true, Checked = true };
    private readonly CheckBox _remember = new() { Text = "Remember paired iPhone for reconnect", AutoSize = true, Checked = true };
    private readonly CheckBox _bluetoothEnabled = new() { Text = "Receive small files from iPhone over Bluetooth LE", AutoSize = true, Checked = true };
    private readonly Button _bluetoothButton = new() { Text = "Start Bluetooth", Width = 164 };
    private readonly CheckBox _startWithWindows = new() { Text = "Launch Drift when I sign in to Windows", AutoSize = true };
    private readonly ProgressBar _progress = new() { Width = 520, Height = 18, Minimum = 0, Maximum = 100 };
    private readonly RichTextBox _log = new() { ReadOnly = true, BackColor = Back, ForeColor = Ink,
                                                 BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
                                                 Font = new Font("Consolas", 9.5f), DetectUrls = false };
    private readonly NotifyIcon _tray;
    private bool _allowClipboardRead;
    private bool _allowClipboardWrite;
    private bool _reallyExit;
    private bool _receiverRecoveryRunning;
    private string? _preferredIp;

    private sealed record InterfaceOption(IPAddress Address, string Name)
    {
        public override string ToString() => $"{Name}: {Address}";
    }
    private sealed record Preferences(bool AutoReceive, bool Remember, string? Ip, bool? Bluetooth);
    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Drift", "windows-settings.json");

    public bool ClipboardSharingEnabled => _allowClipboardRead;
    public bool ClipboardReceivingEnabled => _allowClipboardWrite;

    public MainForm()
    {
        _service = new DriftService(this);
        _bluetooth = new DriftBluetoothReceiver(this);
        LoadPreferences();
        Text = "Drift | Windows Companion";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(690, 700);
        Size = new Size(800, 820);
        BackColor = Back;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
                                          RowCount = 2, Padding = new Padding(20, 16, 20, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        Controls.Add(root);

        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                                            WrapContents = false, AutoScroll = true };
        root.Controls.Add(content, 0, 0);
        content.Resize += (_, _) =>
        {
            foreach (Control child in content.Controls) child.Width = Math.Max(580, content.ClientSize.Width - 26);
        };

        var heading = new Panel { Height = 80 };
        heading.Controls.Add(new Label { Text = "◈  DRIFT", Font = new Font("Segoe UI", 27, FontStyle.Bold),
                                          ForeColor = Mint, Location = new Point(10, 3), AutoSize = true });
        heading.Controls.Add(new Label { Text = "Windows ↔ iPhone ↔ Android TV  •  local transfer, no cloud",
                                          ForeColor = Ink, Location = new Point(12, 53), AutoSize = true });
        content.Controls.Add(heading);

        var receive = Group("iPhone → Windows (pair on the iPhone)", 193);
        var receiveRow = NewRow(20, 38);
        receiveRow.Controls.Add(Styled(new Label { Text = "Windows LAN IP:", Width = 134, TextAlign = ContentAlignment.MiddleLeft }));
        receiveRow.Controls.Add(_interface);
        var refresh = Button("Refresh IPs", 100);
        refresh.Click += (_, _) => RefreshAddresses();
        receiveRow.Controls.Add(refresh);
        receive.Controls.Add(receiveRow);
        var receiverRow = NewRow(20, 82);
        Style(_listen); _listen.Click += async (_, _) => await ToggleReceiving();
        receiverRow.Controls.Add(_listen);
        receiverRow.Controls.Add(Styled(new Label { Text = "Pairing code:", Width = 112, TextAlign = ContentAlignment.MiddleLeft }));
        receiverRow.Controls.Add(_code);
        receive.Controls.Add(receiverRow);
        var savedRow = NewRow(20, 126);
        _autoReceive.ForeColor = Ink; _remember.ForeColor = Ink;
        _autoReceive.CheckedChanged += (_, _) => SavePreferences();
        _remember.CheckedChanged += (_, _) => { _service.RememberPairedDevices = _remember.Checked; SavePreferences(); };
        savedRow.Controls.Add(_autoReceive);
        savedRow.Controls.Add(_remember);
        receive.Controls.Add(savedRow);
        _service.RememberPairedDevices = _remember.Checked;
        content.Controls.Add(receive);

        var share = Group("Send Windows → Android TV (optional)", 187);
        var peerRow = NewRow(20, 30);
        peerRow.Controls.Add(Styled(new Label { Text = "TV / Drift IP:", Width = 118, TextAlign = ContentAlignment.MiddleLeft }));
        peerRow.Controls.Add(_peerIp);
        peerRow.Controls.Add(_peerCode);
        Style(_pair); _pair.Click += async (_, _) => await PairToReceiver();
        peerRow.Controls.Add(_pair);
        share.Controls.Add(peerRow);

        var fileRow = NewRow(20, 80);
        Style(_send); _send.Click += async (_, _) => await SendFile();
        Style(_queue); _queue.Click += (_, _) => QueueFile();
        fileRow.Controls.Add(_send);
        fileRow.Controls.Add(_queue);
        share.Controls.Add(fileRow);
        var progressRow = NewRow(20, 126);
        progressRow.Controls.Add(_progress);
        share.Controls.Add(progressRow);
        var sendNote = Styled(new Label { Text = "No Windows Pair needed for iPhone → PC. Use Pair on the iPhone instead.",
                                         Location = new Point(23, 158), Width = 620, AutoSize = false, Height = 25,
                                         ForeColor = Mint });
        share.Controls.Add(sendNote);
        content.Controls.Add(share);

        var keep = Group("Keep connected", 100);
        _startWithWindows.ForeColor = Ink;
        _startWithWindows.Checked = IsLaunchAtLoginEnabled();
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            try { SetLaunchAtLogin(_startWithWindows.Checked); }
            catch (Exception error) { Error(error); _startWithWindows.Checked = IsLaunchAtLoginEnabled(); }
        };
        var keepRow = NewRow(18, 33);
        keepRow.Controls.Add(_startWithWindows);
        var revoke = Button("Forget trusted phones", 186);
        revoke.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Revoke every remembered iPhone? You will need to pair again.", "Forget devices",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _service.RevokeTrustedDevices();
        };
        keepRow.Controls.Add(revoke);
        keep.Controls.Add(keepRow);
        content.Controls.Add(keep);

        var ble = Group("Bluetooth LE receiver (small files up to 8 MiB)", 108);
        var bleRow = NewRow(18, 34);
        _bluetoothEnabled.ForeColor = Ink;
        _bluetoothEnabled.CheckedChanged += (_, _) => SavePreferences();
        bleRow.Controls.Add(_bluetoothEnabled);
        Style(_bluetoothButton);
        _bluetoothButton.Click += async (_, _) => await ToggleBluetoothAsync();
        bleRow.Controls.Add(_bluetoothButton);
        ble.Controls.Add(bleRow);
        var bleHelp = Styled(new Label { Text = "On iPhone: Bluetooth → Find nearby Drift receivers. Requires Windows BLE peripheral support.",
            Location = new Point(20, 77), Width = 660, Height = 22, AutoEllipsis = true });
        ble.Controls.Add(bleHelp);
        content.Controls.Add(ble);

        var clip = Group("Pasteboard sync (opt-in)", 132);
        var clipRow = NewRow(15, 32);
        _shareClipboard.ForeColor = Ink;
        _receiveClipboard.ForeColor = Ink;
        _shareClipboard.CheckedChanged += (_, _) => _allowClipboardRead = _shareClipboard.Checked;
        _receiveClipboard.CheckedChanged += (_, _) => _allowClipboardWrite = _receiveClipboard.Checked;
        clipRow.Controls.Add(_shareClipboard);
        var clipRow2 = NewRow(15, 63);
        clipRow2.Controls.Add(_receiveClipboard);
        var note = Styled(new Label { Text = "Sync works with Drift on iPhone while the app is open. Windows clipboard is never published unless enabled.",
                                      Width = 610, Height = 28, ForeColor = Mint, AutoEllipsis = true });
        clipRow2.Controls.Add(note);
        clip.Controls.Add(clipRow);
        clip.Controls.Add(clipRow2);
        content.Controls.Add(clip);

        var air = Group("Native AirDrop (experimental, separate engine)", 116);
        var airRow = NewRow(16, 30);
        var pulse = Button("PulseIt setup / research", 230);
        pulse.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/gsl0001/windows-airdrop") { UseShellExecute = true });
        airRow.Controls.Add(pulse);
        air.Controls.Add(airRow);
        var caution = Styled(new Label { Text = "Needs a specific TP-Link Archer T3U (2357:012d) USB adapter, admin driver setup, and separate PulseIt build.",
                                          Left = 18, Top = 77, Width = 650, Height = 26, AutoEllipsis = true });
        air.Controls.Add(caution);
        content.Controls.Add(air);

        var logPanel = Group("Activity", 0);
        logPanel.Dock = DockStyle.Fill;
        logPanel.Padding = new Padding(12, 28, 12, 12);
        logPanel.Controls.Add(_log);
        root.Controls.Add(logPanel, 0, 1);

        _tray = new NotifyIcon { Icon = Icon, Text = "Drift Windows Companion", Visible = true };
        _pairingCodeTimer.Tick += (_, _) =>
        {
            if (!_service.Running) return;
            var current = _service.PairingCode;
            if (_code.Text == current) return;
            _code.Text = current;
            Report("Expired pairing code refreshed. Trusted iPhones continue reconnecting without a code.");
        };
        _pairingCodeTimer.Start();
        _receiverHealthTimer.Tick += async (_, _) => await EnsureReceiverHealthyAsync();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Drift", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Exit Drift", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += (_, e) =>
        {
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            { e.Cancel = true; Hide(); _tray.ShowBalloonTip(1700, "Drift still running", "Use the tray menu to quit.", ToolTipIcon.Info); }
        };
        FormClosed += (_, _) =>
        {
            _receiverHealthTimer.Stop();
            _receiverHealthTimer.Dispose();
            _pairingCodeTimer.Stop();
            _pairingCodeTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _sender.Dispose();
            _bluetooth.Stop();
            _service.StopAsync().GetAwaiter().GetResult();
        };
        RefreshAddresses();
        Shown += async (_, _) =>
        {
            if (_autoReceive.Checked) await ToggleReceiving();
            _receiverHealthTimer.Start();
            if (_bluetoothEnabled.Checked) await ToggleBluetoothAsync();
        };
        Report("iPhone → Windows: use the phone's Pair button, not Windows Pair. Auto-receiving and Bonjour discovery are supported.");
    }

    private static GroupBox Group(string title, int height) => new()
    {
        Text = title, Height = height, Width = 710, BackColor = Panel, ForeColor = Mint,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 13),
        Padding = new Padding(14)
    };

    private static FlowLayoutPanel NewRow(int left, int top) => new()
    {
        Location = new Point(left, top), Width = 660, Height = 42, FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false, BackColor = Color.Transparent
    };

    private static Control Styled(Control control)
    {
        control.ForeColor = Ink;
        control.Margin = new Padding(3, 6, 7, 3);
        return control;
    }

    private static Button Button(string text, int width)
    {
        var button = new Button { Text = text, Width = width };
        Style(button);
        return button;
    }

    private static void Style(Button button)
    {
        button.Height = 34;
        button.BackColor = Color.FromArgb(49, 74, 72);
        button.ForeColor = Mint;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Mint;
        button.Margin = new Padding(3, 3, 8, 3);
    }

    private void RefreshAddresses()
    {
        _interface.Items.Clear();
        foreach (var (addr, name) in DriftService.LocalInterfaces()) _interface.Items.Add(new InterfaceOption(addr, name));
        if (_interface.Items.Count > 0)
        {
            // Always default to the best physical Wi-Fi/Ethernet adapter. A previously
            // saved Hyper-V or mobile-hotspot address may no longer reach the iPhone.
            _interface.SelectedIndex = 0;
        }
        else Report("No private IPv4 network found. Connect to Wi-Fi or a Windows Wi-Fi Direct group.");
    }

    private async Task ToggleReceiving()
    {
        _listen.Enabled = false;
        try
        {
            if (_service.Running)
            {
                await _service.StopAsync();
                // Explicit Stop receiving means stop, including the watchdog.
                _autoReceive.Checked = false;
                _listen.Text = "Start receiving";
                _code.Text = "";
            }
            else
            {
                if (_interface.SelectedItem is not InterfaceOption selected)
                    throw new InvalidOperationException("Select a LAN IPv4 address first");
                await _service.StartAsync(selected.Address);
                _preferredIp = selected.Address.ToString();
                SavePreferences();
                _listen.Text = "Stop receiving";
                _code.Text = _service.PairingCode;
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { _listen.Enabled = true; }
    }

    private async Task EnsureReceiverHealthyAsync()
    {
        if (!_autoReceive.Checked || _receiverRecoveryRunning || !_listen.Enabled) return;
        _receiverRecoveryRunning = true;
        try
        {
            if (_service.ListeningOnNetwork) return;
            Report(_service.Running
                ? "Receiver socket or network interface disappeared. Restarting the listener…"
                : "Automatic receiver was stopped. Starting it again…");
            if (_service.Running) await _service.StopAsync();
            RefreshAddresses();
            if (_interface.SelectedItem is not InterfaceOption selected)
            {
                Report("Waiting for a private Wi-Fi or Ethernet address before restarting the receiver.");
                return;
            }
            await _service.StartAsync(selected.Address);
            _preferredIp = selected.Address.ToString();
            SavePreferences();
            _listen.Text = "Stop receiving";
            _code.Text = _service.PairingCode;
            Report("Receiver recovered automatically on " + selected.Address + ":" + DriftService.Port);
        }
        catch (Exception ex)
        {
            _listen.Text = "Start receiving";
            _code.Text = "";
            Report("Receiver recovery failed: " + ex.Message + ". Retrying in 15 seconds.");
        }
        finally { _receiverRecoveryRunning = false; }
    }

    private async Task ToggleBluetoothAsync()
    {
        _bluetoothButton.Enabled = false;
        try
        {
            if (_bluetooth.Running)
            {
                _bluetooth.Stop();
                _bluetoothButton.Text = "Start Bluetooth";
            }
            else
            {
                await _bluetooth.StartAsync();
                _bluetoothButton.Text = "Stop Bluetooth";
            }
        }
        catch (Exception error)
        {
            Report("Bluetooth unavailable: " + error.Message);
            _bluetoothButton.Text = "Start Bluetooth";
            // Allow Wi-Fi transfers even when this PC cannot advertise BLE.
        }
        finally { _bluetoothButton.Enabled = true; }
    }

    private void LoadPreferences()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath));
            if (saved is null) return;
            _autoReceive.Checked = saved.AutoReceive;
            _remember.Checked = saved.Remember;
            _bluetoothEnabled.Checked = saved.Bluetooth ?? true;
            _preferredIp = saved.Ip;
        }
        catch (IOException) { }
        catch (JsonException) { }
    }

    private void SavePreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new Preferences(
            _autoReceive.Checked, _remember.Checked, _preferredIp, _bluetoothEnabled.Checked)));
    }

    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool IsLaunchAtLoginEnabled()
    {
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryRunKey);
        return registry?.GetValue("DriftWindows") is string;
    }

    private static void SetLaunchAtLogin(bool enabled)
    {
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
        if (enabled) registry?.SetValue("DriftWindows", $"\"{Application.ExecutablePath}\"");
        else registry?.DeleteValue("DriftWindows", throwOnMissingValue: false);
    }

    private async Task PairToReceiver()
    {
        _pair.Enabled = false;
        try { await _sender.PairAsync(_peerIp.Text, _peerCode.Text); Report("Paired with receiver " + _peerIp.Text); }
        catch (Exception ex) { Error(ex); }
        finally { _pair.Enabled = true; }
    }

    private async Task SendFile()
    {
        using var dialog = new OpenFileDialog { Title = "Choose file to send to Drift receiver" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _send.Enabled = false;
        _progress.Value = 0;
        try
        {
            Report("Waiting for receiver approval: " + Path.GetFileName(dialog.FileName));
            var progress = new Progress<double>(value => _progress.Value = Math.Clamp((int)(value * 100), 0, 100));
            await _sender.SendFileAsync(dialog.FileName, progress);
            Report("Sent successfully: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { Error(ex); }
        finally { _send.Enabled = true; }
    }

    private void QueueFile()
    {
        if (!_service.Running) { Report("Start receiving before sharing a file with iPhone"); return; }
        using var dialog = new OpenFileDialog { Title = "Choose file for iPhone to download" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _service.QueueForIPhone(dialog.FileName); }
        catch (Exception ex) { Error(ex); }
    }

    private void Error(Exception ex)
    {
        Report("ERROR: " + ex.Message);
        MessageBox.Show(this, ex.Message, "Drift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public void Report(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => Report(message))); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    public Task<bool> ApproveFileAsync(string name, long bytes) => OnUi(() =>
        MessageBox.Show(this, $"Accept {name} ({bytes / 1024.0 / 1024.0:N2} MiB) from a paired Drift device?",
                        "Incoming Drift file", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes);

    public Task SetClipboardAsync(string text) => OnUi(() =>
    { if (!ClipboardReceivingEnabled) throw new InvalidOperationException("Clipboard receive was disabled");
      if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text);
      return true; });

    public Task<string> GetClipboardAsync() => OnUi(() =>
    { if (!ClipboardSharingEnabled) throw new InvalidOperationException("Clipboard sharing was disabled"); return Clipboard.ContainsText() ? Clipboard.GetText() : ""; });

    private Task<T> OnUi<T>(Func<T> callback)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated) { result.SetException(new InvalidOperationException("Drift window closed")); return result.Task; }
        BeginInvoke((Action)(() =>
        {
            try { result.TrySetResult(callback()); }
            catch (Exception ex) { result.TrySetException(ex); }
        }));
        return result.Task;
    }
}
