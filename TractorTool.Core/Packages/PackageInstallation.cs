using TractorTool.Contracts.Packages;

namespace TractorTool.Core.Packages;

public sealed partial class ExternalPackageService
{
    private async Task<long> PreparePackageContentAsync(Entry entry, PackageDefinition definition, string work,
        string staging, CancellationToken token)
    {
        if (definition.Archives.Count == 0)
        {
            var zip = Path.Combine(work, "download.zip");
            await DownloadArchiveAsync(entry, definition, definition.DownloadUri, definition.Sha256, zip, 0, true, token).ConfigureAwait(false);
            SetState(entry, PackagePhase.Installing);
            Directory.CreateDirectory(staging);
            return await PackageFiles.ExtractAsync(zip, staging, token).ConfigureAwait(false);
        }

        List<(string Path, PackageArchive Definition)> archives = [];
        long downloaded = 0;
        foreach (var archive in definition.Archives)
        {
            var zip = Path.Combine(work, $"download-{archives.Count}.zip");
            downloaded += await DownloadArchiveAsync(entry, definition, archive.Url, archive.Sha256, zip,
                downloaded, archives.Count == definition.Archives.Count - 1, token).ConfigureAwait(false);
            archives.Add((zip, archive));
        }
        SetState(entry, PackagePhase.Installing);
        Directory.CreateDirectory(staging);
        return await PackageFiles.ExtractMappedAsync(archives, staging, token).ConfigureAwait(false);
    }

    private async Task<long> DownloadArchiveAsync(Entry entry, PackageDefinition definition, Uri url, string sha256,
        string destination, long downloadedBefore, bool lastArchive, CancellationToken token)
    {
        try
        {
            SetState(entry, PackagePhase.Downloading);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var resolved = response.RequestMessage?.RequestUri;
            if (resolved is not { Scheme: "https" }) throw new InvalidDataException("下载不能重定向到非 HTTPS 地址。");
            if (definition.Archives.Count != 0)
                PackageArchiveDefinitions.ValidateDownloadUri(definition.SourceKind, definition.SourceUri, resolved);
            var length = response.Content.Headers.ContentLength;
            if (length > PackageFiles.MaxDownload - downloadedBefore) throw new InvalidDataException("下载总大小超过限制。");
            long received = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long lastNotification = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > PackageFiles.MaxDownload - downloadedBefore) throw new InvalidDataException("下载总大小超过限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        entry.Downloaded = downloadedBefore + received;
                        entry.Total = lastArchive && length is not null ? downloadedBefore + length : null;
                    }
                    // Keep transfer notifications bounded while totals accumulate across archives.
                    if (Environment.TickCount64 - lastNotification >= 100) { Notify(); lastNotification = Environment.TickCount64; }
                }
            }
            if (length is not null && received != length) throw new InvalidDataException("下载不完整。");
            SetState(entry, PackagePhase.Verifying);
            if (!string.Equals(await HashAsync(destination, token).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 校验失败，未安装下载内容。");
            return received;
        }
        catch (OperationCanceledException ex) when (definition.Archives.Count != 0 && !token.IsCancellationRequested)
        { throw new TimeoutException($"下载归档 {url} 超时，请重试。", ex); }
        catch (HttpRequestException ex) when (definition.Archives.Count != 0)
        { throw new HttpRequestException($"下载归档 {url} 失败：{ex.Message}", ex, ex.StatusCode); }
        catch (InvalidDataException ex) when (definition.Archives.Count != 0)
        { throw new InvalidDataException($"下载归档 {url} 失败：{ex.Message}", ex); }
        catch (IOException ex) when (definition.Archives.Count != 0)
        { throw new IOException($"下载归档 {url} 失败：{ex.Message}", ex); }
    }
}
