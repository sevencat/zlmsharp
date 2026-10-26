using System.Buffers;
using ZlmSharp.Codecs;
using ZlmSharp.Core;
using ZlmSharp.Protocols.Flv;

namespace ZlmSharp.Http;

/// <summary>FLV 数据写出目标（HTTP 响应体 / WebSocket 二进制消息）。</summary>
internal delegate ValueTask FlvWriteHandler(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

/// <summary>
/// HTTP-FLV 与 WS-FLV 共用的分发循环：先发 FLV header / onMetaData / AVC sequence header，
/// 再持续转发视频 tag。调用方负责捕获网络异常并记录日志。
/// </summary>
internal static class FlvLiveStream
{
    public static async Task<(int Frames, long Bytes)> RunAsync(
        MediaSource source,
        VideoCodecConfig config,
        FlvMetadata metadata,
        FlvWriteHandler write,
        CancellationToken cancellationToken)
    {
        var sink = source.CreateReader();
        var muxer = new FlvMuxer(config, metadata);
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

            await foreach (var frame in sink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
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
