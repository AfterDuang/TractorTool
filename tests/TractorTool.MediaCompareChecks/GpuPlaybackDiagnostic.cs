using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.Views;

// Opt-in real Win32/GPU diagnostic. The default checks never initialize Avalonia or native media.
internal static class GpuPlaybackDiagnostic
{
    private static DiagnosticRun? _run;

    internal static int Run(string[] args)
    {
        if (args.Length != 5 || args[0] != "--gpu-playback")
        {
            Console.Error.WriteLine("Usage: --gpu-playback <runtimeRoot> <newScratchDirectory> <syntheticA> <syntheticB>");
            return 2;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The GPU diagnostic requires Windows.");
        foreach (var path in args.Skip(1))
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Diagnostic paths must be absolute.");
        if (Directory.Exists(args[2]) || File.Exists(args[2]))
            throw new IOException("Choose a new scratch directory; diagnostics never reuse existing application data.");
        if (!Directory.Exists(args[1]) || !File.Exists(args[3]) || !File.Exists(args[4]))
            throw new FileNotFoundException("Runtime or synthetic input is missing.");

        using var run = new DiagnosticRun(args[1], args[2], args[3], args[4]);
        _run = run;
        var exitCode = 1;
        var thread = new Thread(() =>
        {
            try
            {
                exitCode = AppBuilder.Configure<DiagnosticApplication>()
                    .UsePlatformDetect()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { run.Write("app-failed", error.ToString()); }
        }) { Name = "MediaCompare diagnostic UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A native deadlock cannot be made cancellable by a managed timeout. The launcher
        // must monitor diagnostic.log and terminate this separate process after capturing stacks.
        thread.Join();
        _run = null;
        return exitCode;
    }

    private sealed class DiagnosticApplication : Application
    {
        public override void Initialize() => Styles.Add(new FluentAvaloniaTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            var run = _run ?? throw new InvalidOperationException("Missing diagnostic configuration.");
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                throw new InvalidOperationException("The diagnostic requires a desktop lifetime.");
            var preview = new ComparePreview();
            var window = new Window
            {
                Title = "TractorTool isolated GPU playback diagnostic",
                Width = 640, Height = 360,
                Position = new PixelPoint(-30000, -30000),
                ShowActivated = false, ShowInTaskbar = false,
                WindowDecorations = WindowDecorations.None,
                Content = preview
            };
            lifetime.MainWindow = window;
            window.Opened += async (_, _) => await run.ExecuteAsync(lifetime, window, preview);
            base.OnFrameworkInitializationCompleted();
        }
    }

    private sealed class DiagnosticRun : IDisposable
    {
        private readonly string _runtime, _scratch, _mediaA, _mediaB;
        private readonly object _logGate = new();
        private readonly StreamWriter _log;
        private readonly System.Threading.Timer _heartbeat;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private long _uiHeartbeat, _receivedFrames, _submittedFrames;
        private long _maxUiGap;
        private Exception? _failure;
        private string _stage = "startup";
        private bool _disposed;

        internal DiagnosticRun(string runtime, string scratch, string mediaA, string mediaB)
        {
            _runtime = runtime; _scratch = scratch; _mediaA = mediaA; _mediaB = mediaB;
            Directory.CreateDirectory(scratch);
            _log = new StreamWriter(Path.Combine(scratch, "diagnostic.log"), append: false) { AutoFlush = true };
            Write("process", $"pid={Environment.ProcessId}; mainThread={Environment.CurrentManagedThreadId}; scratch={scratch}");
            _heartbeat = new System.Threading.Timer(_ =>
                Write("heartbeat", $"stage={Volatile.Read(ref _stage)}; uiAgeMs={_elapsed.ElapsedMilliseconds - Interlocked.Read(ref _uiHeartbeat)}; frames={Interlocked.Read(ref _receivedFrames)}; submitted={Interlocked.Read(ref _submittedFrames)}"),
                null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        internal void Write(string kind, string text)
        {
            lock (_logGate)
            {
                if (_disposed) return;
                var line = $"{DateTimeOffset.Now:O} t={Environment.CurrentManagedThreadId} {kind}: {text}";
                _log.WriteLine(line);
                Console.WriteLine(line);
            }
        }

        private void Stage(string stage)
        {
            Volatile.Write(ref _stage, stage);
            Write("stage", stage);
        }

        private void RecordFailure(string kind, string message)
        {
            Interlocked.CompareExchange(ref _failure, new InvalidOperationException($"{kind}: {message}"), null);
            Write(kind, message);
        }

        private void CheckFailure()
        {
            if (Volatile.Read(ref _failure) is { } failure) throw failure;
        }

        private async Task ObservePlaybackAsync(ComparisonSession session)
        {
            var start = session.Position;
            var frames = Interlocked.Read(ref _submittedFrames);
            await Task.Delay(3000);
            CheckFailure();
            var advance = session.Position - start;
            Write("playback-observed", $"advanceMs={advance.TotalMilliseconds:F0}; submittedDelta={Interlocked.Read(ref _submittedFrames) - frames}; maxUiGapMs={Interlocked.Read(ref _maxUiGap)}");
            if (advance < TimeSpan.FromSeconds(1) || Interlocked.Read(ref _submittedFrames) - frames < 10)
                throw new InvalidOperationException("Playback did not advance through the synthetic media.");
            if (Interlocked.Read(ref _maxUiGap) > 1000)
                throw new InvalidOperationException("UI heartbeat stalled for more than one second.");
        }

        internal async Task ExecuteAsync(IClassicDesktopStyleApplicationLifetime lifetime, Window window, ComparePreview preview)
        {
            ComparisonSession? session = null;
            Task? stopTask = null;
            var success = false;
            var previewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void AvailabilityChanged(bool available) { if (available) previewReady.TrySetResult(); }
            void PreviewFailed(string message)
            {
                RecordFailure("preview-error", message);
                previewReady.TrySetException(new InvalidOperationException(message));
            }
            void Receive(FramePair pair)
            {
                Interlocked.Increment(ref _receivedFrames);
                preview.Submit(pair);
                Interlocked.Increment(ref _submittedFrames);
            }
            Interlocked.Exchange(ref _uiHeartbeat, _elapsed.ElapsedMilliseconds);
            var uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
            {
                var now = _elapsed.ElapsedMilliseconds;
                var gap = now - Interlocked.Exchange(ref _uiHeartbeat, now);
                if (gap > Interlocked.Read(ref _maxUiGap)) Interlocked.Exchange(ref _maxUiGap, gap);
            });
            preview.AvailabilityChanged += AvailabilityChanged;
            preview.Error += PreviewFailed;
            preview.RendererStatusChanged += status => Write("renderer", status);
            if (preview.IsAvailable) previewReady.TrySetResult();
            try
            {
                Stage("wait-gpu-preview");
                await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Stage("create-session");
                session = await Task.Run(() => new ComparisonSession(_runtime, Path.Combine(_scratch, "cache")));
                session.FrameReady += Receive;
                session.Error += error => RecordFailure("session-error", error);
                session.PlayingChanged += playing => Write("playing", playing.ToString());
                Stage("open-A");
                await session.OpenAsync(MediaSide.A, new(_mediaA)).WaitAsync(TimeSpan.FromSeconds(35));
                Stage("open-B");
                await session.OpenAsync(MediaSide.B, new(_mediaB)).WaitAsync(TimeSpan.FromSeconds(35));
                Stage("play-first");
                Interlocked.Exchange(ref _maxUiGap, 0);
                await session.SetPlayingAsync(true).WaitAsync(TimeSpan.FromSeconds(35));
                await ObservePlaybackAsync(session);
                Stage("pause");
                await session.SetPlayingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
                Stage("seek");
                await session.SeekAsync(TimeSpan.FromSeconds(Math.Min(1, session.Duration.TotalSeconds / 2)))
                    .WaitAsync(TimeSpan.FromSeconds(35));
                Stage("play-second");
                Interlocked.Exchange(ref _maxUiGap, 0);
                await session.SetPlayingAsync(true).WaitAsync(TimeSpan.FromSeconds(35));
                await ObservePlaybackAsync(session);
                Stage("stop");
                session.FrameReady -= Receive;
                stopTask = session.DisposeAsync().AsTask();
                await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
                session = null;
                Stage("detach-preview");
                window.Content = null;
                await Task.Delay(500);
                CheckFailure();
                Write("scope", "Decoded position, UI responsiveness and decoder stop observed; Submit count is not visual acceptance, and preview detach does not await GPU disposal.");
                Stage("complete");
                success = true;
            }
            catch (Exception error) { Stage("failed"); Write("failure", error.ToString()); }
            finally
            {
                if (session is not null)
                {
                    session.FrameReady -= Receive;
                    try
                    {
                        stopTask ??= session.DisposeAsync().AsTask();
                        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception error) { Write("cleanup-failed", error.ToString()); }
                }
                uiTimer.Stop();
                preview.AvailabilityChanged -= AvailabilityChanged;
                preview.Error -= PreviewFailed;
                lifetime.Shutdown(success ? 0 : 1);
            }
        }

        public void Dispose()
        {
            _heartbeat.Dispose();
            lock (_logGate) { _disposed = true; _log.Dispose(); }
        }
    }
}
