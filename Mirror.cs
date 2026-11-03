using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BudsTray;

/// Plays whatever Windows sends to one output device on a second one as well
/// (loopback capture of the source, rendered to the target).
sealed class Mirror : IDisposable
{
    // Capture and playback run on different clocks; when the backlog grows past this,
    // drop it so the copy never drifts audibly behind.
    static readonly TimeSpan MaxBacklog = TimeSpan.FromMilliseconds(150);

    readonly WasapiLoopbackCapture _capture;
    readonly WasapiOut _output;
    readonly BufferedWaveProvider _buffer;

    public string SourceId { get; }
    public string TargetId { get; }

    public Mirror(string sourceId, string targetId)
    {
        SourceId = sourceId;
        TargetId = targetId;
        using var enumerator = new MMDeviceEnumerator();
        var source = enumerator.GetDevice(sourceId);
        var target = enumerator.GetDevice(targetId);

        _capture = new WasapiLoopbackCapture(source);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // silence when the source is quiet
        };
        _capture.DataAvailable += (_, e) =>
        {
            if (_buffer.BufferedDuration > MaxBacklog) _buffer.ClearBuffer();
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        // Shared-mode output must match the target's mix format.
        var mix = target.AudioClient.MixFormat;
        ISampleProvider samples = _buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1 && mix.Channels >= 2) samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels > 2 && mix.Channels == 2) samples = new MultiplexingSampleProvider(new[] { samples }, 2);
        if (samples.WaveFormat.SampleRate != mix.SampleRate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);

        _output = new WasapiOut(target, AudioClientShareMode.Shared, true, 50);
        _output.Init(samples);
    }

    public void Start()
    {
        _output.Play();
        _capture.StartRecording();
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        try { _output.Stop(); } catch { }
        _capture.Dispose();
        _output.Dispose();
    }
}
