#if DEBUG
// Developer switches and the demo mode. Debug builds only; none of this is in Release.
//
// A WinExe has no console, so the diagnostic commands write their output to a file.
// Their output contains device addresses and ids: keep it out of bug reports.

using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;

namespace BudsTray;

static class DevCommands
{
    const string Usage = """
        BudsTray developer switches (Debug builds only)
          --list <outfile>                       devices, endpoints and KS filters
          --meter <seconds> <outfile>            every 100 ms: default output (*) and peak level per set
          --battery <outdir> <outfile>           battery per device; icon PNGs into <outdir>
          --volume <mac> [<0..1>] <outfile>      read or set a set's volume
          --mic on|off <mac> <outfile>           enable or disable a set's Hands-Free endpoints
          --connect|--disconnect <mac> <outfile> one-shot connect or disconnect
          --scan <seconds> <outfile>             pairing candidates in range (nothing is paired)
          --demo                                 run the tray with fake devices
          --menu-shot <png>                      open the menu once, save a screenshot, exit
          --pair-shot <png> <png>                demo pairing dialogs, save screenshots, exit
        <mac> is 12 hex digits without separators.
        """;

    /// Runs a command and returns its exit code, or null to start the tray normally.
    public static int? Run(string[] args)
    {
        if (args.Length == 0) return null;
        string Arg(int i) => i < args.Length ? args[i] : throw new ArgumentException($"{args[0]}: missing argument {i}");
        string Out() => args[^1];
        var inv = CultureInfo.InvariantCulture;
        try
        {
            switch (args[0])
            {
                case "--list":
                {
                    var log = new List<string>();
                    var devices = BtAudio.Enumerate(log);
                    log.Add("--- devices ---");
                    foreach (var d in devices)
                        log.Add($"{d.Label} mac={d.Mac} paired={d.Paired} connected={d.Connected} micDisabled={d.MicDisabled} hfp=[{string.Join(", ", d.HfpEndpoints.Select(e => $"{(e.Capture ? "mic" : "out")}:{e.State}"))}] filters={string.Join(" | ", d.FilterIds)}");
                    File.WriteAllLines(Arg(1), log);
                    return 0;
                }
                case "--meter":
                {
                    using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    double seconds = double.Parse(Arg(1), inv);
                    string outFile = Arg(2);
                    var lines = new List<string>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < seconds)
                    {
                        string def = "?";
                        try { def = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).ID; } catch { }
                        var parts = new List<string>();
                        foreach (var d in BtAudio.Enumerate().Where(d => d.StereoEndpointId is not null))
                        {
                            string peak = "off";
                            if (d.Connected)
                                try { peak = en.GetDevice(d.StereoEndpointId).AudioMeterInformation.MasterPeakValue.ToString("0.000", inv); } catch { }
                            parts.Add($"{d.Mac?[8..]}{(d.StereoEndpointId == def ? "*" : "")}={peak}");
                        }
                        lines.Add($"{sw.ElapsedMilliseconds,6} {string.Join("  ", parts)}");
                        Thread.Sleep(100);
                    }
                    File.WriteAllLines(outFile, lines);
                    return 0;
                }
                case "--battery":
                {
                    string dir = Arg(1), outFile = Arg(2);
                    var lines = new List<string>();
                    foreach (var d in BtAudio.Enumerate().Where(d => d.Mac is not null))
                    {
                        int? level = Battery.Read(d.Mac!);
                        lines.Add($"{d.Label} connected={d.Connected} battery={level?.ToString() ?? "unknown"}");
                        if (level is { } l)
                            using (var icon = TrayContext.MakeBatteryIcon(l, TrayContext.IsLightTaskbar()))
                                icon.ToBitmap().Save(Path.Combine(dir, $"icon-{d.Mac}.png"));
                    }
                    foreach (int l in new[] { 5, 20, 45, 67, 89, 100 })
                        foreach (bool light in new[] { false, true })
                            using (var icon = TrayContext.MakeBatteryIcon(l, light))
                                icon.ToBitmap().Save(Path.Combine(dir, $"icon-sample-{l}-{(light ? "light" : "dark")}.png"));
                    File.WriteAllLines(outFile, lines);
                    return 0;
                }
                case "--volume":
                {
                    var dev = Find(Arg(1));
                    _ = Arg(2);
                    string result = dev?.StereoEndpointId is not { } ep ? "not found"
                        : args.Length >= 4 ? $"set {BtAudio.SetVolume(ep, float.Parse(args[2], inv))}"
                        : $"volume {BtAudio.GetVolume(ep)?.ToString(inv) ?? "inactive"}";
                    File.WriteAllText(Out(), result);
                    return 0;
                }
                case "--mic":
                {
                    bool on = Arg(1) == "on";
                    var dev = Find(Arg(2));
                    var log = new List<string>();
                    log.Add(dev is null ? "not found" : $"failed {BtAudio.SetMicEnabled(dev, on, log)}");
                    File.WriteAllLines(Arg(3), log);
                    return 0;
                }
                case "--connect" or "--disconnect":
                {
                    var dev = Find(Arg(1));
                    var log = new List<string>();
                    log.Add(dev is null ? "not found"
                        : $"accepted by {(args[0] == "--connect" ? BtAudio.Connect(dev, log) : BtAudio.Disconnect(dev, log))} filters");
                    File.WriteAllLines(Arg(2), log);
                    return 0;
                }
                case "--scan":
                {
                    var found = Pairing.ScanAsync(TimeSpan.FromSeconds(double.Parse(Arg(1), inv))).GetAwaiter().GetResult();
                    File.WriteAllLines(Arg(2), found.Select(c => $"{c.Label} rssi={c.Rssi?.ToString() ?? "?"}"));
                    return 0;
                }
                case "--demo":
                    TrayContext.Demo = true;
                    if (args.Length >= 3 && args[1] == "--menu-shot") TrayContext.MenuShotPath = args[2];
                    return null;
                case "--menu-shot":
                    TrayContext.MenuShotPath = Arg(1);
                    TrayContext.Demo = args.Contains("--demo");
                    return null;
                case "--pair-shot":
                    TrayContext.Demo = true;
                    TrayContext.PairShotPaths = new Queue<string>(new[] { Arg(1), Arg(2) });
                    return null;
                case "--help" or "-h" or "/?":
                    MessageBox.Show(Usage, "BudsTray");
                    return 0;
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{ex.Message}\n\n{Usage}", "BudsTray");
            return 1;
        }
    }

    static BtAudioDevice? Find(string mac) =>
        BtAudio.Enumerate().FirstOrDefault(d => d.Mac == mac.Replace(":", "").ToLowerInvariant());
}

