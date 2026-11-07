using ZlmSharp.Cli;
using ZlmSharp.Media.AutoGen;

// 与 zlmsharp 相同的宿主，只把媒体实现从 ffmpeg.exe 子进程换成进程内 libav（ffmpeg 8.x dll）。
// dll 目录来自 ZLM_FFMPEG_BIN 指向的 ffmpeg.exe 所在目录，故 dll 与 ffmpeg.exe 放在一起即可。
CliApplication.ConfigureMedia = services => services.AddAutoGenMediaToolkit();

return await CliApplication.RunAsync(args).ConfigureAwait(false);
