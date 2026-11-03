using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BudsTray;

/// Makes swapping sets seamless:
///  - with MirrorEnabled, while two sets are connected, the one Windows is playing to is
///    mirrored to the other, so the fresh set already plays the moment it is out of the case;
///  - when the set Windows was playing to goes away, the remaining set becomes the default
///    device at once (instead of Windows falling back to speakers);
///  - a set that connects while no set was connected becomes the default device.
/// Device changes are reported by Windows immediately; the tray timer is only a fallback.
sealed class Handover : IDisposable
{
    readonly SynchronizationContext _ui;
    readonly MMDeviceEnumerator _enumerator = new();
    readonly Notifications _notifications;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 100 };
    Mirror? _mirror;
    string? _lastDefault;
    HashSet<string> _lastConnected = new();
    string? _failedMirror; // don't retry a mirror that failed until the devices change

    public bool MirrorEnabled { get; set; }
    /// While paused, Evaluate does nothing: no default-device changes, no mirror.
    public bool Paused { get; set; }
    public Action<string>? Log { get; set; }

    public Handover()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Evaluate(); };
        _notifications = new Notifications(() => _ui.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null));
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    public void Evaluate()
    {
        if (Paused) return;
        try { EvaluateCore(); }
        catch (Exception ex) { Log?.Invoke($"handover: {ex.Message}"); }
    }

    void EvaluateCore()
    {
        var connected = BtAudio.Enumerate()
            .Where(d => d.Connected && d.StereoEndpointId is not null)
            .ToDictionary(d => d.StereoEndpointId!, d => d);
        string? def = DefaultOutput();

        if (_lastDefault is not null && _lastConnected.Contains(_lastDefault) && !connected.ContainsKey(_lastDefault)
            && connected.Count > 0 && (def is null || !connected.ContainsKey(def)))
        {
            // The set you were listening to is gone: hand over to the one still connected.
            var next = connected.Keys.First();
            SetDefault(next);
            Log?.Invoke($"handover: {connected[next].Label} is now the output");
            def = next;
        }
        else if (_lastConnected.Count == 0 && connected.Count > 0 && (def is null || !connected.ContainsKey(def)))
        {
            var next = connected.Keys.First();
            SetDefault(next);
            Log?.Invoke($"connected: {connected[next].Label} is now the output");
            def = next;
        }

        string? target = MirrorEnabled && def is not null && connected.ContainsKey(def)
            ? connected.Keys.FirstOrDefault(id => id != def) : null;
        string? wanted = target is null ? null : $"{def}>{target}";
        string? running = _mirror is null ? null : $"{_mirror.SourceId}>{_mirror.TargetId}";
        if (wanted != running)
        {
            _mirror?.Dispose();
            _mirror = null;
            if (running is not null) Log?.Invoke("mirror: stopped");
            if (wanted is not null && wanted != _failedMirror)
            {
                try
                {
                    _mirror = new Mirror(def!, target!);
                    _mirror.Start();
                    _failedMirror = null;
                    Log?.Invoke($"mirror: {connected[def!].Label} -> {connected[target!].Label}");
                }
                catch (Exception ex)
                {
                    _mirror?.Dispose();
                    _mirror = null;
                    _failedMirror = wanted;
                    Log?.Invoke($"mirror failed: {ex.Message}");
                }
            }
        }
        if (!connected.Keys.ToHashSet().SetEquals(_lastConnected)) _failedMirror = null;

        _lastDefault = def;
        _lastConnected = connected.Keys.ToHashSet();
    }

    public string? DefaultOutput()
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { return null; }
    }

    public static void SetDefault(string endpointId)
    {
        var policy = Native.PolicyConfig();
        for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(endpointId, role);
    }

    public void Dispose()
    {
        _mirror?.Dispose();
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifications); } catch { }
        _enumerator.Dispose();
        _debounce.Dispose();
    }

    sealed class Notifications(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render) changed(); }
        public void OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key) { }
    }
}
