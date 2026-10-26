using System.Globalization;
using System.Text;
using ZlmSharp.Core;

namespace ZlmSharp.Recording;

/// <summary>
/// HLS 输出：滚动切片（关键帧对齐）+ <c>.m3u8</c> 播放列表。
/// 切片名 <c>hls-000001.ts</c>，列表 <c>hls.m3u8</c>，只保留最近 <c>seg_num</c> 个切片。
/// </summary>
internal sealed class HlsPackage : IDisposable
{
    private readonly string _directory;
    private readonly string _playlistName;
    private readonly int _segmentSeconds;
    private readonly int _segmentCount;
    private readonly List<Segment> _segments = [];
    private readonly object _gate = new();

    private TsMuxer? _muxer;
    private string? _currentFile;
    private long _currentStartMs = -1;
    private long _currentLastMs;
    private long _nextSequence;
    private bool _disposed;

    private readonly CodecId _videoCodec;

    private byte[]? _audioSpecificConfig;
    private int _audioSampleRate;
    private int _audioChannels;

    public HlsPackage(
        string directory,
        string name,
        int segmentSeconds,
        int segmentCount,
        byte[]? audioSpecificConfig,
        int audioSampleRate,
        int audioChannels,
        CodecId videoCodec = CodecId.H264)
    {
        Directory.CreateDirectory(directory);
        _videoCodec = videoCodec;

        _directory = directory;
        _playlistName = name;
        _segmentSeconds = Math.Max(1, segmentSeconds);
        _segmentCount = Math.Max(2, segmentCount);
        _audioSpecificConfig = audioSpecificConfig;
        _audioSampleRate = audioSampleRate;
        _audioChannels = audioChannels;
        PlaylistPath = Path.Combine(directory, name + ".m3u8");
    }

    public string PlaylistPath { get; }

    public string? CurrentSegmentPath
    {
        get
        {
            lock (_gate)
            {
                return _currentFile;
            }
        }
    }

    public void WriteVideo(ReadOnlySpan<byte> data, long dts, long pts, bool keyFrame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_muxer is null)
            {
                // HLS 切片必须从关键帧开始，非关键帧等下一个 GOP
                if (!keyFrame)
                {
                    return;
                }

                OpenSegment(dts);
            }
            else if (keyFrame && dts - _currentStartMs >= _segmentSeconds * 1000L)
            {
                CloseSegment();
                OpenSegment(dts);
            }

            _muxer!.WriteVideo(data, dts, pts, keyFrame);
            _currentLastMs = Math.Max(_currentLastMs, dts);
        }
    }

    /// <summary>录制中途才拿到 AAC 配置时更新；对已开始的切片不生效（下一段起带音频）。</summary>
    public void ConfigureAudio(byte[]? audioSpecificConfig, int sampleRate, int channels)
    {
        lock (_gate)
        {
            _audioSpecificConfig = audioSpecificConfig;
            _audioSampleRate = sampleRate;
            _audioChannels = channels;
        }
    }

    public void WriteAudio(ReadOnlySpan<byte> data, long dts)
    {
        lock (_gate)
        {
            if (_disposed || _muxer is null)
            {
                return;
            }

            _muxer.WriteAudio(data, dts);
            _currentLastMs = Math.Max(_currentLastMs, dts);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CloseSegment(endList: true);
            _disposed = true;
        }
    }

    private void OpenSegment(long dts)
    {
        _nextSequence++;
        var name = $"hls-{_nextSequence:D6}.ts";
        _currentFile = Path.Combine(_directory, name);
        _currentStartMs = dts;
        _currentLastMs = dts;

        var stream = new FileStream(_currentFile, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        try
        {
            _muxer = new TsMuxer(stream, _audioSpecificConfig, _audioSampleRate, _audioChannels, _videoCodec);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void CloseSegment(bool endList = false)
    {
        var muxer = _muxer;
        var file = _currentFile;
        _muxer = null;
        _currentFile = null;

        if (muxer is null || file is null)
        {
            if (endList)
            {
                WritePlaylist(endList: true);
            }

            return;
        }

        var duration = _currentLastMs > _currentStartMs
            ? (_currentLastMs - _currentStartMs) / 1000.0
            : 0.1;
        muxer.Dispose();

        _segments.Add(new Segment(_nextSequence, Path.GetFileName(file), Math.Max(0.1, duration)));

        while (_segments.Count > _segmentCount)
        {
            var removed = _segments[0];
            _segments.RemoveAt(0);
            TryDelete(Path.Combine(_directory, removed.Name));
        }

        WritePlaylist(endList);
    }

    private void WritePlaylist(bool endList)
    {
        var builder = new StringBuilder(256);
        builder.Append("#EXTM3U\n");
        builder.Append("#EXT-X-VERSION:3\n");
        builder.Append("#EXT-X-TARGETDURATION:").Append(_segmentSeconds).Append('\n');
        builder.Append("#EXT-X-MEDIA-SEQUENCE:")
            .Append(_segments.Count > 0 ? _segments[0].Sequence : _nextSequence)
            .Append('\n');

        foreach (var segment in _segments)
        {
            builder.Append("#EXTINF:")
                .Append(segment.Duration.ToString("F3", CultureInfo.InvariantCulture))
                .Append(",\n")
                .Append(segment.Name)
                .Append('\n');
        }

        if (endList)
        {
            builder.Append("#EXT-X-ENDLIST\n");
        }

        var temporary = PlaylistPath + ".tmp";
        File.WriteAllText(temporary, builder.ToString(), Encoding.UTF8);
        File.Move(temporary, PlaylistPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Segment(long Sequence, string Name, double Duration);
}
