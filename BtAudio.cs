using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BudsTray;

/// One paired Bluetooth audio device (grouped by PnP container), with the KS filters
/// that accept the one-shot reconnect/disconnect properties.
sealed class BtAudioDevice
{
    public required Guid ContainerId { get; init; }
    public string Name { get; set; } = "Bluetooth audio";
    public string? Mac { get; set; }
    public bool Connected { get; set; }
    public bool Paired { get; set; }
    public HashSet<string> FilterIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// Hands-Free (HFP) endpoints: the microphone and the "Hands-Free" headset output that pairs with it.
    public List<(string Id, uint State, bool Capture)> HfpEndpoints { get; } = new();
    public bool HasHfp => HfpEndpoints.Count > 0;
    /// The stereo (A2DP) output endpoint, whose volume is the one you hear in music/games.
    public string? StereoEndpointId { get; set; }
    public bool MicDisabled => HasHfp && HfpEndpoints.All(e => e.State == Native.DEVICE_STATE_DISABLED);
    /// A name the user gave this set in the tray menu; replaces Name and the address suffix.
    public string? CustomName { get; set; }

    /// The custom name, or the device name followed by the last four hex digits of its
    /// address, so two identical sets can be told apart.
    public string Label => CustomName
        ?? (Mac is { Length: 12 } ? $"{Name} ({Mac[8..].ToUpperInvariant()})" : Name);
}

static class BtAudio
{
    // e.g. ...bthenum#{0000110b-...}_localmfg&0000#8&1a2b3c4d&0&001122aabbcc_c00000000#...
    static readonly Regex MacInFilterId = new(@"[&_]([0-9a-f]{12})[_#&]", RegexOptions.IgnoreCase);

