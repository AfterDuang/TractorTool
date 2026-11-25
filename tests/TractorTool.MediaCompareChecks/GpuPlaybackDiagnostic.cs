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
            var preview = new ComparePreview { Lifetime = new PreviewLifetime() };
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
        private long _uiHeartbeat, _receivedFrames, _submittedFrames, _publishedPosition;
        private long _maxUiGap;
        private int _frameSides;
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

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static async Task ObserveCanceledSeekAsync(Task seek, CancellationToken token)
        {
            try { await seek.WaitAsync(TimeSpan.FromSeconds(35)); }
            // Completion before cancellation is valid; a superseded request may also be
            // skipped without throwing. Other failures must still fail the diagnostic.
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private async Task CheckSessionInteractionsAsync(ComparisonSession session)
        {
            Stage("seek-multiple");
            await session.SetPlayingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var seconds in new[] { .2, .7, 1.3, 2.05, .4, 3.1, 4.2, 5.9, 8.2, 10.1, .9 })
            {
                var target = TimeSpan.FromSeconds(Math.Min(seconds, session.Duration.TotalSeconds - .1));
                var received = Interlocked.Read(ref _receivedFrames);
                await session.SeekAsync(target).WaitAsync(TimeSpan.FromSeconds(35));
                CheckFailure();
                Require(session.Position == target && !session.IsPlaying && Volatile.Read(ref _frameSides) == 3 &&
                    Interlocked.Read(ref _receivedFrames) > received && Interlocked.Read(ref _publishedPosition) == target.Ticks,
                    "Seeking did not publish both paused media at the requested position.");
                Write("seek-observed", $"positionMs={session.Position.TotalMilliseconds:F0}");
            }

            Stage("seek-latest-after-cancellation");
            using (var first = new CancellationTokenSource())
            using (var second = new CancellationTokenSource())
            using (var third = new CancellationTokenSource())
            {
                var firstSeek = session.SeekAsync(TimeSpan.FromSeconds(.2), first.Token);
                first.Cancel();
                var secondSeek = session.SeekAsync(TimeSpan.FromSeconds(1.3), second.Token);
                second.Cancel();
                var thirdSeek = session.SeekAsync(TimeSpan.FromSeconds(.4), third.Token);
                third.Cancel();
                var target = TimeSpan.FromSeconds(Math.Min(.7, session.Duration.TotalSeconds / 2));
                var received = Interlocked.Read(ref _receivedFrames);
                var latest = session.SeekAsync(target);
                await Task.WhenAll(ObserveCanceledSeekAsync(firstSeek, first.Token),
                    ObserveCanceledSeekAsync(secondSeek, second.Token),
                    ObserveCanceledSeekAsync(thirdSeek, third.Token), latest).WaitAsync(TimeSpan.FromSeconds(35));
                Require(session.Position == target && !session.IsPlaying &&
                    Interlocked.Read(ref _receivedFrames) > received && Interlocked.Read(ref _publishedPosition) == target.Ticks,
                    "An obsolete seek replaced the latest requested position.");
            }

            Stage("audio-selection-and-clear-A");
            foreach (var source in new[] { AudioSource.Muted, AudioSource.A, AudioSource.B, AudioSource.A })
            {
                await session.SetAudioAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
                Require(session.SelectedAudio == source, "The session must report the committed audio selection.");
            }
            await session.SetPlayingAsync(true).WaitAsync(TimeSpan.FromSeconds(35));
            await Task.Delay(150);
            var retainedB = session.B;
            var retainedPosition = session.Position;
            await session.ClearAsync(MediaSide.A).WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.A is null && retainedB is not null && session.B == retainedB &&
                session.Duration == retainedB.Duration && session.Position >= retainedPosition &&
                Interlocked.Read(ref _publishedPosition) == session.Position.Ticks &&
                !session.IsPlaying && session.SelectedAudio == AudioSource.Muted && Volatile.Read(ref _frameSides) == 2,
                "Clearing A did not retain paused B or publish an empty A preview.");
            CheckFailure();
            Write("clear-A-observed", $"B={retainedB?.Name}; positionMs={session.Position.TotalMilliseconds:F0}");

            Stage("play-B-only");
            await session.SetAudioAsync(AudioSource.B).WaitAsync(TimeSpan.FromSeconds(10));
            Interlocked.Exchange(ref _maxUiGap, 0);
            await session.SetPlayingAsync(true).WaitAsync(TimeSpan.FromSeconds(35));
            await ObservePlaybackAsync(session);
            await session.SetPlayingAsync(false).WaitAsync(TimeSpan.FromSeconds(10));

            Stage("clear-all-with-pending-seek");
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = session.SeekAsync(TimeSpan.FromSeconds(.2), cancellation.Token);
                cancellation.Cancel();
                var clear = session.ClearAsync();
                await Task.WhenAll(ObserveCanceledSeekAsync(pending, cancellation.Token), clear)
                    .WaitAsync(TimeSpan.FromSeconds(35));
            }
            await Task.Delay(100);
            CheckFailure();
            Require(session.A is null && session.B is null && session.Duration == TimeSpan.Zero &&
                session.Position == TimeSpan.Zero && !session.IsPlaying && session.SelectedAudio == AudioSource.Muted &&
                Volatile.Read(ref _frameSides) == 0,
                "Clearing all media did not restore the empty stopped session.");

            Stage("reopen-and-play");
            await session.OpenAsync(MediaSide.A, new(_mediaA)).WaitAsync(TimeSpan.FromSeconds(35));
            await session.SetAudioAsync(AudioSource.Muted).WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.A is not null && session.B is null && Volatile.Read(ref _frameSides) == 1,
                "Reopening after clearing did not publish the new A-only preview.");
            Interlocked.Exchange(ref _maxUiGap, 0);
            await session.SetPlayingAsync(true).WaitAsync(TimeSpan.FromSeconds(35));
            await ObservePlaybackAsync(session);
            Write("interaction-scope", "Seek cancellation, side/all clearing, reopen and committed audio/mute state checked; speaker output is not measured.");
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
                Interlocked.Exchange(ref _publishedPosition, pair.Position.Ticks);
                Interlocked.Increment(ref _receivedFrames);
                Volatile.Write(ref _frameSides, (pair.A is null ? 0 : 1) | (pair.B is null ? 0 : 2));
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
                Stage("rapid-preview-reattach");
                previewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Content = null;
                window.Content = preview;
                window.Content = null;
                window.Content = preview;
                await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Stage("drag-event-routing");
                InteractionChecks.CheckEmptyDragRouting();
                await PreviewLifetimeChecks.RunAsync();
                Write("drag-routing-observed", "Empty DragEnter/DragOver/Drop bubbled from Preview and were rejected; this does not exercise native Explorer file transfer.");
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
                await CheckSessionInteractionsAsync(session);
                Stage("stop");
                // Refresh queues a pair while disposal starts, exercising in-flight presentation.
                await session.RefreshPreviewAsync().WaitAsync(TimeSpan.FromSeconds(10));
                session.FrameReady -= Receive;
                var previewStop = preview.Lifetime!.DisposeAsync().AsTask();
                stopTask = session.DisposeAsync().AsTask();
                await Task.WhenAll(stopTask, previewStop).WaitAsync(TimeSpan.FromSeconds(15));
                session = null;
                Stage("detach-preview");
                window.Content = null;
                await preview.DisposeAsync();
                window.Content = preview;
                Require(!preview.IsAvailable, "A stopped preview must not create a new renderer on reattachment.");
                window.Content = null;
                Stage("stop-during-preview-initialization");
                var initializationLifetime = new PreviewLifetime();
                var initializing = new ComparePreview { Lifetime = initializationLifetime };
                window.Content = initializing;
                window.Content = null;
                await initializationLifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                await initializing.DisposeAsync();
                Require(!initializing.IsAvailable, "Stop must await retirement of an initializing preview.");
                CheckFailure();
                Write("scope", "Decoded position, UI responsiveness, decoder stop and awaited preview retirement observed; Submit count is not visible UI, audio synchronization or driver allocation acceptance.");
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
                try { await preview.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { success = false; Write("preview-cleanup-failed", error.ToString()); }
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
