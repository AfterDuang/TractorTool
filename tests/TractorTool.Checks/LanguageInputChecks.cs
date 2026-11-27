using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using TractorTool.Core.Packages.Node;
using TractorTool.Core.Packages.Python;

internal static class LanguageInputChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Language inputs: " + name, () =>
        {
            var directory = Path.Combine(root, "language-input-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); action(directory).GetAwaiter().GetResult();
        });

        Test("Python compares normalized names without accepting a different project", _ =>
        {
            const string parsed = """
                [{"project":{"name":"Demo__.-Tools","version":"1.0.0","requires-python":">=3.13","dependencies":[]}},
                 {"version":1,"package":[{"name":"demo-tools","source":{"virtual":"."}}]}]
                """;
            var definition = new PythonEnvironmentDefinition("", "Test.Python", "1.0.0", "Probe", "worker",
                PythonEnvironmentDefinitions.SupportedPythonVersion, "win-x64", PythonEnvironmentDefinitions.PyPiUrl,
                "", "", "", PythonEnvironmentDefinitions.UvVersion, PythonEnvironmentDefinitions.PythonBuild);
            Invoke("Python.PythonInputPolicy", "Validate", parsed, definition);
            Throws<InvalidDataException>(() => Invoke("Python.PythonInputPolicy", "Validate", parsed.Replace("demo-tools", "other-project"), definition));
            return Task.CompletedTask;
        });

        Test("both language snapshots preserve bytes and reject malformed UTF-8 and wrong hashes", directory =>
        {
            foreach (var node in new[] { false, true })
            {
                var project = Path.Combine(directory, node ? "package.json" : "pyproject.toml");
                var locked = Path.Combine(directory, node ? "pnpm-lock.yaml" : "uv.lock");
                var bytes = Encoding.UTF8.GetBytes("\uFEFForiginal\r\n中文\n");
                File.WriteAllBytes(project, bytes); File.WriteAllBytes(locked, bytes);
                string Read(string projectHash)
                {
                    var lockHash = Hash(File.ReadAllBytes(locked));
                    return node
                        ? NodeEnvironmentDefinitions.Snapshot(new() { Id = "worker", NodeVersion = NodeEnvironmentDefinitions.SupportedNodeVersion,
                            Project = Path.GetFileName(project), Lock = Path.GetFileName(locked), ProjectSha256 = projectHash, LockSha256 = lockHash },
                            "Test.Node", "1.0.0", "Probe", directory, "win-x64").ProjectText
                        : PythonEnvironmentDefinitions.Snapshot(new() { Id = "worker", PythonVersion = PythonEnvironmentDefinitions.SupportedPythonVersion,
                            Project = Path.GetFileName(project), Lock = Path.GetFileName(locked), ProjectSha256 = projectHash, LockSha256 = lockHash },
                            "Test.Python", "1.0.0", "Probe", directory, "win-x64").ProjectText;
                }
                Require(Encoding.UTF8.GetBytes(Read(Hash(bytes))).SequenceEqual(bytes));
                Throws<InvalidDataException>(() => Read(new string('0', 64)));
                byte[] malformed = [0xc3, 0x28]; File.WriteAllBytes(project, malformed);
                Throws<DecoderFallbackException>(() => Read(Hash(malformed)));
                File.WriteAllBytes(project, []);
                Throws<InvalidDataException>(() => Read(Hash([])));
            }
            return Task.CompletedTask;
        });

        Test("download deadline covers a stalled body after response headers", async directory =>
        {
            var stream = new StalledBody();
            using var http = new HttpClient(new ResponseHandler(() => new StreamContent(stream)));
            var download = Download(http, Path.Combine(directory, "tool.zip"), new string('0', 64), CancellationToken.None, TimeSpan.FromSeconds(1));
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await download.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected body timeout."); }
            catch (TimeoutException ex) when (ex.InnerException is OperationCanceledException) { }
            Require(stream.Disposed);
        });

        Test("caller cancellation remains cancellation and closes the stalled response", async directory =>
        {
            var stream = new StalledBody();
            using var http = new HttpClient(new ResponseHandler(() => new StreamContent(stream)));
            using var cancellation = new CancellationTokenSource();
            var download = Download(http, Path.Combine(directory, "tool.zip"), new string('0', 64), cancellation.Token, TimeSpan.FromSeconds(10));
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            try { await download.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(stream.Disposed);
        });

        Test("download verifies the body hash and rejects oversized response headers", async directory =>
        {
            byte[] payload = [1, 2, 3, 4];
            using var http = new HttpClient(new ResponseHandler(() => new ByteArrayContent(payload)));
            var destination = Path.Combine(directory, "valid.zip");
            await Download(http, destination, Hash(payload), CancellationToken.None, TimeSpan.FromSeconds(5));
            Require(File.ReadAllBytes(destination).SequenceEqual(payload));
            await Throws<InvalidDataException>(() => Download(http, Path.Combine(directory, "invalid.zip"), new string('0', 64), CancellationToken.None, TimeSpan.FromSeconds(5)));
            using var oversized = new HttpClient(new ResponseHandler(() =>
            {
                var content = new ByteArrayContent([]); content.Headers.ContentLength = long.MaxValue; return content;
            }));
            var rejected = Path.Combine(directory, "oversized.zip");
            await Throws<InvalidDataException>(() => Download(oversized, rejected, Hash(payload), CancellationToken.None, TimeSpan.FromSeconds(5)));
            Require(!File.Exists(rejected));
        });
    }

    private static Task Download(HttpClient http, string path, string hash, CancellationToken token, TimeSpan timeout) =>
        (Task)Invoke("Environments.EnvironmentDownload", "DownloadAsync", http, "https://example.invalid/tool.zip", path, hash, token, timeout)!;
    private static object? Invoke(string typeName, string method, params object[] args)
    {
        var type = typeof(NodeEnvironmentDefinitions).Assembly.GetType("TractorTool.Core.Packages." + typeName, throwOnError: true)!;
        try { return type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private sealed class ResponseHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content() });
    }
    private sealed class StalledBody : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool condition) { if (!condition) throw new Exception("Language input assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