    /// The Bluetooth address (12 lowercase hex digits) inside a KS filter id, if any.
    internal static string? MacFromFilterId(string filterId)
    {
        var m = MacInFilterId.Match(filterId);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// The connected set Windows plays to, or else the first connected set.
    internal static BtAudioDevice? Active(IEnumerable<BtAudioDevice> devices, string? defaultOutput)
    {
        var connected = devices.Where(d => d.Connected && d.Mac is not null).ToList();
        return connected.FirstOrDefault(d => d.StereoEndpointId is not null && d.StereoEndpointId == defaultOutput)
            ?? connected.FirstOrDefault();
    }

    public static List<BtAudioDevice> Enumerate(List<string>? log = null)
    {
        var enumerator = Native.DeviceEnumerator();
        var byContainer = new Dictionary<Guid, BtAudioDevice>();

        Check(enumerator.EnumAudioEndpoints(Native.eAll, Native.DEVICE_STATEMASK_ALL, out var collection));
        collection.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            try
            {
                if (collection.Item(i, out var endpoint) != 0) continue;
                string? filterId = ConnectedFilterId(endpoint);
                endpoint.GetId(out string endpointId);
                log?.Add($"endpoint {endpointId} -> {filterId ?? "(no filter)"}");
                if (filterId is null || !IsBluetoothFilter(filterId)) continue;

                endpoint.OpenPropertyStore(0, out var props);
                var container = GetGuid(props, Native.PKEY_Device_ContainerId) ?? Guid.Empty;
                if (!byContainer.TryGetValue(container, out var dev))
                {
                    dev = new BtAudioDevice { ContainerId = container };
                    dev.Name = GetString(props, Native.PKEY_DeviceInterface_FriendlyName)
                        ?? GetString(props, Native.PKEY_Device_FriendlyName) ?? dev.Name;
                    byContainer[container] = dev;
                }
                dev.FilterIds.Add(filterId);
                if (MacFromFilterId(filterId) is { } mac) dev.Mac = mac;
                endpoint.GetState(out uint state);
                if (state == Native.DEVICE_STATE_ACTIVE) dev.Connected = true;
                if (filterId.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase))
                    dev.HfpEndpoints.Add((endpointId, state, endpointId.StartsWith("{0.0.1.", StringComparison.Ordinal)));
                else if (endpointId.StartsWith("{0.0.0.", StringComparison.Ordinal))
                    dev.StereoEndpointId = endpointId;
            }
            catch (Exception ex)
            {
                log?.Add($"endpoint #{i}: {ex.Message}");
            }
        }
        foreach (var dev in byContainer.Values)
        {
            if (dev.Mac is not null && PairedName(dev.Mac) is { } name) dev.Name = name;
            // Endpoints outlive an unpairing; the KS filter only exists while the device is paired.
            dev.Paired = dev.FilterIds.Any(id => enumerator.GetDevice(id, out _) == 0);
        }
        return byContainer.Values.OrderBy(d => d.Label).ToList();
    }

    /// The remote device name Windows stored at pairing time.
    static string? PairedName(string mac)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{mac}");
        return key?.GetValue("Name") is byte[] raw ? System.Text.Encoding.UTF8.GetString(raw).TrimEnd('\0') : null;
    }

    /// Disables or re-enables the device's Hands-Free endpoints, like "Disable" in the Sound
    /// control panel. Windows keeps this across reconnects and reboots. Returns failures.
    public static int SetMicEnabled(BtAudioDevice dev, bool enabled, List<string>? log = null)
    {
        var policy = Native.PolicyConfig();
        int failed = 0;
        foreach (var (id, _, _) in dev.HfpEndpoints)
        {
            int hr = policy.SetEndpointVisibility(id, enabled ? 1 : 0);
            log?.Add($"SetEndpointVisibility({enabled}) 0x{hr:X8} {id}");
            if (hr != 0) failed++;
        }
        return failed;
    }

    /// Volume 0..1 of an output endpoint, or null if it is not active.
    public static float? GetVolume(string endpointId)
    {
        var vol = EndpointVolume(endpointId);
        return vol is not null && vol.GetMasterVolumeLevelScalar(out float level) == 0 ? level : null;
    }

    public static bool SetVolume(string endpointId, float level)
    {
        var vol = EndpointVolume(endpointId);
        var ctx = Guid.Empty;
        return vol is not null && vol.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx) == 0;
    }

    static IAudioEndpointVolume? EndpointVolume(string endpointId)
    {
        var enumerator = Native.DeviceEnumerator();
        if (enumerator.GetDevice(endpointId, out var endpoint) != 0) return null;
        endpoint.GetState(out uint state);
        if (state != Native.DEVICE_STATE_ACTIVE) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        return endpoint.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var obj) == 0 ? (IAudioEndpointVolume)obj : null;
    }

    public static int Connect(BtAudioDevice dev, List<string>? log = null) => SendToAll(dev, Native.KSPROPERTY_ONESHOT_RECONNECT, log);
    public static int Disconnect(BtAudioDevice dev, List<string>? log = null) => SendToAll(dev, Native.KSPROPERTY_ONESHOT_DISCONNECT, log);

    /// Returns how many filters accepted the request.
    static int SendToAll(BtAudioDevice dev, uint propertyId, List<string>? log)
    {
        var enumerator = Native.DeviceEnumerator();
        int ok = 0;
        foreach (var filterId in dev.FilterIds)
        {
            int hr = enumerator.GetDevice(filterId, out var filter);
            log?.Add($"GetDevice 0x{hr:X8} {filterId}");
            if (hr != 0) continue;
            var iid = typeof(IKsControl).GUID;
            hr = filter.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var obj);
            log?.Add($"Activate(IKsControl) 0x{hr:X8}");
            if (hr != 0) continue;
            var ks = (IKsControl)obj;

            var header = new KsPropertyHeader { Set = Native.KSPROPSETID_BtAudio, Id = propertyId, Flags = Native.KSPROPERTY_TYPE_GET };
            int size = Marshal.SizeOf<KsPropertyHeader>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(header, p, false);
                hr = ks.KsProperty(p, (uint)size, IntPtr.Zero, 0, out _);
                log?.Add($"KsProperty 0x{hr:X8}");
                if (hr == 0) ok++;
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        return ok;
    }

    static string? ConnectedFilterId(IMMDevice endpoint)
    {
        var iid = typeof(IDeviceTopology).GUID;
        if (endpoint.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return null;
        var topology = (IDeviceTopology)obj;
        if (topology.GetConnector(0, out var connector) != 0) return null;
        return connector.GetDeviceIdConnectedTo(out string id) == 0 ? id : null;
    }

    static bool IsBluetoothFilter(string id) =>
        id.Contains("bthenum", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase);

    static string? GetString(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.VarType == Native.VT_LPWSTR ? Marshal.PtrToStringUni(pv.Pointer) : null; }
        finally { Native.PropVariantClear(ref pv); }
    }

    static Guid? GetGuid(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.VarType == Native.VT_CLSID ? Marshal.PtrToStructure<Guid>(pv.Pointer) : null; }
        finally { Native.PropVariantClear(ref pv); }
    }

    static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }
}
