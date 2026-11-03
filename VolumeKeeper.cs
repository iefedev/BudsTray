namespace BudsTray;

/// Carries the output volume over when you switch headphones. Windows keeps a separate
/// volume per device, so a newly connected set comes back at whatever it had last time.
/// This remembers the volume of the set in use and applies it to the next set that connects,
/// whether it was connected from the menu or by itself.
sealed class VolumeKeeper
{
    // Bluetooth absolute volume can report the earbuds' own level a moment after connecting,
    // which overwrites ours; keep re-applying for this long.
    static readonly TimeSpan EnforceFor = TimeSpan.FromSeconds(4);

    float? _level;
    HashSet<string> _connected = new();
    readonly Dictionary<string, DateTime> _enforceUntil = new();

    public bool Enabled { get; set; } = true;
    public Action<string>? Log { get; set; }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        var current = BtAudio.Enumerate()
            .Where(d => d.Connected && d.StereoEndpointId is not null)
            .ToDictionary(d => d.StereoEndpointId!, d => d);

        var arrived = current.Keys.Where(id => !_connected.Contains(id)).ToList();
        bool firstTick = _connected.Count == 0 && _level is null;
        _connected = current.Keys.ToHashSet();
        foreach (var id in _enforceUntil.Keys.Where(id => _enforceUntil[id] < now || !current.ContainsKey(id)).ToList())
            _enforceUntil.Remove(id);

        if (Enabled && _level is { } level && !firstTick)
            foreach (var id in arrived)
                _enforceUntil[id] = now + EnforceFor;

        foreach (var (id, until) in _enforceUntil)
        {
            if (BtAudio.GetVolume(id) is { } v && Math.Abs(v - _level!.Value) > 0.005f)
            {
                BtAudio.SetVolume(id, _level.Value);
                Log?.Invoke($"{current[id].Label}: {v:P0} -> {_level.Value:P0}");
            }
        }

        // Learn from a device that is not being corrected, so your own changes are remembered.
        foreach (var id in current.Keys.Where(id => !_enforceUntil.ContainsKey(id)))
            if (BtAudio.GetVolume(id) is { } v) { _level = v; break; }
    }
}
