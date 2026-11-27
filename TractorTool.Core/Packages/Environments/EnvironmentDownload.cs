using System.Security.Cryptography;

namespace TractorTool.Core.Packages.Environments;

internal static class EnvironmentDownload
{
    internal static async Task DownloadAsync(HttpClient http, string url, string destination, string sha256,
        CancellationToken callerToken, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("TractorTool/1.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > PackageFiles.MaxDownload)
                throw new InvalidDataException("依赖工具链下载地址或大小无效。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; long length = 0; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                length += count;
                if (length > PackageFiles.MaxDownload) throw new InvalidDataException("依赖工具链下载超过大小限制。");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("依赖工具链附件 SHA256 校验失败。");
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        { throw new TimeoutException("依赖工具链下载超时，请重试。", ex); }
    }
}
