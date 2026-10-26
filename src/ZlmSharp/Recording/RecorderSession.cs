using Microsoft.Extensions.Logging;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Recording;

/// <summary>
/// 一路录像：订阅 <see cref="MediaSource"/> 的帧，按格式写 MP4/FLV（按 <c>max_second</c> 切片）
/// 或 HLS（TS 关键帧对齐滚动切片 + m3u8）。
/// 视频帧是 Annex-B，音频是 AAC 原始访问单元（不含 ADTS）。
/// </summary>
internal sealed class RecorderSession : IDisposable
{
    private static readonly int[] SamplingRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000, 7350,
    ];

    private readonly StreamKey _key;
    private readonly MediaSource _source;
    private readonly RecordFormat _formats;
    private readonly int _maxSeconds;
    private readonly string _root;
    private readonly ILogger? _logger;
    private readonly VideoCodecConfig _videoConfig;
    private readonly int _width;
    private readonly int _height;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private readonly int _hlsSegmentSeconds;
    private readonly int _hlsSegmentCount;

    private MediaSink? _sink;
    private Task? _task;
    private Slice? _slice;
    private HlsPackage? _hls;
    private byte[]? _audioAsc;
    private int _audioSampleRate;
    private int _audioChannels;
    private long _frames;
    private long _firstDts = -1;
    private long _lastDts = -1;
    private int _sliceIndex;
    private RecordingState _state = RecordingState.Starting;
    private string? _error;
    private int _disposed;

    public RecorderSession(
        StreamKey key,
        MediaSource source,
        RecordFormat formats,
        int maxSeconds,
        string root,
        ILogger? logger,
        int hlsSegmentSeconds = 5,
        int hlsSegmentCount = 5)
    {
        _key = key;
        _source = source;
        _formats = formats;
        _maxSeconds = Math.Max(0, maxSeconds);
        _root = root;
        _logger = logger;
        _hlsSegmentSeconds = Math.Max(1, hlsSegmentSeconds);
        _hlsSegmentCount = Math.Max(2, hlsSegmentCount);

        _videoConfig = VideoCodecConfigResolver.Resolve(source)
            ?? throw new InvalidOperationException($"流还没有 H.264 参数集（未收到关键帧），无法录像：{key}");

        _width = source.Video.Width;
        _height = source.Video.Height;
        if (_width <= 0 || _height <= 0)
        {
            throw new InvalidOperationException($"流分辨率未知，无法录像：{key}");
        }

        _audioAsc = source.Audio.AudioSpecificConfig;
        _audioSampleRate = source.Audio.SampleRate;
        _audioChannels = source.Audio.Channels;
    }

    public void Start()
    {
        // GOP 里的 AAC sequence header 先抓一份，保证第一个切片能带音频
        foreach (var frame in _source.SnapshotGop())
        {
            try
            {
                if (frame.TrackType == TrackType.Audio && frame.IsConfig)
                {
                    CaptureAudioConfig(frame.Data.Span);
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        if (_formats.HasFlag(RecordFormat.Hls))
        {
            _hls = new HlsPackage(
                RecordPaths.BuildHlsDirectory(_root, _key),
                "hls",
                _hlsSegmentSeconds,
                _hlsSegmentCount,
                _audioAsc,
                _audioSampleRate,
                _audioChannels,
                _videoConfig.Codec);
        }

        // 先建好第一个切片，startRecord 能立即返回文件路径
        lock (_gate)
        {
            _slice = CreateSlice();
        }

        _sink = _source.CreateReader();
        _state = RecordingState.Recording;
        _task = Task.Run(() => LoopAsync(_cts.Token));
    }

    public RecordingStatus Snapshot()
    {
        lock (_gate)
        {
            return new RecordingStatus(
                _key.Vhost,
                _key.App,
                _key.Stream,
                _formats,
                _state,
                _slice?.Mp4Path,
                _slice?.FlvPath,
                _startedAt,
                _firstDts >= 0 && _lastDts >= _firstDts ? _lastDts - _firstDts : 0,
                Interlocked.Read(ref _frames),
                _error,
                _hls?.PlaylistPath);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        try
        {
            _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _sink?.Dispose();
        FinalizeSlice();

        try
        {
            _hls?.Dispose();
        }
        catch (IOException exception)
        {
            _logger?.LogWarning(exception, "HLS 收尾失败 {Key}", _key);
        }

        _state = RecordingState.Stopped;
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _sink!.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (frame.IsConfig)
                    {
                        if (frame.TrackType == TrackType.Audio)
                        {
                            CaptureAudioConfig(frame.Data.Span);
                        }

                        continue;
                    }

                    if (frame.TrackType == TrackType.Video)
                    {
                        WriteVideo(frame);
                    }
                    else if (frame.TrackType == TrackType.Audio)
                    {
                        WriteAudio(frame);
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError($"录像失败：{exception.Message}");
            _logger?.LogWarning(exception, "录像失败 {Key}", _key);
        }
        finally
        {
            FinalizeSlice();
            if (cancellationToken.IsCancellationRequested)
            {
                _state = RecordingState.Stopped;
            }
            else if (CurrentError() is null)
            {
                _state = RecordingState.Stopped;
            }
            else
            {
                _state = RecordingState.Failed;
            }

            _logger?.LogInformation(
                "录像结束 {Key}：state={State} frames={Frames} error={Error}",
                _key,
                _state,
                Interlocked.Read(ref _frames),
                CurrentError() ?? "-");
        }
    }

    private void WriteVideo(MediaFrame frame)
    {
        _hls?.WriteVideo(frame.Data.Span, frame.Dts, frame.Pts, frame.IsKeyFrame);

        if (_formats.HasFlag(RecordFormat.Mp4) || _formats.HasFlag(RecordFormat.Flv))
        {
            lock (_gate)
            {
                EnsureSlice(frame.Dts);
                _slice!.WriteVideo(frame);
            }
        }

        UpdateProgress(frame.Dts);
    }

    private void WriteAudio(MediaFrame frame)
    {
        if (frame.Length > 0)
        {
            _hls?.WriteAudio(frame.Data.Span, frame.Dts);
        }

        if (_formats.HasFlag(RecordFormat.Mp4) || _formats.HasFlag(RecordFormat.Flv))
        {
            lock (_gate)
            {
                EnsureSlice(frame.Dts);
                _slice!.WriteAudio(frame);
            }
        }

        UpdateProgress(frame.Dts);
    }

    private void EnsureSlice(long dts)
    {
        if (_slice is not null && !_slice.Expired(dts, _maxSeconds))
        {
            _slice.MarkStarted(dts);
            return;
        }

        _slice?.Dispose();
        _slice = CreateSlice();
        _slice.MarkStarted(dts);
    }

    private Slice CreateSlice()
    {
        var time = DateTimeOffset.Now;
        var index = ++_sliceIndex;
        var slice = new Slice();

        try
        {
            if (_formats.HasFlag(RecordFormat.Mp4))
            {
                var path = RecordPaths.BuildPath(_root, _key, "mp4", time, index);
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
                try
                {
                    slice.Mp4 = new Mp4Writer(
                        stream,
                        _videoConfig,
                        _width,
                        _height,
                        _audioAsc ?? default,
                        _audioSampleRate,
                        _audioChannels);
                    slice.Mp4Path = path;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            if (_formats.HasFlag(RecordFormat.Flv))
            {
                var path = RecordPaths.BuildPath(_root, _key, "flv", time, index);
                slice.Flv = new FlvFileWriter(
                    path,
                    _videoConfig,
                    new FlvMetadata
                    {
                        Width = _width,
                        Height = _height,
                        FrameRate = _source.Video.Fps,
                    },
                    _audioAsc ?? default,
                    _audioSampleRate,
                    _audioChannels);
                slice.FlvPath = path;
            }

            _logger?.LogInformation(
                "录像开始切片 {Key}：mp4={Mp4} flv={Flv}",
                _key,
                slice.Mp4Path ?? "-",
                slice.FlvPath ?? "-");

            return slice;
        }
        catch
        {
            slice.Dispose();
            throw;
        }
    }

    private void FinalizeSlice()
    {
        lock (_gate)
        {
            _slice?.Dispose();
            _slice = null;
        }
    }

    private void CaptureAudioConfig(ReadOnlySpan<byte> asc)
    {
        if (asc.IsEmpty)
        {
            return;
        }

        var rate = _audioSampleRate;
        var channels = _audioChannels;
        if (ParseAudioConfig(asc, out var parsedRate, out var parsedChannels))
        {
            rate = rate > 0 ? rate : parsedRate;
            channels = channels > 0 ? channels : parsedChannels;
        }

        lock (_gate)
        {
            _audioAsc = asc.ToArray();
            _audioSampleRate = rate;
            _audioChannels = channels;
        }

        _hls?.ConfigureAudio(_audioAsc, rate, channels);
    }

    private void UpdateProgress(long dts)
    {
        lock (_gate)
        {
            if (_firstDts < 0)
            {
                _firstDts = dts;
            }

            if (dts > _lastDts)
            {
                _lastDts = dts;
            }
        }

        Interlocked.Increment(ref _frames);
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _error ??= message;
        }
    }

    private string? CurrentError()
    {
        lock (_gate)
        {
            return _error;
        }
    }

    private static bool ParseAudioConfig(ReadOnlySpan<byte> asc, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 2;

        if (asc.Length < 2)
        {
            return false;
        }

        var frequencyIndex = (asc[0] >> 2) & 0x0F;
        if (frequencyIndex == 0x0F)
        {
            if (asc.Length < 5)
            {
                return false;
            }

            sampleRate = ((asc[1] & 0x7F) << 17) | (asc[2] << 9) | (asc[3] << 1) | ((asc[4] >> 7) & 0x01);
        }
        else if (frequencyIndex < SamplingRates.Length)
        {
            sampleRate = SamplingRates[frequencyIndex];
        }

        var channelConfig = (asc[1] >> 3) & 0x0F;
        channels = channelConfig switch
        {
            0 => 0,
            7 => 8,
            _ => channelConfig,
        };

        if (channels <= 0)
        {
            channels = 1;
        }

        return sampleRate > 0;
    }

    private sealed class Slice : IDisposable
    {
        public Mp4Writer? Mp4 { get; set; }

        public FlvFileWriter? Flv { get; set; }

        public string? Mp4Path { get; set; }

        public string? FlvPath { get; set; }

        public long? StartDts { get; private set; }

        public void MarkStarted(long dts) => StartDts ??= dts;

        public bool Expired(long dts, int maxSeconds) =>
            maxSeconds > 0 && StartDts is { } start && dts - start >= maxSeconds * 1000L;

        public void WriteVideo(MediaFrame frame)
        {
            Mp4?.WriteVideo(frame.Data.Span, frame.Dts, frame.Pts, frame.IsKeyFrame);
            Flv?.WriteVideo(frame);
        }

        public void WriteAudio(MediaFrame frame)
        {
            if (frame.Length == 0)
            {
                return;
            }

            Mp4?.WriteAudio(frame.Data.Span, frame.Dts);
            Flv?.WriteAudio(frame);
        }

        public void Dispose()
        {
            try
            {
                Mp4?.Dispose();
            }
            finally
            {
                Flv?.Dispose();
            }
        }
    }
}
