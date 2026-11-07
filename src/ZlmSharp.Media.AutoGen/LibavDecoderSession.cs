using FFmpeg.AutoGen;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// 一路输入的常驻解码会话（进程内 libav）：打开 URL → 选视频流 → 解码 → sws 转成 RGB24。
/// 等价于 <c>ffmpeg -i url -an -vf scale=w:h -pix_fmt rgb24 -f rawvideo pipe:1</c>，
/// 但不成子进程、不经管道。
/// </summary>
internal sealed unsafe class LibavDecoderSession : IDisposable
{
    private const string ReadTimeoutMicroseconds = "5000000";

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private SwsContext* _sws;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private int _streamIndex = -1;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _draining;
    private bool _disposed;

    private LibavDecoderSession()
    {
    }

    /// <summary>目标宽（缩放后）。</summary>
    public int Width { get; private set; }

    /// <summary>目标高（缩放后）。</summary>
    public int Height { get; private set; }

    /// <summary>源分辨率（打开后可得）。</summary>
    public int SourceWidth => _sourceWidth;

    public int SourceHeight => _sourceHeight;

    /// <summary>打开解码会话。<paramref name="width"/>/<paramref name="height"/> 传 0 表示保持源分辨率。</summary>
    /// <exception cref="InvalidOperationException">打开输入/找流/初始化解码器失败。</exception>
    public static LibavDecoderSession Open(string url, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        var session = new LibavDecoderSession { Width = width, Height = height };
        try
        {
            session.OpenCore(url);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OpenCore(string url)
    {
        AVDictionary* options = null;
        try
        {
            if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                // 与 exe 实现一致：RTSP 走 TCP interleaved，避免 UDP 丢包/防火墙带来的偶发卡顿
                ffmpeg.av_dict_set(&options, "rtsp_transport", "tcp", 0);
                ffmpeg.av_dict_set(&options, "stimeout", ReadTimeoutMicroseconds, 0);
            }

            // 读超时：让 av_read_frame 周期性返回 EAGAIN，解码线程据此检查取消/存活
            ffmpeg.av_dict_set(&options, "rw_timeout", ReadTimeoutMicroseconds, 0);

            var format = _format;
            var open = ffmpeg.avformat_open_input(&format, url, null, &options);
            _format = format;
            if (open < 0)
            {
                throw LibavEnvironment.Error("打开输入", open, url);
            }
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        var probe = ffmpeg.avformat_find_stream_info(_format, null);
        if (probe < 0)
        {
            throw LibavEnvironment.Error("读取流信息", probe, url);
        }

        _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (_streamIndex < 0)
        {
            throw LibavEnvironment.Error("查找视频流", _streamIndex, url);
        }

        var parameters = _format->streams[_streamIndex]->codecpar;
        var decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (decoder == null)
        {
            throw new InvalidOperationException($"没有可用的解码器（codec_id={(int)parameters->codec_id}）：{url}");
        }

        _codec = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codec == null)
        {
            throw new InvalidOperationException($"分配解码器上下文失败：{url}");
        }

        var copy = ffmpeg.avcodec_parameters_to_context(_codec, parameters);
        if (copy < 0)
        {
            throw LibavEnvironment.Error("复制解码参数", copy, url);
        }

        var opened = ffmpeg.avcodec_open2(_codec, decoder, null);
        if (opened < 0)
        {
            throw LibavEnvironment.Error("打开解码器", opened, url);
        }

        _sourceWidth = _codec->width;
        _sourceHeight = _codec->height;
        if (_sourceWidth <= 0 || _sourceHeight <= 0)
        {
            throw new InvalidOperationException($"视频流分辨率未知：{url}");
        }

        // 抓单帧（Grab）走源分辨率；采样/转码走调用方指定尺寸
        if (Width <= 0)
        {
            Width = _sourceWidth;
        }

        if (Height <= 0)
        {
            Height = _sourceHeight;
        }

        _sws = ffmpeg.sws_getContext(
            _sourceWidth,
            _sourceHeight,
            _codec->pix_fmt,
            Width,
            Height,
            AVPixelFormat.AV_PIX_FMT_RGB24,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);

        if (_sws == null)
        {
            throw new InvalidOperationException($"创建缩放上下文失败（{_sourceWidth}x{_sourceHeight} → {Width}x{Height}）：{url}");
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null)
        {
            throw new InvalidOperationException($"分配解码缓冲失败：{url}");
        }
    }

    /// <summary>
    /// 解下一帧并写入 <paramref name="rgb"/>（长度必须 ≥ Width*Height*3）。
    /// 返回 false = 流结束或已取消。
    /// </summary>
    /// <exception cref="InvalidOperationException">解码出错。</exception>
    public bool TryReadFrame(byte[] rgb, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length < Width * Height * 3)
        {
            throw new ArgumentException("缓冲区小于一帧 RGB24", nameof(rgb));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var received = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (received == 0)
            {
                ConvertToRgb(rgb);
                return true;
            }

            if (received != ffmpeg.AVERROR(ffmpeg.EAGAIN) && received != ffmpeg.AVERROR_EOF)
            {
                throw LibavEnvironment.Error("解码", received);
            }

            if (_draining)
            {
                return false;
            }

            var read = ffmpeg.av_read_frame(_format, _packet);
            if (read == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // rw_timeout 到点但连接还在：继续等下一段数据
                continue;
            }

            if (read < 0)
            {
                // 输入结束：送空包让解码器把缓存帧吐完
                _draining = true;
                ffmpeg.avcodec_send_packet(_codec, null);
                continue;
            }

            if (_packet->stream_index == _streamIndex)
            {
                ffmpeg.avcodec_send_packet(_codec, _packet);
            }

            ffmpeg.av_packet_unref(_packet);
        }

        return false;
    }

    private void ConvertToRgb(byte[] rgb)
    {
        var source = new byte*[4];
        var sourceStride = new int[4];
        for (uint i = 0; i < 4; i++)
        {
            source[i] = _frame->data[i];
            sourceStride[i] = _frame->linesize[i];
        }

        var target = new byte*[4];
        var targetStride = new int[4];
        fixed (byte* pixels = rgb)
        {
            target[0] = pixels;
            targetStride[0] = Width * 3;

            var scaled = ffmpeg.sws_scale(_sws, source, sourceStride, 0, _sourceHeight, target, targetStride);
            if (scaled < 0)
            {
                throw LibavEnvironment.Error("像素格式转换", scaled);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        var frame = _frame;
        if (frame != null)
        {
            ffmpeg.av_frame_free(&frame);
            _frame = frame;
        }

        var packet = _packet;
        if (packet != null)
        {
            ffmpeg.av_packet_free(&packet);
            _packet = packet;
        }

        var codec = _codec;
        if (codec != null)
        {
            ffmpeg.avcodec_free_context(&codec);
            _codec = codec;
        }

        var format = _format;
        if (format != null)
        {
            ffmpeg.avformat_close_input(&format);
            _format = format;
        }
    }
}
