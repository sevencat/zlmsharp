using System.Buffers;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Recording;

/// <summary>FLV 录像文件：追加式写入，崩溃后最多丢最后一个 tag，可直接用 VLC/ffmpeg 播放。</summary>
internal sealed class FlvFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly FlvMuxer _muxer;
    private readonly ArrayBufferWriter<byte> _buffer = new(512 * 1024);

    public FlvFileWriter(
        string path,
        VideoCodecConfig videoConfig,
        FlvMetadata metadata,
        ReadOnlyMemory<byte> audioConfig,
        int audioSampleRate,
        int audioChannels)
    {
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        _muxer = new FlvMuxer(videoConfig, metadata, audioConfig, audioSampleRate, audioChannels);

        _stream.Write(_muxer.BuildHeader());
        _stream.Write(_muxer.BuildMetadataTag());
        _stream.Write(_muxer.BuildAvcConfigTag());

        var audioTag = _muxer.BuildAudioConfigTag();
        if (audioTag.Length > 0)
        {
            _stream.Write(audioTag);
        }
    }

    public string Path => _stream.Name;

    public bool HasAudio => _muxer.HasAudio;

    public void WriteVideo(MediaFrame frame)
    {
        _buffer.Clear();
        var written = _muxer.WriteVideoTag(frame, _buffer);
        if (written > 0)
        {
            _stream.Write(_buffer.WrittenSpan);
        }
    }

    public void WriteAudio(MediaFrame frame)
    {
        if (!_muxer.HasAudio)
        {
            return;
        }

        _buffer.Clear();
        var written = _muxer.WriteAudioTag(frame, _buffer);
        if (written > 0)
        {
            _stream.Write(_buffer.WrittenSpan);
        }
    }

    public void Complete()
    {
        _stream.Flush();
    }

    public void Dispose()
    {
        try
        {
            Complete();
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
