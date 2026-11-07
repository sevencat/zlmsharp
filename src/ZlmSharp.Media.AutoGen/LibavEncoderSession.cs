using FFmpeg.AutoGen;
using ZlmSharp.Extra.Media;

namespace ZlmSharp.Media.AutoGen;

/// <summary>
/// 进程内编码会话：RGB24 帧 → libx264（veryfast + zerolatency + yuv420p）→ 封装（flv/rtmp）→ 输出 URL。
/// 等价于 <c>ffmpeg -f rawvideo -pix_fmt rgb24 -s WxH -r fps -i pipe:0 -c:v libx264 ... -f flv rtmp://...</c>。
/// </summary>
/// <remarks>非线程安全：只允许在同一个编码线程里调用 <see cref="Encode"/> / <see cref="Flush"/>。</remarks>
internal sealed unsafe class LibavEncoderSession : IDisposable
{
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVStream* _stream;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private long _pts;
    private int _width;
    private int _height;
    private bool _disposed;

    private LibavEncoderSession()
    {
    }

    /// <summary>打开编码器与输出（会立即建立 RTMP 连接）。</summary>
    /// <exception cref="InvalidOperationException">编码器/封装器/连接初始化失败。</exception>
    public static LibavEncoderSession Open(FrameSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputUrl);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Fps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BitrateKbps, 1);

        var session = new LibavEncoderSession { _width = options.Width, _height = options.Height };
        try
        {
            session.OpenCore(options);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OpenCore(FrameSinkOptions options)
    {
        var encoder = ffmpeg.avcodec_find_encoder_by_name("libx264");
        if (encoder == null)
        {
            throw new InvalidOperationException("当前 ffmpeg 构建不包含 libx264 编码器");
        }

        _codec = ffmpeg.avcodec_alloc_context3(encoder);
        if (_codec == null)
        {
            throw new InvalidOperationException("分配编码器上下文失败");
        }

        _codec->width = options.Width;
        _codec->height = options.Height;
        _codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _codec->time_base = new AVRational { num = 1, den = options.Fps };
        _codec->framerate = new AVRational { num = options.Fps, den = 1 };
        _codec->gop_size = options.GopSize;
        _codec->max_b_frames = 0;
        _codec->bit_rate = options.BitrateKbps * 1000L;

        // 与 exe 实现保持一致的编码参数
        ffmpeg.av_opt_set(_codec->priv_data, "preset", "veryfast", 0);
        ffmpeg.av_opt_set(_codec->priv_data, "tune", "zerolatency", 0);
        ffmpeg.av_opt_set(_codec->priv_data, "sc_threshold", "0", 0);

        // flv/rtmp 需要把 SPS/PPS 放进 extradata（AVCDecoderConfigurationRecord），
        // 否则 muxer 首个配置帧不是合法 avcC，播放端要等下一个关键帧才能解码
        _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        var opened = ffmpeg.avcodec_open2(_codec, encoder, null);
        if (opened < 0)
        {
            throw LibavEnvironment.Error("打开编码器", opened, options.OutputUrl);
        }

        // RTMP 无法从 URL 推断封装格式（av_guess_format 只看扩展名/协议），必须显式 flv
        var formatName = options.OutputUrl.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
            || options.OutputUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase)
            ? "flv"
            : null;

        var format = _format;
        var allocated = ffmpeg.avformat_alloc_output_context2(&format, null, formatName, options.OutputUrl);
        _format = format;
        if (allocated < 0 || _format == null)
        {
            throw LibavEnvironment.Error("创建封装器", allocated, options.OutputUrl);
        }

        _stream = ffmpeg.avformat_new_stream(_format, null);
        if (_stream == null)
        {
            throw new InvalidOperationException($"创建输出流失败：{options.OutputUrl}");
        }

        var parameters = ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codec);
        if (parameters < 0)
        {
            throw LibavEnvironment.Error("复制编码参数", parameters, options.OutputUrl);
        }

        _stream->time_base = _codec->time_base;

        if ((_format->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            // rw_timeout：推流网络卡住时不至于永久阻塞关闭
            AVDictionary* ioOptions = null;
            try
            {
                ffmpeg.av_dict_set(&ioOptions, "rw_timeout", "5000000", 0);
                var io = ffmpeg.avio_open2(&_format->pb, options.OutputUrl, ffmpeg.AVIO_FLAG_WRITE, null, &ioOptions);
                if (io < 0)
                {
                    throw LibavEnvironment.Error("打开输出", io, options.OutputUrl);
                }
            }
            finally
            {
                ffmpeg.av_dict_free(&ioOptions);
            }
        }

        var header = ffmpeg.avformat_write_header(_format, null);
        if (header < 0)
        {
            throw LibavEnvironment.Error("写输出头", header, options.OutputUrl);
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame == null || _packet == null)
        {
            throw new InvalidOperationException("分配编码缓冲失败");
        }

        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _frame->width = options.Width;
        _frame->height = options.Height;

        var buffer = ffmpeg.av_frame_get_buffer(_frame, 0);
        if (buffer < 0)
        {
            throw LibavEnvironment.Error("分配编码帧缓冲", buffer);
        }

        _sws = ffmpeg.sws_getContext(
            options.Width,
            options.Height,
            AVPixelFormat.AV_PIX_FMT_RGB24,
            options.Width,
            options.Height,
            AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);

        if (_sws == null)
        {
            throw new InvalidOperationException("创建编码缩放上下文失败");
        }
    }

    /// <summary>编码一帧 RGB24（长度必须 ≥ Width*Height*3）。</summary>
    public void Encode(ReadOnlySpan<byte> rgb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgb.Length < _width * _height * 3)
        {
            throw new ArgumentException("缓冲区不足一帧 RGB24", nameof(rgb));
        }

        var makeWritable = ffmpeg.av_frame_make_writable(_frame);
        if (makeWritable < 0)
        {
            throw LibavEnvironment.Error("准备编码帧", makeWritable);
        }

        fixed (byte* pixels = rgb)
        {
            var source = new byte*[4];
            var sourceStride = new int[4];
            source[0] = pixels;
            sourceStride[0] = _width * 3;

            var target = new byte*[4];
            var targetStride = new int[4];
            for (uint i = 0; i < 4; i++)
            {
                target[i] = _frame->data[i];
                targetStride[i] = _frame->linesize[i];
            }

            var scaled = ffmpeg.sws_scale(_sws, source, sourceStride, 0, _height, target, targetStride);
            if (scaled < 0)
            {
                throw LibavEnvironment.Error("编码前像素转换", scaled);
            }
        }

        _frame->pts = _pts++;
        var send = ffmpeg.avcodec_send_frame(_codec, _frame);
        if (send < 0)
        {
            throw LibavEnvironment.Error("送编码帧", send);
        }

        Drain();
    }

    /// <summary>冲刷编码器并写输出尾。</summary>
    public void Flush()
    {
        if (_disposed || _codec == null)
        {
            return;
        }

        ffmpeg.avcodec_send_frame(_codec, null);
        Drain();

        if (_format != null)
        {
            ffmpeg.av_write_trailer(_format);
        }
    }

    private void Drain()
    {
        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            if (received < 0)
            {
                throw LibavEnvironment.Error("取编码包", received);
            }

            ffmpeg.av_packet_rescale_ts(_packet, _codec->time_base, _stream->time_base);
            _packet->stream_index = _stream->index;

            var written = ffmpeg.av_interleaved_write_frame(_format, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (written < 0)
            {
                throw LibavEnvironment.Error("写输出", written);
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
            if (format->pb != null)
            {
                ffmpeg.avio_closep(&format->pb);
            }

            ffmpeg.avformat_free_context(format);
            _format = null;
        }
    }
}
