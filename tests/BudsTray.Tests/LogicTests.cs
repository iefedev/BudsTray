using Xunit;

namespace BudsTray.Tests;

public class SettingsTests
{
    [Fact]
    public void NewInstallStartsWithMirrorOff()
    {
        var s = Settings.Parse(null, existingInstall: false);
        Assert.False(s.Mirror);
        Assert.True(s.KeepVolume);
        Assert.True(s.ShowBattery);
    }

    [Fact]
    public void ExistingInstallWithoutMirrorKeyKeepsOldDefault() =>
        Assert.True(Settings.Parse(null, existingInstall: true).Mirror);

    [Fact]
    public void StoredValuesWin()
    {
        var s = Settings.Parse("keepVolume=0\r\nshowBattery=1\r\nmirror=1\r\n", existingInstall: false);
        Assert.Equal(new Settings(false, true, true), s);
        Assert.False(Settings.Parse("mirror=0", existingInstall: true).Mirror);
    }

    [Fact]
    public void RoundTrips()
    {
        var s = new Settings(true, false, true);
        Assert.Equal(s, Settings.Parse(string.Join("\n", s.ToLines()), existingInstall: false));
    }
}

public class DeviceTests
{
    [Theory]
    [InlineData(@"{2}.\\?\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_localmfg&0000#8&1a2b3c4d&0&001122aabbcc_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\a2dp", "001122aabbcc")]
    [InlineData(@"{2}.\\?\bthhfenum#bthhfpaudio#8&1a2b3c4d&0&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\hfp", null)]
    public void FindsAddressInFilterId(string filterId, string? mac) =>
        Assert.Equal(mac, BtAudio.MacFromFilterId(filterId));

    [Fact]
    public void LabelShowsAddressSuffixUnlessRenamed()
    {
        var d = new BtAudioDevice { ContainerId = Guid.NewGuid(), Name = "Earbuds", Mac = "00112233a1b2" };
        Assert.Equal("Earbuds (A1B2)", d.Label);
        d.CustomName = "Left pair";
        Assert.Equal("Left pair", d.Label);
        Assert.Equal("00112233a1b2", d.Mac); // renaming keeps the address
    }

    [Fact]
    public void ActiveIsTheDefaultOutputNotTheFirstByName()
    {
        var a = new BtAudioDevice { ContainerId = Guid.NewGuid(), Name = "A", Mac = "001122330001", Connected = true, StereoEndpointId = "ep-a" };
        var b = new BtAudioDevice { ContainerId = Guid.NewGuid(), Name = "B", Mac = "001122330002", Connected = true, StereoEndpointId = "ep-b" };
        Assert.Same(b, BtAudio.Active(new[] { a, b }, "ep-b"));
        Assert.Same(a, BtAudio.Active(new[] { a, b }, "speakers"));
        Assert.Null(BtAudio.Active(Array.Empty<BtAudioDevice>(), "ep-a"));
    }

    [Theory]
    [InlineData((ushort)4, true)]   // audio/video
    [InlineData((ushort)2, false)]  // phone
    [InlineData((ushort)5, false)]  // peripheral (keyboard, mouse)
    [InlineData(null, true)]        // class not reported
    public void PairingScanKeepsAudioDevices(object? majorClass, bool kept) =>
        Assert.Equal(kept, Pairing.IsAudioOrUnknown(majorClass));
}

public class IconTests
{
    [Fact]
    public void HundredFitsTheBadge()
    {
        int width = "100".Sum(c => TrayContext.Glyph(c)[0].Length) + 2;
        Assert.True(width <= 16, $"100 is {width} px wide");
    }

    [Fact]
    public void GlyphsHaveNineRowsOfEqualWidth()
    {
        foreach (char c in "0123456789")
        {
            var rows = TrayContext.Glyph(c);
            Assert.Equal(9, rows.Length);
            Assert.All(rows, r => Assert.Equal(rows[0].Length, r.Length));
        }
    }
}
