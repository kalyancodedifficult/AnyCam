using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AnyCam.Core.Media;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Plays the phone's microphone on an output device. Pick a virtual cable (e.g. VB-Audio "CABLE Input")
/// to use the iPhone as a microphone in other apps. A small, steady buffer keeps lip sync; the delay
/// can be raised to line the audio up with the video.
/// </summary>
public sealed class AudioPlayback : IDisposable
{
    private readonly object _lock = new();
    private WasapiPlayer? _player;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _format;
    private int _targetBytes;
    private float _volume = 1;

    public static IReadOnlyList<AudioDevice> OutputDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _player is not null;
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_lock)
            {
                if (_player is not null)
                    _player.Volume = _volume;
            }
        }
    }

    /// <summary>Starts playback on the given device (null = default output) with an extra delay in ms.</summary>
    public void Start(string? deviceId, int sampleRate, int channels, int delayMs)
    {
        Stop();
        lock (_lock)
        {
            _format = new WaveFormat(sampleRate, 16, channels);
            _buffer = new BufferedWaveProvider(_format, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            // Target fill level: enough to ride out network jitter, plus the user's sync offset.
            _targetBytes = _format.AverageBytesPerSecond * (60 + Math.Clamp(delayMs, 0, 1000)) / 1000;
            _targetBytes -= _targetBytes % _format.BlockAlign;

            var builder = new WasapiPlayerBuilder().WithSharedMode().WithLatency(30).WithEventSync();
            if (!string.IsNullOrEmpty(deviceId))
            {
                using var enumerator = new MMDeviceEnumerator();
                builder = builder.WithDevice(enumerator.GetDevice(deviceId));
            }
            _player = builder.Build();
            _player.Init(_buffer);
            _player.Volume = _volume;
            _player.Play();
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        lock (_lock)
        {
            if (_buffer is null || _format is null)
                return;

            // If we drifted far behind (clock drift, burst after a stall), drop the backlog.
            if (_buffer.BufferedBytes > _targetBytes + _format.AverageBytesPerSecond / 5)
                _buffer.ClearBuffer();
            if (_buffer.BufferedBytes == 0 && _targetBytes > 0)
                _buffer.AddSamples(new byte[_targetBytes]); // pre-roll silence up to the target delay
            _buffer.AddSamples(pcm);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _player?.Stop();
            _player?.Dispose();
            _player = null;
            _buffer = null;
        }
    }

    public void Dispose() => Stop();
}
