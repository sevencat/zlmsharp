using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ZlmSharp.Core;

namespace ZlmSharp.Ai;

/// <summary>
/// 常驻解码器池：一路流一个 <see cref="FfmpegLiveDecoder"/>，空闲一段时间自动关闭。
/// 分辨率变化或进程意外退出时自动重建。
/// </summary>
public sealed class FfmpegDecoderPool : IAsyncDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private readonly string _ffmpegPath;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<StreamKey, Slot> _slots = new();
    private readonly Timer _sweeper;
    private int _disposed;

    public FfmpegDecoderPool(string ffmpegPath, ILogger? logger = null)
    {
        _ffmpegPath = ffmpegPath;
        _logger = logger;
        _sweeper = new Timer(_ => _ = SweepAsync(), null, SweepInterval, SweepInterval);
    }

    public int Count => _slots.Count;

    /// <summary>取一路流的最新一帧；超时返回 null（解码器仍在后台运行）。</summary>
    public async Task<RgbImage?> ReadFrameAsync(
        StreamKey key,
        string url,
        int width,
        int height,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var slot = GetOrCreate(key, url, width, height);
        var image = await slot.Decoder.ReadFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (image is not null)
        {
            Interlocked.Exchange(ref slot.LastUsedTicks, DateTime.UtcNow.Ticks);
            return image;
        }

        if (slot.Decoder.IsRunning)
        {
            // 进程活着但这次没出帧（启动中/网络抖动），交给调用方按失败处理，保留进程
            return null;
        }

        _logger?.LogWarning("常驻解码器已退出，重建 {Key}：{Error}", key, slot.Decoder.LastError);
        Remove(key);

        slot = GetOrCreate(key, url, width, height);
        image = await slot.Decoder.ReadFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (image is not null)
        {
            Interlocked.Exchange(ref slot.LastUsedTicks, DateTime.UtcNow.Ticks);
        }

        return image;
    }

    /// <summary>关闭并移除某路流的解码器（流结束时调用）。</summary>
    public void Remove(StreamKey key)
    {
        if (_slots.TryRemove(key, out var slot))
        {
            _ = slot.Decoder.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sweeper.Dispose();

        foreach (var pair in _slots)
        {
            if (_slots.TryRemove(pair))
            {
                await pair.Value.Decoder.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Slot GetOrCreate(StreamKey key, string url, int width, int height)
    {
        while (true)
        {
            if (_slots.TryGetValue(key, out var existing))
            {
                if (existing.Width == width && existing.Height == height && existing.Decoder.IsRunning)
                {
                    return existing;
                }

                if (_slots.TryRemove(new KeyValuePair<StreamKey, Slot>(key, existing)))
                {
                    _ = existing.Decoder.DisposeAsync();
                }

                continue;
            }

            var decoder = FfmpegLiveDecoder.Start(_ffmpegPath, url, width, height);
            var slot = new Slot(decoder)
            {
                Width = width,
                Height = height,
                LastUsedTicks = DateTime.UtcNow.Ticks,
            };

            if (_slots.TryAdd(key, slot))
            {
                _logger?.LogInformation("启动常驻解码器 {Key}（{Width}x{Height}）", key, width, height);
                return slot;
            }

            _ = decoder.DisposeAsync();
        }
    }

    private async Task SweepAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var deadline = DateTime.UtcNow.Ticks - IdleTimeout.Ticks;

        foreach (var pair in _slots)
        {
            var slot = pair.Value;
            var idle = Interlocked.Read(ref slot.LastUsedTicks) < deadline;
            if (!idle && slot.Decoder.IsRunning)
            {
                continue;
            }

            if (_slots.TryRemove(pair))
            {
                _logger?.LogInformation("关闭常驻解码器 {Key}（空闲或已退出）", pair.Key);
                await slot.Decoder.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class Slot
    {
        public Slot(FfmpegLiveDecoder decoder) => Decoder = decoder;

        public FfmpegLiveDecoder Decoder { get; }

        public int Width { get; init; }

        public int Height { get; init; }

        public long LastUsedTicks;
    }
}
