using System.Text.Json;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Plugins.MediaCompare.Media;
using TractorTool.Plugins.MediaCompare.ViewModels;

internal static class PackageRequestChecks
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly (string PluginId, PackageReference Package, IReadOnlyDictionary<string, string> Libraries) Declaration = ReadDeclaration();

    internal static async Task RunAsync()
    {
        await DeclineAndRetry();
        await CancelSharedPreparation();
        await CancelWhilePreparing();
        await CancelAfterAcquisition();
        await PackageServiceShutdown();
        await AsyncShutdownBeforeLinkedCancellation();
        await ValidateBeforeLoading();
        await MissingService();
    }

    private static async Task DeclineAndRetry()
    {
        var packages = new Packages((_, _) => Task.FromResult<IPackageLease?>(null));
        var log = new Log();
        await using var model = new CompareViewModel(Context(packages, log));
        await model.EnterViewAsync();
        Require(packages.Requests == 0, "Constructing or entering the empty page must not request a runtime.");
        await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        Require(!model.IsBusy && model.CanOperate && model.ShowStatus && model.HasDependencyNotice && !model.HasError,
            "Declining installation must leave a visible ordinary notice and keep Open available.");
        Require(!model.HasMedia && log.Errors.Count == 0, "Declining installation must not open media or log an error.");
        await model.OpenAsync(MediaSide.B, "not-opened-again.mp4", MediaSourceKind.File);
        Require(packages.Requests == 2 && packages.Last == Declaration.Package,
            "Opening again must request the declared runtime again rather than cache a declined request.");
    }

    private static async Task CancelSharedPreparation()
    {
        var packages = new Packages((_, _) => Task.FromCanceled<IPackageLease?>(new CancellationToken(true)));
        var log = new Log();
        await using var model = new CompareViewModel(Context(packages, log));
        await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        Require(!model.IsBusy && model.CanOperate && model.ShowStatus && model.HasDependencyNotice && !model.HasError && log.Errors.Count == 0,
            "Canceling the shared host download must leave an ordinary retryable notice, even when this caller's token was not canceled.");
    }

    private static async Task CancelWhilePreparing()
    {
        var pending = new TaskCompletionSource<IPackageLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var packages = new Packages((_, token) => pending.Task.WaitAsync(token));
        var log = new Log();
        var model = new CompareViewModel(Context(packages, log));
        var opening = model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        try
        {
            Require(packages.Requests == 1 && model.IsBusy, "The first open must wait for runtime preparation.");
            await Task.WhenAll(model.DisposeAsync().AsTask(), opening).WaitAsync(CheckTimeout);
            Require(!model.IsBusy && !model.HasMedia && log.Errors.Count == 0,
                "Stopping during runtime preparation must cancel the request and finish without an error.");
        }
        finally
        {
            // A broken cancellation chain must fail promptly and still release the simulated request.
            pending.TrySetResult(null);
            await Task.WhenAll(model.DisposeAsync().AsTask(), opening).WaitAsync(CheckTimeout);
        }
    }

    private static async Task CancelAfterAcquisition()
    {
        using var stopping = new CancellationTokenSource();
        var lease = new Lease();
        var packages = new Packages((_, _) =>
        {
            stopping.Cancel();
            return Task.FromResult<IPackageLease?>(lease);
        });
        var log = new Log();
        await using var model = new CompareViewModel(Context(packages, log, stopping.Token));
        await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        Require(lease.Disposed && !lease.Retained && !model.HasMedia && log.Errors.Count == 0,
            "Cancellation after acquisition must dispose the lease without retaining or loading native files.");
    }

    private static async Task PackageServiceShutdown()
    {
        foreach (var error in new Exception[] { new ObjectDisposedException("Packages"), new InvalidOperationException("Installation confirmation was withdrawn.") })
        foreach (var shutdown in new[] { false, true })
        {
            using var stopping = new CancellationTokenSource();
            var packages = new Packages((_, _) =>
            {
                if (shutdown) stopping.Cancel();
                return Task.FromException<IPackageLease?>(error);
            });
            var log = new Log();
            await using var model = new CompareViewModel(Context(packages, log, stopping.Token));
            await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
            Require(!model.IsBusy && !model.HasMedia && (shutdown
                    ? !model.HasError && log.Errors.Count == 0
                    : model.HasError && ReferenceEquals(log.Errors.Single(), error)),
                "Package preparation failures must be ignored only during shutdown; active failures must remain visible.");
        }
    }

    private static async Task AsyncShutdownBeforeLinkedCancellation()
    {
        using var stopping = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cancellation = Task.CompletedTask;
        var beforeLinkedCancellation = false;
        var packages = new Packages(async (_, token) =>
        {
            cancellation = stopping.CancelAsync();
            await callbackEntered.Task.WaitAsync(CheckTimeout);
            beforeLinkedCancellation = stopping.IsCancellationRequested && !token.IsCancellationRequested;
            throw new ObjectDisposedException("Packages");
        });
        var log = new Log();
        var model = new CompareViewModel(Context(packages, log, stopping.Token));
        // Registered last: hold CancelAsync before it invokes the model's linked-token callback.
        using var registration = stopping.Token.Register(() =>
        {
            callbackEntered.SetResult();
            if (!releaseCallback.Wait(CheckTimeout)) throw new TimeoutException("The cancellation callback was not released.");
        });
        var opening = model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        try
        {
            await opening.WaitAsync(CheckTimeout);
            Require(beforeLinkedCancellation,
                "The original shutdown signal must precede propagation to the linked request token.");
            Require(!model.IsBusy && !model.HasError && !model.HasDependencyNotice && log.Errors.Count == 0,
                "A package service closed during asynchronous host cancellation must not report an error or retry notice.");
        }
        finally
        {
            releaseCallback.Set();
            await Task.WhenAll(cancellation, opening, model.DisposeAsync().AsTask()).WaitAsync(CheckTimeout);
        }
    }

    private static async Task ValidateBeforeLoading()
    {
        var invalid = new Lease { Libraries = new Dictionary<string, string>() };
        var expected = new InvalidOperationException("stop before native initialization");
        var valid = new Lease { RetainError = expected };
        var attempt = 0;
        var packages = new Packages((_, _) => Task.FromResult<IPackageLease?>(++attempt == 1 ? invalid : valid));
        var log = new Log();
        await using var model = new CompareViewModel(Context(packages, log));
        await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        Require(invalid.Disposed && !invalid.Retained && model.HasError && log.Errors.Single() is InvalidDataException,
            "A missing declared entrypoint must fail before process retention or native loading.");
        await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        Require(valid.Disposed && valid.Retained && ReferenceEquals(log.Errors.Last(), expected) && !model.HasMedia,
            "Retention must precede native initialization; failed retention must dispose the ordinary lease and remain retryable.");
    }

    private static async Task MissingService()
    {
        var log = new Log();
        await using var model = new CompareViewModel(Context(null, log));
        await model.OpenAsync(MediaSide.A, "not-opened.mp4", MediaSourceKind.File);
        Require(!model.IsBusy && model.CanOperate && model.HasError && log.Errors.Single() is NotSupportedException,
            "An unsupported host must report the missing service without falling back to a bundled runtime.");
    }

    private static (string, PackageReference, IReadOnlyDictionary<string, string>) ReadDeclaration()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MediaCompare.plugin.json")));
        var root = manifest.RootElement;
        var dependency = root.GetProperty("dependencies").EnumerateArray().Single();
        var download = dependency.GetProperty("downloads").EnumerateArray()
            .Single(item => item.GetProperty("rid").GetString() == "win-x64");
        var libraries = download.GetProperty("libraries").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
        return (root.GetProperty("id").GetString()!,
            new(dependency.GetProperty("id").GetString()!, dependency.GetProperty("version").GetString()!), libraries);
    }

    private static PluginContext Context(IExternalPackageService? packages, IAppLog log, CancellationToken stopping = default)
        => new(Declaration.PluginId, new Paths(), new Settings(), log, stopping) { Packages = packages };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Packages(Func<PackageReference, CancellationToken, Task<IPackageLease?>> request) : IExternalPackageService
    {
        internal int Requests { get; private set; }
        internal PackageReference? Last { get; private set; }
        public Task<IPackageLease?> TryRequestAsync(PackageReference package, CancellationToken cancellationToken = default)
        {
            Requests++;
            Last = package;
            return request(package, cancellationToken);
        }
        public PackageStatus GetStatus(PackageReference package) => throw new NotSupportedException();
        public Task<IPackageLease> AcquireAsync(PackageReference package, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Lease : IPackageLease
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "TractorTool-PackageRequestChecks-" + Guid.NewGuid().ToString("N"));
        public IReadOnlyDictionary<string, string> Tools { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> Libraries { get; init; }
        internal bool Retained { get; private set; }
        internal bool Disposed { get; private set; }
        internal Exception? RetainError { get; init; }
        public Lease() => Libraries = Declaration.Libraries.ToDictionary(item => item.Key,
            item => Path.Combine(Directory, item.Value), StringComparer.Ordinal);
        public void RetainUntilProcessExit()
        {
            Retained = true;
            if (RetainError is { } error) throw error;
            throw new InvalidOperationException("This check must stop before native initialization.");
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Log : IAppLog
    {
        internal List<Exception?> Errors { get; } = [];
        public void Write(string source, string message, Exception? error = null)
            => Write(error is null ? AppLogLevel.Info : AppLogLevel.Error, source, message, error);
        public void Write(AppLogLevel level, string source, string message, Exception? error = null)
        {
            if (level == AppLogLevel.Error) Errors.Add(error);
        }
    }

    private sealed class Paths : IAppPaths
    {
        public string AppRoot => Path.GetTempPath();
        public string PluginRoot => throw new NotSupportedException();
        public string DataRoot => throw new NotSupportedException();
        public string PackageRoot => throw new NotSupportedException();
        public string GetPluginDataDirectory(string pluginId) => Path.Combine(AppRoot, "TractorTool-PackageRequestChecks", pluginId);
    }

    private sealed class Settings : IPluginSettingsStore
    {
        public T Load<T>(string name) where T : IVersionedSettings, new() => throw new NotSupportedException();
        public void Save<T>(string name, T value) where T : IVersionedSettings, new() => throw new NotSupportedException();
    }
}
