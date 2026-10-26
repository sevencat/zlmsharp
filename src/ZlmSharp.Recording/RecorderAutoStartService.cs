using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZlmSharp.Configuration;
using ZlmSharp.Core;

namespace ZlmSharp.Recording;

/// <summary>
/// <c>protocol.enable_mp4 / enable_flv_record</c> 打开时，自动为在线流录像。
/// 跳过 <c>*_yolo</c> 叠加流，避免把派生流也录一份。
/// </summary>
internal sealed class RecorderAutoStartService : BackgroundService
{
    private const string OverlaySuffix = "_yolo";

    private readonly RecorderManager _manager;
    private readonly MediaSourceRegistry _registry;
    private readonly AppOptions _options;
    private readonly ILogger? _logger;

    public RecorderAutoStartService(
        RecorderManager manager,
        MediaSourceRegistry registry,
        AppOptions options,
        ILogger? logger = null)
    {
        _manager = manager;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    AutoStart();
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning(exception, "自动录像巡检失败");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AutoStart()
    {
        var formats = RecordFormat.None;
        if (_options.Protocol.EnableMp4)
        {
            formats |= RecordFormat.Mp4;
        }

        if (_options.Protocol.EnableFlvRecord)
        {
            formats |= RecordFormat.Flv;
        }

        if (formats == RecordFormat.None)
        {
            return;
        }

        foreach (var source in _registry.Sources)
        {
            var key = source.Key;
            if (source.IsClosed || key.Stream.EndsWith(OverlaySuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_manager.Get(key)?.Recording == true)
            {
                continue;
            }

            try
            {
                _manager.Start(key, formats);
            }
            catch (InvalidOperationException)
            {
                // 还没收到关键帧/参数集，下一轮再试
            }
        }
    }
}
