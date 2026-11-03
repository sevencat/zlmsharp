using System.Buffers;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Http;

/// <summary>FLV 数据写出目标（HTTP 响应体 / WebSocket 二进制消息）。</summary>
internal delegate ValueTask FlvWriteHandler(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

/// <summary>
/// HTTP-FLV 与 WS-FLV 共用的分发循环：先发 FLV header / onMetaData / AVC sequence header /
/// AAC sequence header，再持续转发音频与视频 tag。调用方负责捕获网络异常并记录日志。
/// </summary>
internal static class FlvLiveStream
{
    /// <param name="enhancedRtmp">HEVC 是否写 Enhanced RTMP 视频头（由 <c>[rtmp] enhanced</c> 控制）。</param>
    public static async Task<(int Frames, long Bytes)> RunAsync(
        MediaSource source,
        VideoCodecConfig config,
        FlvMetadata metadata,
        FlvWriteHandler write,
        CancellationToken cancellationToken,
        bool enhancedRtmp = false)
    {
        var sink = source.CreateReader();

        // 音频配置必须在构造 muxer 时就带上：AAC sequence header 通常只在推流开始时出现一次，
        // 中途加入的观众拿不到，mpegts.js / flv.js 会因为没有解码配置而静音。
        var muxer = new FlvMuxer(
            config,
            metadata,
            source.Audio.AudioSpecificConfig ?? default,
            source.Audio.SampleRate,
            source.Audio.Channels,
            enhancedRtmp);

        var tagBuffer = new ArrayBufferWriter<byte>(64 * 1024);

        long bytes = 0;
        var frames = 0;

        try
        {
            var header = muxer.BuildHeader();
            await write(header, cancellationToken).ConfigureAwait(false);
            bytes += header.Length;

            var metadataTag = muxer.BuildMetadataTag();
            await write(metadataTag, cancellationToken).ConfigureAwait(false);
            bytes += metadataTag.Length;

            var configTag = muxer.BuildAvcConfigTag();
            await write(configTag, cancellationToken).ConfigureAwait(false);
            bytes += configTag.Length;

            var audioConfigTag = muxer.BuildAudioConfigTag();
            if (audioConfigTag.Length > 0)
            {
                await write(audioConfigTag, cancellationToken).ConfigureAwait(false);
                bytes += audioConfigTag.Length;
            }

            await foreach (var frame in sink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (frame.TrackType == TrackType.Audio)
                    {
                        tagBuffer.Clear();
                        var audioWritten = muxer.WriteAudioTag(frame, tagBuffer);
                        if (audioWritten == 0)
                        {
                            continue;
                        }

                        await write(tagBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
                        bytes += audioWritten;
                        continue;
                    }

                    if (frame.TrackType != TrackType.Video)
                    {
                        continue;
                    }

                    tagBuffer.Clear();
                    var written = muxer.WriteVideoTag(frame, tagBuffer);
                    if (written == 0)
                    {
                        continue;
                    }

                    await write(tagBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

                    bytes += written;
                    frames++;
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        finally
        {
            source.RemoveReader(sink);
            sink.Dispose();
        }

        return (frames, bytes);
    }
}