/// Fake devices for the demo mode, used for README screenshots. The addresses are made up.
static class DemoData
{
    public static string PlayingId = "demo-endpoint-a";

    public static List<BtAudioDevice> Devices() => new()
    {
        Make("Earbuds A", "00112233a1b2", "demo-endpoint-a"),
        Make("Earbuds B", "00112233c3d4", "demo-endpoint-b"),
    };

    public static int? Battery(BtAudioDevice d) => d.Mac == "00112233a1b2" ? 72 : 95;

    public static readonly Pairing.Candidate Candidate = new(null, "Earbuds C", "00:11:22:33:44:55", -48);

    static BtAudioDevice Make(string name, string mac, string endpoint)
    {
        var d = new BtAudioDevice
        {
            ContainerId = Guid.NewGuid(), Name = name, Mac = mac,
            Connected = true, Paired = true, StereoEndpointId = endpoint,
        };
        d.HfpEndpoints.Add(($"{endpoint}-mic", Native.DEVICE_STATE_ACTIVE, true));
        return d;
    }
}

sealed partial class TrayContext
{
    internal static string? MenuShotPath;
    internal static Queue<string>? PairShotPaths;

    partial void OnStarted()
    {
        if (MenuShotPath is not null) ScheduleMenuShot();
        else if (PairShotPaths is not null) SchedulePairShots();
    }

    /// The pairing dialogs of a real pairing, with a fake device; nothing is paired.
    async void DemoPair()
    {
        var c = DemoData.Candidate;
        if (AskPair(c) != DialogResult.Yes) return;
        await Task.Delay(300);
        bool ok = ConfirmPairingCode(c.Label, DevicePairingKinds.ConfirmPinMatch, "123456");
        Notify(ok ? $"{c.Label} paired (demo: nothing was changed)." : "Pairing cancelled.", ToolTipIcon.Info);
    }

    void ScheduleMenuShot()
    {
        var open = new System.Windows.Forms.Timer { Interval = 1500 };
        open.Tick += (_, _) =>
        {
            open.Stop();
            var shot = new System.Windows.Forms.Timer { Interval = 700 };
            shot.Tick += (_, _) =>
            {
                shot.Stop();
                // The open popup menu is a window of class #32768. Only its own rectangle is
                // captured, so nothing else on the screen ends up in the image.
                GetWindowRect(FindWindow("#32768", null), out var r);
                var area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                using var bmp = new Bitmap(area.Width, area.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                bmp.Save(MenuShotPath!);
                EndMenu();
            };
            shot.Start();
            ShowMenu();
            _icon.Visible = false;
            ExitThread();
        };
        open.Start();
    }

    /// Runs the demo pairing; each dialog is saved to the next path once it has been open for
    /// a moment, then answered (first button on the first dialog, last button on the last),
    /// and the app exits.
    void SchedulePairShots()
    {
        Form? seen = null;
        DateTime seenAt = default;
        var poll = new System.Windows.Forms.Timer { Interval = 200 };
        poll.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Visible && f.Text == "BudsTray");
            if (dialog is null) return;
            if (dialog != seen) { seen = dialog; seenAt = DateTime.UtcNow; return; }
            if (DateTime.UtcNow - seenAt < TimeSpan.FromMilliseconds(800)) return;

            SaveWindow(dialog.Handle, PairShotPaths!.Dequeue());
            bool last = PairShotPaths.Count == 0;
            var buttons = Descendants(dialog).OfType<Button>().OrderBy(b => b.Left).ToList();
            (last ? buttons[^1] : buttons[0]).PerformClick();
            seen = null;
            if (last)
            {
                poll.Stop();
                var exit = new System.Windows.Forms.Timer { Interval = 500 };
                exit.Tick += (_, _) => { exit.Stop(); _icon.Visible = false; ExitThread(); };
                exit.Start();
            }
        };
        poll.Start();
        BeginInvokeLater(DemoPair);
    }

    static IEnumerable<Control> Descendants(Control c) =>
        c.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));

    void BeginInvokeLater(Action a)
    {
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    /// Saves a window as PNG, cropped to its visible frame (without the invisible resize border).
    static void SaveWindow(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var outer);
        var frame = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT f, Marshal.SizeOf<RECT>()) == 0 ? f : outer;
        using var full = new Bitmap(outer.Right - outer.Left, outer.Bottom - outer.Top);
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        var crop = new Rectangle(frame.Left - outer.Left, frame.Top - outer.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        using var cropped = full.Clone(crop, full.PixelFormat);
        cropped.Save(path);
    }

    const uint PW_RENDERFULLCONTENT = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] static extern bool EndMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
}
#endif
