namespace BudsTray;

/// The user settings, stored as key=value lines in %APPDATA%\BudsTray\settings.txt.
sealed record Settings(bool KeepVolume, bool ShowBattery, bool Mirror)
{
    /// <param name="text">The settings file's content, or null if there is no file.</param>
    /// <param name="existingInstall">Whether the app's data folder already existed. Versions
    /// before 0.2.0 mirrored by default and only wrote the file once a setting was changed,
    /// so for them a missing "mirror" key still means on. New installs start with it off.</param>
    public static Settings Parse(string? text, bool existingInstall)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        bool Flag(string key, bool fallback) => values.TryGetValue(key, out var v) ? v != "0" : fallback;
        return new Settings(Flag("keepVolume", true), Flag("showBattery", true), Flag("mirror", existingInstall));
    }

    public string[] ToLines() => new[]
    {
        $"keepVolume={(KeepVolume ? 1 : 0)}",
        $"showBattery={(ShowBattery ? 1 : 0)}",
        $"mirror={(Mirror ? 1 : 0)}",
    };
}
