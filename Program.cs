using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace BudsTray;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
#if DEBUG
        // Developer command-line switches (see DevCommands.cs); not compiled into Release builds.
        if (DevCommands.Run(args) is { } exitCode) return exitCode;
        string mutexName = TrayContext.Demo ? "BudsTray.Demo" : "BudsTray.SingleInstance";
#else
        const string mutexName = "BudsTray.SingleInstance";
#endif
        using var mutex = new Mutex(true, mutexName, out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
        return 0;
    }
}

sealed partial class TrayContext : ApplicationContext
{
#if DEBUG
    /// Debug-only demo mode: fake devices, nothing on the machine is changed.
    internal static bool Demo;
#else
    static bool Demo => false;
#endif

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BudsTray");
    static readonly string NamesFile = Path.Combine(DataDir, "names.txt");
    static readonly string SettingsFile = Path.Combine(DataDir, "settings.txt");
    static readonly string LogFile = Path.Combine(DataDir, "log.txt");

    readonly NotifyIcon _icon;
    readonly NativeWindow _menuOwner = new(); // hidden window the native menu belongs to
    readonly Dictionary<string, string> _names = LoadNames();
    readonly VolumeKeeper _volume = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly Handover _handover = new();
    bool _showBattery = true;
    string? _shownState; // what the icon currently displays, to redraw only on change
    int _ticks;
    bool _scanning;

    public TrayContext()
    {
        bool light = IsLightTaskbar();
        _icon = new NotifyIcon { Icon = MakeIcon(light), Text = "BudsTray", Visible = true };
        _shownState = $"|{light}";

        bool existingInstall = Directory.Exists(DataDir);
        string? text = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
        var settings = Settings.Parse(text, existingInstall);
        _volume.Enabled = settings.KeepVolume;
        _showBattery = settings.ShowBattery;
        _handover.MirrorEnabled = settings.Mirror;
        if (text is null) SaveSettings(); // so the first-run defaults stay as they are

        _volume.Log = WriteLog;
        _handover.Log = WriteLog;
        if (Demo)
        {
            _handover.Paused = true;
            _handover.MirrorEnabled = true;
        }
        _handover.Evaluate();
        _timer.Tick += (_, _) =>
        {
            if (!Demo)
            {
                try { _volume.Tick(); }
                catch (Exception ex) { WriteLog($"volume: {ex.Message}"); }
                _handover.Evaluate();
            }
            // Earbuds report battery in coarse steps; every 5 s is plenty.
            if (_ticks++ % 5 == 0) UpdateIcon();
        };
        _timer.Start();
        _menuOwner.CreateHandle(new CreateParams { Caption = "BudsTray" });
        _icon.MouseUp += (_, e) => ShowMenu(); // left or right click
        OnStarted();
    }

    /// Debug builds hook the screenshot switches in here.
    partial void OnStarted();

    List<BtAudioDevice> Devices()
    {
#if DEBUG
        if (Demo) return DemoData.Devices();
#endif
        return BtAudio.Enumerate();
    }

    string? PlayingOutput()
    {
#if DEBUG
        if (Demo) return DemoData.PlayingId;
#endif
        return _handover.DefaultOutput();
    }

    static int? BatteryLevel(BtAudioDevice d)
    {
#if DEBUG
        if (Demo) return DemoData.Battery(d);
#endif
        return d.Mac is null ? null : Battery.Read(d.Mac);
    }

    /// Wraps a menu action that changes devices or files; in demo mode it does nothing.
    Action Real(Action action) => () =>
    {
        if (Demo) Notify("Demo mode: nothing was changed.", ToolTipIcon.Info);
        else action();
    };

