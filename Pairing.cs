using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BudsTray;

/// Pairs a device that is in pairing mode. Windows' own "Add device" list hides classic
/// devices whose name has not been read yet; some adapters return no name on the first
/// inquiry, so such devices never appear there. Here they are found by address.
static class Pairing
{
    const string AddressProp = "System.Devices.Aep.DeviceAddress";
    const string SignalProp = "System.Devices.Aep.SignalStrength";
    const string MajorClassProp = "System.Devices.Aep.Bluetooth.Cod.Major";
    const int AudioVideoMajorClass = 4; // Bluetooth class of device: headsets, headphones, speakers

    /// <param name="Info">The device to pair; null only for the Debug demo mode.</param>
    public sealed record Candidate(DeviceInformation? Info, string Name, string Address, int? Rssi)
    {
        public string Label => string.IsNullOrEmpty(Name) ? $"(unnamed) {Address}" : $"{Name} {Address}";
    }

    /// Unpaired classic Bluetooth devices in range that can be paired, strongest signal first.
    /// Devices that report a class other than audio/video (phones, keyboards, computers) are
    /// left out; devices that report no class at all are kept.
    public static async Task<List<Candidate>> ScanAsync(TimeSpan duration)
    {
        var found = new Dictionary<string, DeviceInformation>();
        var watcher = DeviceInformation.CreateWatcher(
            BluetoothDevice.GetDeviceSelectorFromPairingState(false),
            new[] { AddressProp, SignalProp, MajorClassProp }, DeviceInformationKind.AssociationEndpoint);
        watcher.Added += (_, d) => { lock (found) found[d.Id] = d; };
        watcher.Updated += (_, u) => { lock (found) if (found.TryGetValue(u.Id, out var d)) d.Update(u); };
        watcher.Start();
        await Task.Delay(duration);
        watcher.Stop();

        lock (found)
            return found.Values
                .Where(d => d.Pairing.CanPair && IsAudioOrUnknown(Property(d, MajorClassProp)))
                .Select(d => new Candidate(d, d.Name ?? "", Property(d, AddressProp)?.ToString() ?? "",
                    Property(d, SignalProp) is int r ? r : null))
                .OrderByDescending(c => c.Rssi ?? int.MinValue)
                .ToList();
    }

    internal static bool IsAudioOrUnknown(object? majorClass) =>
        majorClass is not IConvertible c || c.ToInt32(null) == AudioVideoMajorClass;

    static object? Property(DeviceInformation d, string name) =>
        d.Properties.TryGetValue(name, out var value) ? value : null;

    /// Pairs the candidate. The caller has already asked the user to confirm this device, so
    /// a "just works" request (ConfirmOnly, typical for earbuds) is accepted. A request that
    /// comes with a PIN or a numeric comparison code is passed to <paramref name="confirmCode"/>
    /// (kind, PIN) and accepted only if it returns true.
    public static async Task<DevicePairingResultStatus> PairAsync(Candidate c, Func<DevicePairingKinds, string, Task<bool>> confirmCode)
    {
        var custom = (c.Info ?? throw new InvalidOperationException("No device to pair.")).Pairing.Custom;
        custom.PairingRequested += async (_, a) =>
        {
            var deferral = a.GetDeferral();
            try
            {
                bool accept = a.PairingKind == DevicePairingKinds.ConfirmOnly
                    || await confirmCode(a.PairingKind, a.Pin ?? "");
                if (accept) a.Accept();
            }
            catch { } // not accepting rejects the pairing
            finally { deferral.Complete(); }
        };
        var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch);
        return result.Status;
    }
}
