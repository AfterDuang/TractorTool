using TractorTool.Contracts.Processes;

namespace TractorTool.Plugins.VideoExport;

public sealed record FileStamp(string Path, long Length, DateTime Modified)
{
    public static FileStamp Read(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new IOException("请选择本地文件的完整路径。");
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("所选文件已不存在，请重新选择。");
        return new(file.FullName, file.Length, file.LastWriteTimeUtc);
    }
    public void Verify()
    {
        if (Read(Path) != this) throw new IOException("处理期间输入文件发生变化，请重新选择或读取。");
    }
}

// All media policy remains in this plugin; the host owns only package leases and process execution.
public sealed class VideoExporter(IProcessRunner processes, Action<Exception> cleanupError)
{
    public async Task<MediaInfo> ProbeAsync(string ffprobe, string path, CancellationToken token)
    {
        var result = await processes.RunAsync(new(ffprobe,
            ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-show_programs", "-of", "json", "-i", path])
            { Timeout = TimeSpan.FromSeconds(30), OutputLimit = 2 * 1024 * 1024 }, token).ConfigureAwait(false);
        CheckResult(result, "读取媒体信息");
        if (result.StandardOutputTruncated) throw new InvalidDataException("媒体信息超出读取上限，未使用不完整结果。");
        return MediaProbe.Parse(result.StandardOutput);
    }

    public async Task<string> ExportAsync(IReadOnlyDictionary<string, string> tools, FileStamp source, MediaInfo info,
        ExportOptions options, string? cover, string directory, Action<double> progress, Action<string> status, CancellationToken token,
        bool useGpu = false, Action<string>? executionInfo = null)
    {
        _ = ExportPlan.Validate(info, options, cover);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("请选择已存在的输出文件夹。");
        source.Verify();
        FileStamp? coverStamp = options.UseCover ? FileStamp.Read(cover!) : null;
        var work = Path.Combine(directory, ".tractortool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var temporary = Path.Combine(work, "output" + ExportPlan.Extension(options.Container));
        var probeOutput = Path.Combine(work, "check" + ExportPlan.Extension(options.Container));
        var passLog = Path.Combine(work, "pass");
        var format = ExportPlan.Format(options.Container);
        var encoder = format.VideoEncoder;
        var twoPass = options.RateControl == "vbr2";
        string? coverCopy = null;
        try
        {
            if (coverStamp is not null)
            {
                coverCopy = Path.Combine(work, "cover" + Path.GetExtension(coverStamp.Path));
                await using (var input = new FileStream(coverStamp.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var output = new FileStream(coverCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                coverStamp.Verify();
            }
            if (useGpu && (twoPass || format.VideoCodec != "h264"))
            {
                executionInfo?.Invoke(twoPass ? "使用 CPU：VBR 两次需要先分析整段视频，再编码。" : "使用 CPU：此输出格式尚未接入 GPU 编码。");
            }
            else if (useGpu)
            {
                foreach (var candidate in ExportPlan.HardwareEncoders)
                {
                    token.ThrowIfCancellationRequested();
                    status("正在检查 GPU 加速…");
                    try
                    {
                        var check = await processes.RunAsync(new(tools["ffmpeg"],
                            ExportPlan.Arguments(source.Path, info, options, coverCopy, probeOutput, hardwareCheck: true, encoder: candidate))
                            { Timeout = TimeSpan.FromSeconds(45) }, token).ConfigureAwait(false);
                        CheckResult(check, "GPU 检查");
                        if (!File.Exists(probeOutput) || new FileInfo(probeOutput).Length == 0)
                            throw new InvalidDataException("GPU 检查未生成有效样本。");
                        encoder = candidate;
                        break;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or TimeoutException) { /* Try the next device, then CPU. */ }
                    finally { if (File.Exists(probeOutput)) File.Delete(probeOutput); }
                }
                executionInfo?.Invoke(encoder == format.VideoEncoder ? "使用 CPU：当前设备无法完成此格式的 GPU 编码检查。" : "本次使用 GPU 加速。");
            }
            source.Verify();
            if (twoPass)
            {
                status("第 1 / 2 次：分析视频…");
                CheckResult(await EncodeAsync(1, value => progress(value * 0.5)).ConfigureAwait(false), "第一遍分析");
                source.Verify(); token.ThrowIfCancellationRequested();
            }
            status(twoPass ? "第 2 / 2 次：编码视频…" : "正在导出…");
            var encoded = await EncodeAsync(twoPass ? 2 : 0, value => progress(twoPass ? 50 + value * 0.49 : value)).ConfigureAwait(false);
            if (encoded.ExitCode != 0 && encoder != format.VideoEncoder)
            {
                // A driver can fail after its short capability check. Retry once, retaining an explicit explanation.
                token.ThrowIfCancellationRequested(); source.Verify();
                if (File.Exists(temporary)) File.Delete(temporary);
                encoder = format.VideoEncoder;
                executionInfo?.Invoke("使用 CPU：GPU 导出失败，已自动重新编码。");
                status("正在使用 CPU 重新导出…"); progress(0);
                encoded = await EncodeAsync(0, progress).ConfigureAwait(false);
            }
            CheckResult(encoded, "视频导出");
            status("正在检查输出…");
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidDataException("编码器未生成视频文件。");
            var outputInfo = await ProbeAsync(tools["ffprobe"], temporary, token).ConfigureAwait(false);
            ExportPlan.ValidateOutput(info, outputInfo, options);
            source.Verify(); token.ThrowIfCancellationRequested();
            var destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(source.Path) + "_导出" + ExportPlan.Extension(options.Container));
            // Atomic, no-overwrite commit on the target filesystem. A concurrent name collision remains a failure.
            if (File.Exists(destination) || Directory.Exists(destination))
                destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(source.Path) + "_导出_" + Guid.NewGuid().ToString("N")[..8] + ExportPlan.Extension(options.Container));
            File.Move(temporary, destination, overwrite: false);
            return destination;

            Task<ProcessResult> EncodeAsync(int pass, Action<double> report)
            {
                var parser = new ExportProgress(info.Duration!.Value, report);
                return processes.RunAsync(new(tools["ffmpeg"],
                    ExportPlan.Arguments(source.Path, info, options, coverCopy, temporary, encoder: encoder, pass: pass, passLog: passLog))
                    { StandardOutputReceived = parser.Receive }, token);
            }
        }
        finally
        {
            // Only files created by this invocation; never recurse through the user's output directory.
            foreach (var file in new[] { temporary, probeOutput, coverCopy, passLog + "-0.log", passLog + "-0.log.mbtree",
                passLog + "-0.log.temp", passLog + "-0.log.mbtree.temp" })
                try { if (file is not null && File.Exists(file)) File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupError(ex); }
            try { Directory.Delete(work, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupError(ex); }
        }
    }

    private static void CheckResult(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var detail = result.StandardError.Length == 0 ? $"退出码 {result.ExitCode}" : result.StandardError[..Math.Min(result.StandardError.Length, 1200)];
        throw new InvalidDataException(operation + "失败：" + detail);
    }
}