    void ShowMenu()
    {
        NativeMenu.ApplyTheme(IsLightTaskbar());
        using var menu = new NativeMenu();
        var root = menu.Root;
        List<BtAudioDevice> devices;
        try { devices = Devices(); }
        catch (Exception ex) { devices = new(); menu.Label(root, $"Error: {ex.Message}"); }
        foreach (var d in devices) ApplyName(d);
        string? playing = PlayingOutput();

        if (devices.Count == 0) menu.Label(root, "No paired Bluetooth headphones");

        foreach (var d in devices)
        {
            if (!d.Paired)
            {
                menu.Label(root, $"{d.Label} — not paired");
                continue;
            }
            // The dot marks the set Windows plays to; a second connected set gets the mirror.
            bool isPlaying = d.Connected && d.StereoEndpointId == playing;
            string text = d.Label + (d.Connected && !isPlaying ? (_handover.MirrorEnabled ? "  —  mirrored" : "  —  connected") : "");
            // Clicking the item uses this set; hovering opens its options.
            var sub = menu.Submenu(root, text, click: Real(() => UseDevice(d)), radio: isPlaying);
            menu.Item(sub, "Disable microphone", Real(() => ToggleMic(d)), check: d.MicDisabled, enabled: d.HasHfp);
            menu.Item(sub, "Disconnect", Real(() => BtAudio.Disconnect(d)), enabled: d.Connected);
            menu.Item(sub, "Rename…", Real(() => Rename(d)));
        }

        menu.Separator(root);
        menu.Item(root, _scanning ? "Searching…" : "Pair Headphones", PairNew, enabled: !_scanning);
        menu.Item(root, "Bluetooth settings", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }));
        var settings = menu.Submenu(root, "Settings");
        menu.Item(settings, "Mirror audio", Real(() => { _handover.MirrorEnabled = !_handover.MirrorEnabled; SaveSettings(); _handover.Evaluate(); }), check: _handover.MirrorEnabled);
        menu.Item(settings, "Keep volume", Real(() => { _volume.Enabled = !_volume.Enabled; SaveSettings(); }), check: _volume.Enabled);
        menu.Item(settings, "Show battery", Real(() => { _showBattery = !_showBattery; SaveSettings(); UpdateIcon(); }), check: _showBattery);
        menu.Item(settings, "Start with Windows", Real(() => SetAutostart(!IsAutostart())), check: IsAutostart());
        menu.Separator(root);
        menu.Item(root, "Exit", () => { _handover.Dispose(); _icon.Visible = false; ExitThread(); });

        menu.Show(_menuOwner.Handle);
    }

    void SaveSettings()
    {
        if (Demo) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(SettingsFile, new Settings(_volume.Enabled, _showBattery, _handover.MirrorEnabled).ToLines());
        }
        catch (IOException ex) { WriteLog($"settings: {ex.Message}"); }
    }

    /// Shows the battery of the set Windows plays to (or of the only connected set) in the
    /// icon and its tooltip, and redraws the icon when the taskbar switches light/dark.
    void UpdateIcon()
    {
        try
        {
            var active = BtAudio.Active(Devices(), PlayingOutput());
            int? level = _showBattery && active is not null ? BatteryLevel(active) : null;
            bool light = IsLightTaskbar();
            if (active is not null) ApplyName(active);
            _icon.Text = Trim(active is null ? "BudsTray" : level is null ? active.Label : $"{active.Label} — {level}%");
            string state = $"{level}|{light}";
            if (state == _shownState) return;
            _shownState = state;

            var old = _icon.Icon;
            _icon.Icon = level is null ? MakeIcon(light) : MakeBatteryIcon(level.Value, light);
            old?.Dispose();
        }
        catch (Exception ex) { WriteLog($"battery: {ex.Message}"); }
    }

    /// Whether the taskbar uses the light theme (Settings > Personalization > Colors >
    /// "Choose your default Windows mode"). The tray icon and the menu follow it.
    internal static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    /// Headphones peeking over a black badge that fills most of the icon, with the battery
    /// percentage in white (red at 20% and below). Drawn at the tray's exact pixel size
    /// (16 px at 100% scaling) on a 16 px grid that is scaled by whole pixels on larger
    /// icons, with the digits from the hand-drawn bitmaps in Glyph, so nothing gets blurred.
    internal static Icon MakeBatteryIcon(int level, bool lightTaskbar)
    {
        int size = Math.Max(SystemInformation.SmallIconSize.Width, 16);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawHeadphones(g, size, lightTaskbar ? Color.FromArgb(40, 40, 40) : Color.White);

            int k = Math.Max(1, size / 16);
            int originX = (size - 16 * k) / 2, originY = size - 16 * k;

            g.SmoothingMode = SmoothingMode.None;
            using (var black = new SolidBrush(Color.Black))
            {
                // Badge rows 5..15 with the corner pixels cut for a rounded look.
                g.FillRectangle(black, originX + k, originY + 5 * k, 14 * k, 11 * k);
                g.FillRectangle(black, originX, originY + 6 * k, 16 * k, 9 * k);
            }

            string text = Math.Clamp(level, 0, 100).ToString();
            int width = text.Sum(c => Glyph(c)[0].Length) + (text.Length - 1);
            int x = (16 - width) / 2, y = 6;
            using var brush = new SolidBrush(level <= 20 ? Color.FromArgb(255, 80, 80) : Color.White);
            foreach (char c in text)
            {
                var rows = Glyph(c);
                for (int r = 0; r < rows.Length; r++)
                    for (int col = 0; col < rows[r].Length; col++)
                        if (rows[r][col] == '#')
                            g.FillRectangle(brush, originX + (x + col) * k, originY + (y + r) * k, k, k);
                x += rows[0].Length + 1;
            }
        }
        return ToIcon(bmp);
    }

    /// Hand-made bold 5x9 digits (the "1" is 3 wide so "100" fits in 15 px). Any font
    /// rendered this small either blurs or breaks its diagonals.
    internal static string[] Glyph(char c) => c switch
    {
        '0' => new[] { "#####", "#####", "##.##", "##.##", "##.##", "##.##", "##.##", "#####", "#####" },
        '1' => new[] { ".##", "###", ".##", ".##", ".##", ".##", ".##", ".##", ".##" },
        '2' => new[] { "#####", "#####", "...##", "...##", "#####", "##...", "##...", "#####", "#####" },
        '3' => new[] { "#####", "#####", "...##", "...##", "#####", "...##", "...##", "#####", "#####" },
        '4' => new[] { "##.##", "##.##", "##.##", "##.##", "#####", "...##", "...##", "...##", "...##" },
        '5' => new[] { "#####", "#####", "##...", "##...", "#####", "...##", "...##", "#####", "#####" },
        '6' => new[] { "#####", "#####", "##...", "##...", "#####", "##.##", "##.##", "#####", "#####" },
        '7' => new[] { "#####", "#####", "...##", "...##", "...##", "...##", "...##", "...##", "...##" },
        '8' => new[] { "#####", "#####", "##.##", "##.##", "#####", "##.##", "##.##", "#####", "#####" },
        _ => new[] { "#####", "#####", "##.##", "##.##", "#####", "...##", "...##", "#####", "#####" }, // 9
    };

    /// Over-ear headphones in rows 0..5: the band and the tops of the ear cups, the rest
    /// hidden behind the battery badge.
    static void DrawHeadphones(Graphics g, int size, Color color)
    {
        float s = size;
        using var pen = new Pen(color, Math.Max(1.5f, s * 0.11f));
        g.DrawArc(pen, s * 0.14f, s * 0.06f, s * 0.72f, s * 0.56f, 180, 180);
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, 0, s * 0.19f, s * 0.22f, s * 0.15f);
        g.FillRectangle(brush, s * 0.78f, s * 0.19f, s * 0.22f, s * 0.15f);
    }

    /// The icon shown when no battery level is known: headphones with a band that contrasts
    /// with the taskbar.
    static Icon MakeIcon(bool lightTaskbar)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(lightTaskbar ? Color.FromArgb(40, 40, 40) : Color.White, 3);
            g.DrawArc(pen, 5, 4, 22, 22, 180, 180);
            using var brush = new SolidBrush(Color.FromArgb(0, 120, 215));
            g.FillRectangle(brush, 3, 15, 7, 12);
            g.FillRectangle(brush, 22, 15, 7, 12);
        }
        return ToIcon(bmp);
    }

    static Icon ToIcon(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    void ToggleMic(BtAudioDevice d)
    {
        bool enable = d.MicDisabled;
        int failed = BtAudio.SetMicEnabled(d, enable);
        if (failed > 0) Notify($"Could not change the microphone setting on {failed} endpoint(s).", ToolTipIcon.Warning);
        else Notify(enable ? $"{d.Label}: microphone enabled." : $"{d.Label}: microphone disabled, apps can't use it.", ToolTipIcon.Info);
    }

    async void PairNew()
    {
#if DEBUG
        if (Demo) { DemoPair(); return; }
#endif
        _scanning = true;
        try
        {
            Notify("Searching for headphones (20 s)… Put them in pairing mode.", ToolTipIcon.Info);
            var candidates = await Pairing.ScanAsync(TimeSpan.FromSeconds(20));
            if (candidates.Count == 0)
            {
                Notify("No audio device in pairing mode was found.", ToolTipIcon.Warning);
                return;
            }
            // Strongest signal first: the earbuds in your hand are normally the closest device.
            foreach (var c in candidates)
            {
                var answer = AskPair(c);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.No) continue;
                var ui = SynchronizationContext.Current!;
                var status = await Pairing.PairAsync(c, (kind, pin) => OnUiThread(ui, () => ConfirmPairingCode(c.Label, kind, pin)));
                Notify(status == DevicePairingResultStatus.Paired
                    ? $"{c.Label} paired." : $"Pairing failed: {status}", ToolTipIcon.Info);
                return;
            }
        }
        catch (Exception ex) { Notify($"Error: {ex.Message}", ToolTipIcon.Error); }
        finally { _scanning = false; }
    }

    /// Yes: pair this one, No: show the next candidate, Cancel: stop.
    static DialogResult AskPair(Pairing.Candidate c) =>
        Dialogs.Ask($"Pair this device?\n\n{c.Label}\nSignal: {c.Rssi?.ToString() ?? "?"} dBm",
            new[] { "Pair", "Next device", "Cancel" }) switch
        {
            0 => DialogResult.Yes,
            1 => DialogResult.No,
            _ => DialogResult.Cancel,
        };

    /// Shows the PIN or comparison code of a pairing request; true only if the user confirms.
    static bool ConfirmPairingCode(string device, DevicePairingKinds kind, string pin) => kind switch
    {
        DevicePairingKinds.ConfirmPinMatch => Dialogs.Ask(
            $"Does {device} show this code?\n\n{pin}\n\nPair only if the codes match.",
            new[] { "Codes match", "Cancel" }, defaultButton: 1) == 0,
        DevicePairingKinds.DisplayPin => Dialogs.Ask(
            $"Enter this PIN on {device}:\n\n{pin}",
            new[] { "OK", "Cancel" }, icon: SystemIcons.Information) == 0,
        _ => false,
    };

    static Task<T> OnUiThread<T>(SynchronizationContext ui, Func<T> f)
    {
        var done = new TaskCompletionSource<T>();
        ui.Post(_ =>
        {
            try { done.SetResult(f()); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    /// Plays to this set: connects it if needed and makes it Windows' output. The other set
    /// stays connected (and gets the mirror, if enabled) until it is put away.
    async void UseDevice(BtAudioDevice target)
    {
        try
        {
            var current = BtAudio.Enumerate().FirstOrDefault(d => d.ContainerId == target.ContainerId);
            if (current is not { Connected: true })
            {
                if (BtAudio.Connect(target) == 0)
                {
                    Notify($"{target.Label}: Windows rejected the connect request.", ToolTipIcon.Warning);
                    return;
                }
                _icon.Text = Trim($"Connecting: {target.Label}");
                // The request is asynchronous; poll the endpoint state for a while.
                for (int i = 0; i < 12 && current is not { Connected: true }; i++)
                {
                    await Task.Delay(1000);
                    current = BtAudio.Enumerate().FirstOrDefault(d => d.ContainerId == target.ContainerId);
                }
                if (current is not { Connected: true })
                {
                    Notify($"Could not connect {target.Label}. Are the earbuds out of the case and not connected to another device?", ToolTipIcon.Warning);
                    return;
                }
            }
            if (current.StereoEndpointId is { } id && id != _handover.DefaultOutput())
            {
                Handover.SetDefault(id);
                _handover.Evaluate();
            }
        }
        catch (Exception ex)
        {
            Notify($"Error: {ex.Message}", ToolTipIcon.Error);
        }
        finally { UpdateIcon(); }
    }

    static void WriteLog(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 64_000) File.Delete(LogFile);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    void Notify(string text, ToolTipIcon kind) => _icon.ShowBalloonTip(3000, "BudsTray", text, kind);

    // NotifyIcon.Text is limited to 127 characters; keep tooltips short.
    static string Trim(string s) => s.Length > 50 ? s[..50] : s;

    void ApplyName(BtAudioDevice d)
    {
        if (d.Mac is not null && _names.TryGetValue(d.Mac, out var name)) d.CustomName = name;
    }

    void Rename(BtAudioDevice d)
    {
        if (d.Mac is null) return;
        string input = Microsoft.VisualBasic.Interaction.InputBox("Name for these headphones:", "BudsTray", d.Label);
        if (string.IsNullOrWhiteSpace(input)) return;
        _names[d.Mac] = input.Trim();
        Directory.CreateDirectory(DataDir);
        File.WriteAllLines(NamesFile, _names.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    static Dictionary<string, string> LoadNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(NamesFile)) return names;
        foreach (var line in File.ReadAllLines(NamesFile))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) names[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return names;
    }

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("BudsTray") is string;
    }

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue("BudsTray", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("BudsTray", false);
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
