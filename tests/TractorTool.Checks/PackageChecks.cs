using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TractorTool.Contracts;
using TractorTool.Contracts.Packages;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Packages;
using TractorTool.Core.Plugins;
using TractorTool.Core.Storage;
using System.Text.Json;

internal static class PackageChecks
{
    public static void Run(string root, Action<string, Action> check)
    {
        void Test(string name, Func<string, Task> action) => check("Packages: " + name, () =>
            action(Path.Combine(root, "packages-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult());

        Test("install, lease blocks removal, and reload", async path =>
        {
            using var http = Client(Archive());
            var runner = new Probe();
            await using (var service = Create(path, http, runner))
            {
                await service.InstallAsync(Key);
                Equal(service.GetStatus(Key).Phase, PackagePhase.Ready);
                using var scope = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
                using (var lease = await scope.AcquireAsync(Key))
                {
                    Require(File.Exists(lease.Tools["tool"]));
                    await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
                    Require(service.GetStatus(Key).Users.Single() == "Test.Plugin");
                }
                Require(service.GetStatus(Key).Users.Count == 0 && runner.Calls == 1);
                Require(runner.Requests.Single().Arguments.SequenceEqual(new[] { "-version" }));
            }
            await using var reload = Create(path, http, runner);
            Equal(reload.GetStatus(Key).Phase, PackagePhase.Ready);
            await reload.RemoveAsync(Key);
            Equal(reload.GetStatus(Key).Phase, PackagePhase.Missing);
        });
        Test("declining consent returns normally; retry and installed reuse work", async path =>
        {
            var calls = 0; var prompts = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            { calls++; return Task.FromResult(Response(request, Archive())); }));
            await using var service = Create(path, http, new Probe());
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(false); };
            using var scope = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
            var exceptions = 0;
            void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
            { if (args.Exception is OperationCanceledException) Interlocked.Increment(ref exceptions); }
            AppDomain.CurrentDomain.FirstChanceException += Observe;
            try { Require(await scope.TryRequestAsync(Key) is null); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
            Require(calls == 0 && prompts == 1 && exceptions == 0 && service.GetStatus(Key).Users.Count == 0);
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
            using (var lease = await scope.TryRequestAsync(Key)) { Require(lease is not null); }
            using (var lease = await scope.TryRequestAsync(Key)) { Require(lease is not null); }
            Require(calls == 1 && prompts == 2);
            await service.RemoveAsync(Key); // A refusal/retry must not leak a reservation or lease.
        });
        Test("API 1.3 retains cancellation on declined consent", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            service.ConfirmInstallAsync = (_, _) => Task.FromResult(false);
            using var scope = service.CreateScope("Legacy.Plugin", [Key], CancellationToken.None);
            await Throws<OperationCanceledException>(() => scope.RequestAsync(Key));
            Require(service.GetStatus(Key).Phase == PackagePhase.Missing);
        });
        Test("shared declined consent returns null to every requester", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompts = 0;
            service.ConfirmInstallAsync = async (_, token) => { prompts++; entered.TrySetResult(); return await answer.Task.WaitAsync(token); };
            using var firstScope = service.CreateScope("First", [Key], CancellationToken.None);
            using var secondScope = service.CreateScope("Second", [Key], CancellationToken.None);
            var first = firstScope.TryRequestAsync(Key);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = secondScope.TryRequestAsync(Key);
            answer.TrySetResult(false);
            Require(await first.WaitAsync(TimeSpan.FromSeconds(5)) is null && await second.WaitAsync(TimeSpan.FromSeconds(5)) is null);
            Require(prompts == 1 && service.GetStatus(Key).Users.Count == 0);
        });
        Test("missing package paths and cleanup cause no first-chance missing-file exceptions", async path =>
        {
            var missing = 0;
            void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is FileNotFoundException or DirectoryNotFoundException &&
                    args.Exception.Message.Contains(path, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref missing);
            }
            AppDomain.CurrentDomain.FirstChanceException += Observe;
            try
            {
                using var http = Client(Archive());
                await using (var service = Create(path, http, new Probe()))
                {
                    Equal(service.GetStatus(Key).Phase, PackagePhase.Missing);
                    await service.InstallAsync(Key); await service.RemoveAsync(Key);
                    Equal(service.GetStatus(Key).Phase, PackagePhase.Missing);
                    Require(!Directory.Exists(service.GetStatus(Key).Directory));
                }
                await using var reload = Create(path, http, new Probe());
                Equal(reload.GetStatus(Key).Phase, PackagePhase.Missing);
            }
            finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
            Require(missing == 0);
        });
        Test("concurrent requests share consent and survive one caller cancelling", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompts = 0;
            service.ConfirmInstallAsync = async (_, token) => { prompts++; entered.TrySetResult(); return await approve.Task.WaitAsync(token); };
            using var cancel = new CancellationTokenSource();
            using var firstScope = service.CreateScope("First", [Key], CancellationToken.None);
            using var secondScope = service.CreateScope("Second", [Key], CancellationToken.None);
            var first = firstScope.TryRequestAsync(Key, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = secondScope.TryRequestAsync(Key);
            cancel.Cancel(); await Throws<OperationCanceledException>(() => first);
            approve.TrySetResult(true);
            using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Require(lease is not null && prompts == 1 && service.GetStatus(Key).Users.Single() == "Second");
            await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
        });
        Test("declared or uncertain references prevent cleanup without a running lease", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            await service.InstallAsync(Key);
            service.UpdateReferences(new([new(Key, "Disabled.Plugin", "Disabled")], true));
            Require(service.GetPackages().Single().RequiredBy.Count == 1 && !service.GetPackages().Single().CanRemove);
            await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
            service.UpdateReferences(new([], false, "Unreadable manifest"));
            await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
            service.UpdateReferences(new([], true));
            Require(service.GetPackages().Single().CanRemove);
            await service.RemoveAsync(Key); Require(service.GetPackages().Count == 0);
        });
        Test("installed receipt remains reusable and cleanable after source removal", async path =>
        {
            using var http = Client(Archive());
            await using (var install = Create(path, http, new Probe())) await install.InstallAsync(Key);
            await using var service = new ExternalPackageService(path, "win-x64", http, new Probe(), new Log(), []);
            service.UpdateReferences(new([], true));
            using (await service.RequestAsync(Key, "Test.Plugin")) { }
            Require(service.GetPackages().Single().IsInstalled && service.GetPackages().Single().CanRemove);
            await service.RemoveAsync(Key);
        });
        foreach (var incomplete in new[] { false, true })
        if (OperatingSystem.IsWindows()) Test("queued cleanup preserves files after references become " + (incomplete ? "uncertain" : "required"), async path =>
        {
            using var http = Client(Archive());
            await using (var install = Create(path, http, new Probe())) await install.InstallAsync(Key);
            var directory = Path.Combine(path, Key.Id, Key.Version, "win-x64");
            var before = File.ReadAllBytes(Path.Combine(directory, "receipt.json"));
            var work = Path.Combine(path, ".work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var marker = Path.Combine(work, "owner.txt");
            File.WriteAllText(marker, "TractorTool.PackageWork.v1");
            using var locked = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var log = new Log((message, _) =>
            {
                if (!message.StartsWith("保留无法恢复的工具暂存目录：", StringComparison.Ordinal)) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Recovery gate was not released.");
            });
            using var offline = new HttpClient(new Handler((_, _) => throw new Exception("Refused cleanup must preserve offline use.")));
            await using var service = new ExternalPackageService(path, "win-x64", offline, new Probe(), log, [Definition(Archive())]);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                service.UpdateReferences(new([], true));
                // The existing recovery task holds removal before its first file mutation.
                var removal = service.RemoveAsync(Key);
                service.UpdateReferences(incomplete ? new([], false, "Refreshing plugin declarations") :
                    new([new(Key, "New.Plugin", "New plugin")], true));
                release.Set();
                await Throws<InvalidOperationException>(() => removal.WaitAsync(TimeSpan.FromSeconds(5)));
                Require(File.ReadAllBytes(Path.Combine(directory, "receipt.json")).SequenceEqual(before) &&
                    File.Exists(Path.Combine(directory, "bin", "tool.exe")) && service.GetPackages().Single().IsInstalled);
                Equal(service.GetStatus(Key).Phase, PackagePhase.Ready);
                using (await service.RequestAsync(Key, "New.Plugin")) { }
                service.UpdateReferences(new([], true));
                await service.RemoveAsync(Key);
                Require(!Directory.Exists(directory));
            }
            finally { release.Set(); }
        });
        if (OperatingSystem.IsWindows()) Test("locked legacy receipt remains usable when metadata upgrade fails", async path =>
        {
            using var http = Client(Archive());
            await using (var install = Create(path, http, new Probe())) await install.InstallAsync(Key);
            var file = Path.Combine(path, Key.Id, Key.Version, "win-x64", "receipt.json");
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            legacy["Schema"] = 1; legacy.Remove("Definition"); File.WriteAllText(file, legacy.ToJsonString());
            using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var service = Create(path, http, new Probe());
            Equal(service.GetStatus(Key).Phase, PackagePhase.Ready);
            Require(service.SourceError.Contains("暂未升级"));
            using var lease = await service.RequestAsync(Key, "Test.Plugin");
        });
        Test("repair uses revised trusted source and keeps persistent references", async path =>
        {
            using (var http = Client(Archive()))
            await using (var install = Create(path, http, new Probe())) await install.InstallAsync(Key);
            File.WriteAllText(Path.Combine(path, Key.Id, Key.Version, "win-x64", "bin", "tool.exe"), "tampered");
            var revised = Definition(Archive()) with { DownloadUri = new("https://example.invalid/revised.zip") };
            using var updatedHttp = new HttpClient(new Handler((request, _) =>
            { Require(request.RequestUri == revised.DownloadUri); return Task.FromResult(Response(request, Archive())); }));
            await using var service = Create(path, updatedHttp, new Probe(), revised);
            service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin")], true));
            using var scope = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
            await Throws<InvalidDataException>(() => scope.AcquireAsync(Key));
            service.ConfirmInstallAsync = (prompt, _) => { Require(prompt.IsRepair); return Task.FromResult(true); };
            using var repaired = await scope.RequestAsync(Key);
            Require(service.GetStatus(Key).Phase == PackagePhase.Ready && !service.GetPackages().Single().CanRemove);
        });
        foreach (var cancel in new[] { false, true })
        Test("repair keeps installed files after " + (cancel ? "cancellation" : "download failure"), async path =>
        {
            using (var http = Client(Archive()))
            await using (var install = Create(path, http, new Probe())) await install.InstallAsync(Key);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var replacement = new HttpClient(new Handler(async (_, token) =>
            {
                entered.SetResult();
                if (cancel) await Task.Delay(Timeout.Infinite, token);
                throw new HttpRequestException("offline");
            }));
            await using var service = Create(path, replacement, new Probe());
            var directory = service.GetStatus(Key).Directory;
            var receipt = File.ReadAllBytes(Path.Combine(directory, "receipt.json"));
            var tool = Path.Combine(directory, "bin", "tool.exe");
            File.WriteAllText(tool, "previous bytes");
            using var scope = service.CreateScope("Repair", [Key], CancellationToken.None);
            await Throws<InvalidDataException>(() => scope.AcquireAsync(Key));
            service.ConfirmInstallAsync = (prompt, _) => Task.FromResult(prompt.IsRepair);
            var repair = scope.TryRequestAsync(Key);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            {
                service.CancelInstall(Key);
                await Throws<OperationCanceledException>(() => repair);
            }
            else await Throws<HttpRequestException>(() => repair);
            Require(service.GetPackages().Single().IsInstalled && File.ReadAllText(tool) == "previous bytes");
            Require(File.ReadAllBytes(Path.Combine(directory, "receipt.json")).SequenceEqual(receipt));
        });
        foreach (var committed in new[] { false, true })
        Test("restart recovers repair interrupted " + (committed ? "after" : "before") + " commit", async path =>
        {
            using var http = Client(Archive());
            string directory;
            await using (var install = Create(path, http, new Probe()))
            {
                await install.InstallAsync(Key);
                directory = install.GetStatus(Key).Directory;
            }
            var transaction = Path.Combine(Path.GetDirectoryName(directory)!, ".repair-win-x64");
            Directory.CreateDirectory(transaction);
            File.WriteAllText(Path.Combine(transaction, "owner.txt"), "TractorTool.PackageRepair.v1");
            var previous = Path.Combine(transaction, "previous");
            if (committed) Directory.CreateDirectory(previous);
            else
            {
                Directory.CreateDirectory(Path.Combine(transaction, "incoming"));
                Directory.Move(directory, previous);
            }
            File.WriteAllText(Path.Combine(previous, "old.txt"), "previous");
            // No plugin declaration is available yet. Recovery must precede receipt discovery.
            await using var reload = new ExternalPackageService(path, "win-x64", http, new Probe(), new Log());
            Equal(reload.GetStatus(Key).Phase, PackagePhase.Ready);
            Require(File.Exists(Path.Combine(directory, "old.txt")) == !committed && !Directory.Exists(transaction));
        });
        Test("same process retries a blocked repair journal before asking to install", async path =>
        {
            using var http = Client(Archive());
            string directory;
            await using (var install = Create(path, http, new Probe()))
            {
                await install.InstallAsync(Key);
                directory = install.GetStatus(Key).Directory;
            }
            var transaction = Path.Combine(Path.GetDirectoryName(directory)!, ".repair-win-x64");
            Directory.CreateDirectory(Path.Combine(transaction, "incoming"));
            var marker = Path.Combine(transaction, "owner.txt");
            File.WriteAllText(marker, "TractorTool.PackageRepair.v1");
            Directory.Move(directory, Path.Combine(transaction, "previous"));
            using var offline = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("unexpected download")));
            using var locked = File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.None);
            await using var service = new ExternalPackageService(path, "win-x64", offline, new Probe(), new Log());
            service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin", [Recipe(Archive())])], true));
            service.ConfirmInstallAsync = (_, _) => throw new InvalidOperationException("unexpected consent");
            Require(!Directory.Exists(directory));
            locked.Dispose();
            using var lease = await service.TryRequestAsync(Key, "Test.Plugin");
            Require(lease is not null && service.GetPackages().Single().IsInstalled && !Directory.Exists(transaction));
        });
        Test("GitHub resolution is shared, consent is required, and installed leases stay offline", async path =>
        {
            var bytes = Archive(); var recipe = Recipe(bytes); var calls = 0; var downloads = 0; var prompts = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                {
                    Require(request.Headers.UserAgent.Count > 0 && request.RequestUri.AbsolutePath == "/repos/Fixture/tool/releases/tags/v1.0.0");
                    Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task.WaitAsync(token);
                    return Response(request, Release(recipe));
                }
                Interlocked.Increment(ref downloads); return Response(request, bytes);
            }));
            await using var service = GitHub(path, http, recipe);
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(false); };
            using var cancel = new CancellationTokenSource();
            var first = service.TryRequestAsync(Key, "A", cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.TryRequestAsync(Key, "B"); cancel.Cancel(); release.TrySetResult();
            await Throws<OperationCanceledException>(async () => { using var ignored = await first; });
            using var declined = await second;
            Require(declined is null && calls == 1 && prompts == 1 && downloads == 0);
            service.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
            using (var installed = await service.TryRequestAsync(Key, "B")) Require(installed is not null && downloads == 1);
            var onlineCalls = calls;
            using (var reused = await service.TryRequestAsync(Key, "B")) Require(reused is not null && calls == onlineCalls);
            var changed = recipe with { Sha256 = new string('a', 64) };
            service.UpdateReferences(new([new(Key, "B", "B", [changed])], true));
            await Throws<InvalidDataException>(async () => { using var denied = await service.TryRequestAsync(Key, "B"); });
            using var scope = service.CreateScope("B", [Key], CancellationToken.None);
            await Throws<InvalidDataException>(async () => { using var denied = await scope.AcquireAsync(Key); });
        });
        Test("GitHub rejects wrong assets, digests, redirects and malformed metadata before consent", async path =>
        {
            var recipe = Recipe(Archive()); var payload = Release(recipe); var redirect = false; var prompts = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (redirect) request.RequestUri = new("https://other.invalid/release");
                return Task.FromResult(Response(request, payload));
            }));
            await using var service = GitHub(path, http, recipe);
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
            foreach (var invalid in new[] { Release(recipe, digest: "sha256:" + new string('f', 64)),
                Release(recipe, asset: "wrong.zip"), Release(recipe, download: "https://other.invalid/file.zip"),
                Release(recipe, tag: "v2.0.0"), Release(recipe, size: 0) })
            {
                payload = invalid;
                await Throws<InvalidDataException>(async () => { using var denied = await service.TryRequestAsync(Key, "A"); });
            }
            payload = Release(recipe); redirect = true;
            await Throws<InvalidDataException>(async () => { using var denied = await service.TryRequestAsync(Key, "A"); });
            Require(prompts == 0 && service.GetStatus(Key).Phase == PackagePhase.Missing);
        });
        Test("configured GitHub source persists and removal keeps existing packages reusable", async path =>
        {
            var bytes = Archive(); var recipe = Recipe(bytes) with { Source = "https://git.example.com/api/v3/" };
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request,
                request.RequestUri!.AbsolutePath.StartsWith("/api/v3/") ? Release(recipe) : bytes))));
            await using (var service = GitHub(path, http, recipe))
            {
                Require(service.GetSources().Single(s => s.Kind == DependencySourceKind.GitHub).IsDefault);
                await Throws<InvalidOperationException>(async () => { using var denied = await service.TryRequestAsync(Key, "A"); });
                foreach (var bad in new[] { "http://git.example.com/api/v3/", "https://user:password@git.example.com/api/v3/", "https://git.example.com/api/v3/?token=bad" })
                    await Throws<InvalidDataException>(() => { service.AddSource("Team", bad); return Task.CompletedTask; });
                service.AddSource("Team", recipe.Source);
                await Throws<InvalidDataException>(() => { service.AddSource("Duplicate", recipe.Source); return Task.CompletedTask; });
                service.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
                using var lease = await service.TryRequestAsync(Key, "A"); Require(lease is not null);
            }
            using var offline = new HttpClient(new Handler((_, _) => throw new Exception("Installed packages must remain offline")));
            await using var restored = GitHub(path, offline, recipe);
            Require(restored.GetSources().Count(s => s.Kind == DependencySourceKind.GitHub) == 2);
            restored.RemoveSource(restored.GetSources().Single(s => !s.IsDefault));
            using var reused = await restored.TryRequestAsync(Key, "A"); Require(reused is not null);
            Require(restored.GetPackages().Single().IsInstalled && restored.GetSources().Single(s => s.Kind == DependencySourceKind.GitHub).IsDefault);
        });
        Test("cached GitHub repair still requires its configured source after plugin references disappear", async path =>
        {
            var bytes = Archive(); var recipe = Recipe(bytes) with { Source = "https://git.example.com/api/v3/" };
            var calls = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Response(request, request.RequestUri!.AbsolutePath.StartsWith("/api/v3/") ? Release(recipe) : bytes));
            }));
            await using (var install = GitHub(path, http, recipe))
            {
                install.AddSource("Team", recipe.Source);
                install.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
                using var lease = await install.TryRequestAsync(Key, "A");
                Require(lease is not null);
            }
            var tool = Path.Combine(path, Key.Id, Key.Version, "win-x64", "bin", "tool.exe");
            File.Delete(tool);
            await using var service = GitHub(path, http, recipe);
            var prompts = 0;
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(false); };
            Require(await service.TryRequestAsync(Key, "软件设置") is null && prompts == 1);
            service.UpdateReferences(new([], true));
            service.RemoveSource(service.GetSources().Single(s => !s.IsDefault));
            var before = calls;
            await Throws<InvalidOperationException>(async () => { using var lease = await service.TryRequestAsync(Key, "软件设置"); });
            Require(calls == before && prompts == 1 && !File.Exists(tool));

            service.AddSource("Team", recipe.Source);
            service.ConfirmInstallAsync = (_, _) => { prompts++; return Task.FromResult(true); };
            using (var repaired = await service.TryRequestAsync(Key, "软件设置")) Require(repaired is not null && File.Exists(tool));
            Require(calls == before + 1 && prompts == 2);
            service.RemoveSource(service.GetSources().Single(s => !s.IsDefault));
            using var reused = await service.TryRequestAsync(Key, "软件设置");
            Require(reused is not null && calls == before + 1 && prompts == 2);
        });
        Test("GitHub source is rechecked after install confirmation before starting a download", async path =>
        {
            var bytes = Archive(); var recipe = Recipe(bytes) with { Source = "https://git.example.com/api/v3/" };
            var downloads = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                var metadata = request.RequestUri!.AbsolutePath.StartsWith("/api/v3/");
                if (!metadata) Interlocked.Increment(ref downloads);
                return Task.FromResult(Response(request, metadata ? Release(recipe) : bytes));
            }));
            await using var service = GitHub(path, http, recipe);
            service.AddSource("Team", recipe.Source);
            service.ConfirmInstallAsync = (_, _) =>
            {
                service.RemoveSource(service.GetSources().Single(s => !s.IsDefault));
                return Task.FromResult(true);
            };
            await Throws<InvalidOperationException>(async () => { using var lease = await service.TryRequestAsync(Key, "A"); });
            Require(downloads == 0 && !Directory.Exists(service.GetStatus(Key).Directory));
        });
        Test("GitHub confirmation retains its own source while another resolution replaces the cache", async path =>
        {
            var bytes = Archive();
            var recipeA = Recipe(bytes) with { Source = "https://a.example.com/api/v3/" };
            var recipeB = recipeA with { Source = "https://b.example.com/api/v3/" };
            var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloads = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.StartsWith("/api/v3/"))
                {
                    if (request.RequestUri.Host == "a.example.com") return Response(request, Release(recipeA));
                    enteredB.TrySetResult();
                    await releaseB.Task.WaitAsync(token);
                    return Response(request, Release(recipeB));
                }
                Interlocked.Increment(ref downloads);
                return Response(request, bytes);
            }));
            await using var service = GitHub(path, http, recipeA);
            service.AddSource("A", recipeA.Source); service.AddSource("B", recipeB.Source);
            service.ConfirmInstallAsync = (_, _) => Task.FromResult(false);
            var cachedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialChanges = 0;
            void CacheChanged() { if (Interlocked.Increment(ref initialChanges) == 4) cachedA.TrySetResult(); }
            service.Changed += CacheChanged;
            try
            {
                Require(await service.TryRequestAsync(Key, "A") is null);
                // Drain resolution, reservation, preparation and request-release notifications.
                await cachedA.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { service.Changed -= CacheChanged; }
            service.UpdateReferences(new([new(Key, "Plugin.B", "Plugin B", [recipeB])], true));
            var requestB = service.TryRequestAsync(Key, "B");
            Action? onChanged = null;
            try
            {
                await enteredB.Task.WaitAsync(TimeSpan.FromSeconds(5));
                service.UpdateReferences(new([], true));
                service.ConfirmInstallAsync = (prompt, _) =>
                {
                    Require(prompt.Definition.DownloadUri.Host == "a.example.com");
                    confirmation.TrySetResult();
                    return approve.Task;
                };
                var requestA = service.TryRequestAsync(Key, "软件设置");
                await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var resolvedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var changes = 0;
                onChanged = () => { if (Interlocked.Increment(ref changes) == 2) resolvedB.TrySetResult(); };
                service.Changed += onChanged;
                releaseB.TrySetResult();
                // Both resolution publication and request B joining A's preparation must finish.
                await resolvedB.Task.WaitAsync(TimeSpan.FromSeconds(5));
                service.Changed -= onChanged; onChanged = null;
                service.RemoveSource(service.GetSources().Single(s => s.Url == recipeA.Source));
                approve.TrySetResult(true);
                await Throws<InvalidOperationException>(async () => { using var lease = await requestA; });
                await Throws<InvalidOperationException>(async () => { using var lease = await requestB; });
                Require(downloads == 0);
            }
            finally
            {
                if (onChanged is not null) service.Changed -= onChanged;
                releaseB.TrySetResult(); approve.TrySetResult(false);
            }
        });
        Test("source settings reject oversized writes without losing the last readable configuration", async path =>
        {
            using var http = Client(Archive());
            await using var service = Create(path, http, new Probe());
            var file = Path.Combine(path, ".sources.json");
            var rejected = false;
            for (var i = 0; i < 32; i++)
            {
                var before = File.Exists(file) ? File.ReadAllBytes(file) : [];
                var count = service.GetSources().Count;
                try { service.AddSource("Mirror " + i, $"https://packages.example.com/{i}/" + new string('a', 1900), DependencySourceKind.NpmRegistry); }
                catch (InvalidDataException)
                {
                    Require(File.ReadAllBytes(file).SequenceEqual(before) && service.GetSources().Count == count);
                    rejected = true; break;
                }
            }
            Require(rejected && new FileInfo(file).Length <= 32768);
            await using var reloaded = Create(path, http, new Probe());
            Require(reloaded.SourceError.Length == 0 && reloaded.GetSources().SequenceEqual(service.GetSources()));
            var saved = File.ReadAllBytes(file);
            await Throws<InvalidDataException>(() =>
            {
                reloaded.AddSource("Oversized Python", "https://packages.example.com/" + new string('a', 2048) + "/simple/", DependencySourceKind.PythonIndex);
                return Task.CompletedTask;
            });
            Require(File.ReadAllBytes(file).SequenceEqual(saved));
            reloaded.RemoveSource(reloaded.GetSources().First(s => !s.IsDefault));
            Require(ExternalPackageService.ReadConfiguredSources(file).Count == reloaded.GetSources().Count);
        });
        Test("legacy receipt recovers from matching plugin instructions without a catalog", async path =>
        {
            using var http = Client(Archive());
            await using (var install = Create(path, http, new Probe())) await install.InstallAsync(Key);
            var file = Path.Combine(path, Key.Id, Key.Version, "win-x64", "receipt.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            json["Schema"] = 1; json.AsObject().Remove("Definition"); File.WriteAllText(file, json.ToJsonString());
            using var offline = new HttpClient(new Handler((_, _) => throw new Exception("No network")));
            await using var restored = GitHub(path, offline, Recipe(Archive()) with { Sha256 = Recipe(Archive()).Sha256.ToUpperInvariant() });
            using var lease = await restored.TryRequestAsync(Key, "A");
            Require(lease is not null && restored.GetStatus(Key).Phase == PackagePhase.Ready && restored.SourceError.Length == 0);
        });
        Test("damaged legacy receipt remains visible and can be repaired", async path =>
        {
            var bytes = Archive(); using var initial = Client(bytes);
            await using (var install = Create(path, initial, new Probe())) await install.InstallAsync(Key);
            var directory = Path.Combine(path, Key.Id, Key.Version, "win-x64");
            var file = Path.Combine(directory, "receipt.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            json["Schema"] = 1; json.AsObject().Remove("Definition"); File.WriteAllText(file, json.ToJsonString());
            File.Delete(Path.Combine(directory, "bin", "tool.exe"));
            var recipe = Recipe(bytes);
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request,
                request.RequestUri!.Host == "api.github.com" ? Release(recipe) : bytes))));
            await using var restored = GitHub(path, http, recipe);
            Require(restored.GetPackages().Single() is { IsInstalled: true, CanInstall: true });
            restored.ConfirmInstallAsync = (prompt, _) => { Require(prompt.IsRepair); return Task.FromResult(true); };
            using var lease = await restored.TryRequestAsync(Key, "A");
            Require(lease is not null && restored.GetStatus(Key).Phase == PackagePhase.Ready);
        });
        foreach (var damage in new[] { "missing", "json", "oversized", "definition" })
        Test("unreadable receipt stays visible and requires consent to repair: " + damage, async path =>
        {
            var bytes = Archive(); using var initial = Client(bytes);
            await using (var install = Create(path, initial, new Probe())) await install.InstallAsync(Key);
            var directory = Path.Combine(path, Key.Id, Key.Version, "win-x64");
            var file = Path.Combine(directory, "receipt.json");
            if (damage == "missing") File.Delete(file);
            else if (damage == "json") File.WriteAllText(file, "broken");
            else if (damage == "oversized") File.WriteAllText(file, new string('x', 65537));
            else
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
                json["Definition"]!["Sha256"] = "invalid"; File.WriteAllText(file, json.ToJsonString());
            }
            var recipe = Recipe(bytes);
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request,
                request.RequestUri!.Host == "api.github.com" ? Release(recipe) : bytes))));
            await using var restored = GitHub(path, http, recipe);
            Require(restored.GetPackages().Single() is { IsInstalled: true, CanInstall: true, Status.Phase: PackagePhase.Failed });
            restored.ConfirmInstallAsync = (prompt, _) => { Require(prompt.IsRepair); return Task.FromResult(false); };
            Require(await restored.TryRequestAsync(Key, "A") is null && File.Exists(Path.Combine(directory, "bin", "tool.exe")));
            restored.ConfirmInstallAsync = (prompt, _) => { Require(prompt.IsRepair); return Task.FromResult(true); };
            using var lease = await restored.TryRequestAsync(Key, "A");
            Require(lease is not null && restored.GetStatus(Key).Phase == PackagePhase.Ready);
        });
        Test("damaged definition cannot replace a readable installed archive hash", async path =>
        {
            using var initial = Client(Archive());
            await using (var install = Create(path, initial, new Probe())) await install.InstallAsync(Key);
            var directory = Path.Combine(path, Key.Id, Key.Version, "win-x64");
            var file = Path.Combine(directory, "receipt.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            json["Definition"]!["Sha256"] = "invalid"; File.WriteAllText(file, json.ToJsonString());
            using var offline = new HttpClient(new Handler((_, _) => throw new Exception("Conflict must not access network")));
            await using var restored = GitHub(path, offline, Recipe(Archive("different.txt")));
            restored.ConfirmInstallAsync = (_, _) => throw new Exception("Conflict must not ask for replacement");
            await Throws<InvalidDataException>(async () => { using var ignored = await restored.TryRequestAsync(Key, "A"); });
            Require(File.Exists(Path.Combine(directory, "bin", "tool.exe")));
        });
        Test("Windows case aliases cannot claim or clean the same package directory", async path =>
        {
            if (!OperatingSystem.IsWindows()) return;
            using var http = Client(Archive());
            await using var service = Create(path, http, new Probe());
            await service.InstallAsync(Key);
            using var scope = service.CreateScope("A", [Key], default);
            using var lease = await scope.AcquireAsync(Key);
            var alias = Key with { Id = Key.Id.ToLowerInvariant() };
            Require(alias != Key);
            service.UpdateReferences(new([new(alias, "B", "B", [Recipe(Archive())])], true));
            Require(service.ReferenceError.Length != 0 && service.GetPackages().All(p => !p.CanRemove));
            await Throws<InvalidDataException>(async () => { using var ignored = await service.TryRequestAsync(alias, "B"); });
            Require(File.Exists(lease.Tools["tool"]));
            service.UpdateReferences(new([], true));
            Require(service.ReferenceError.Length == 0 && !service.GetPackages().Single().CanRemove);
        });
        Test("shutdown cancels GitHub resolution and source edits stop with the service", async path =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (request, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Response(request, []); }));
            await using var service = GitHub(path, http, Recipe(Archive()));
            var pending = service.TryRequestAsync(Key, "A"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync();
            await Throws<OperationCanceledException>(async () => { using var ignored = await pending; });
            await Throws<ObjectDisposedException>(() => { service.AddSource("Team", "https://git.example.com/api/v3/"); return Task.CompletedTask; });
        });
        Test("reference reconstruction includes disabled, pending and recovery manifests", async path =>
        {
            var paths = new AppPaths(Path.Combine(path, "app"), Path.Combine(path, "data"));
            var compatibility = new HostCompatibility("windows", "win-x64", "1.2", "12.1.2");
            var token = Guid.NewGuid().ToString("N");
            PluginManifest WriteManifest(string root, string version, string dependency)
            {
                var directory = Path.Combine(root, "Test.Plugin", version); Directory.CreateDirectory(directory);
                var manifest = new PluginManifest { SchemaVersion = 1, Id = "Test.Plugin", Name = "Plugin", Version = version,
                    ApiVersion = "1.3", UiApiVersion = "1.2", AvaloniaVersion = "12.1.2", EntryAssembly = "plugin.dll",
                    EntryType = "Plugin", SupportedPlatforms = ["windows"], Dependencies = [new(dependency, "1.0.0")] };
                File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return manifest;
            }
            var active = WriteManifest(paths.PluginRoot, "2.0.0", "Active");
            WriteManifest(paths.PluginRoot, "1.0.0", "Recovery");
            WriteManifest(Path.Combine(paths.PluginRoot, ".management", "staging", token), "3.0.0", "Pending");
            new JsonFileStore<PluginManagementState>(Path.Combine(paths.PluginRoot, ".management", "state.json")).Save(new()
            {
                Plugins = [new() { Id = "Test.Plugin", Name = "Plugin", ActiveVersion = "2.0.0", Enabled = false,
                    Recovery = new("1.0.0", Guid.NewGuid().ToString("N"), paths.DataRoot, false),
                    Pending = new(PluginChangeKind.Activate, token, paths.DataRoot, "3.0.0", new string('0', 64)) }]
            });
            using var manager = new PluginManager(paths, compatibility);
            using var http = Client(Archive()); await using var service = Create(paths.PackageRoot, http, new Probe());
            using var references = new PluginPackageReferences(paths, manager, service);
            references.SetDiscovered([new(Path.Combine(paths.PluginRoot, "Test.Plugin", "2.0.0"), active, PluginState.Disabled, "")], null);
            Require(service.ReferenceError == "" && service.GetPackages().Select(p => p.Package.Id).Order().SequenceEqual(new[] { "Active", "Pending", "Recovery" }));
            manager.CancelPending("Test.Plugin");
            Require(service.GetPackages().All(p => p.Package.Id != "Pending"));
            references.SetDiscovered([new("broken", null, PluginState.Invalid, "broken")], null);
            Require(service.ReferenceError.Length != 0);
            await service.InstallAsync(Key);
            references.SetDiscovered([], null);
            Require(service.ReferenceError.Contains("Test.Plugin") && !service.GetPackages().Single(p => p.Package == Key).CanRemove);
            await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
            Require(manager.QueueChange("Test.Plugin", PluginChangeKind.Uninstall, []) is null);
            manager.ApplyPending();
            Require(service.ReferenceError == "" && service.GetPackages().Single(p => p.Package == Key).CanRemove);
        });
        Test("failed reference publication disables tool, Python and Node cleanup without undoing management state", async path =>
        {
            var paths = new AppPaths(Path.Combine(path, "app"), Path.Combine(path, "data"));
            using var manager = new PluginManager(paths, new("windows", "win-x64", "1.2", "12.1.2"));
            using var http = Client(Archive()); await using var service = Create(paths.PackageRoot, http, new Probe());
            await service.InstallAsync(Key);
            Require(service.GetPackages().Single().CanRemove && service.Python.Error == "" && service.Node.Error == "");
            using var references = new PluginPackageReferences(paths, manager, service);
            var manifest = new PluginManifest { Id = "Bad.Plugin", Name = "Fixture", Version = "1.0.0",
                Dependencies = [new(Key.Id, Key.Version) { Downloads = [Recipe(Archive()) with { Repository = "invalid" }] }] };
            var record = new PluginRecord(Path.Combine(paths.PluginRoot, manifest.Id, manifest.Version), manifest, PluginState.Disabled, "fixture");
            // Snapshot creation succeeds; publication rejects the invalid recipe while cloning references.
            references.SetDiscovered([record], null);
            Require(service.ReferenceError.Contains("刷新失败") && !service.GetPackages().Single().CanRemove);
            Require(service.Python.Error.Length != 0 && service.Node.Error.Length != 0);
            Require(manager.QueueChange(manifest.Id, PluginChangeKind.Disable, [record]) is null);
            Require(manager.Snapshot.Single().Pending?.Kind == PluginChangeKind.Disable);
            Require(new JsonFileStore<PluginManagementState>(Path.Combine(paths.PluginRoot, ".management", "state.json")).Load().Plugins.Single() == manager.Snapshot.Single());
            await Throws<InvalidOperationException>(() => service.RemoveAsync(Key));
            references.SetDiscovered([new(record.Directory, manifest with { Dependencies = [] }, PluginState.Disabled, "fixture")], null);
            Require(service.ReferenceError == "" && service.Python.Error == "" && service.Node.Error == "");
            Require(service.GetPackages().Single().CanRemove);
        });
        Test("bad hash never executes tools or commits", async path =>
        {
            var bytes = Archive(); using var http = Client(bytes); var probe = new Probe();
            await using var service = Create(path, http, probe, Definition(bytes) with { Sha256 = new string('0', 64) });
            await Throws<InvalidDataException>(() => service.InstallAsync(Key));
            Equal(service.GetStatus(Key).Phase, PackagePhase.Failed); Require(probe.Calls == 0);
            Require(!Directory.Exists(Path.Combine(path, Key.Id, Key.Version, "win-x64")));
        });
        Test("versions coexist and removing one preserves the other", async path =>
        {
            var bytes = Archive(); using var http = Client(bytes);
            var first = Definition(bytes); var second = first with { Package = new(Key.Id, "2.0.0") };
            await using var service = new ExternalPackageService(path, "win-x64", http, new Probe(), new Log(), [first, second]);
            service.UpdateReferences(new([], true));
            await Task.WhenAll(service.InstallAsync(Key), service.InstallAsync(second.Package));
            await service.RemoveAsync(Key);
            using var scope = service.CreateScope("Test.Plugin", [second.Package], CancellationToken.None);
            using var lease = await scope.AcquireAsync(second.Package);
            Require(File.Exists(lease.Tools["tool"]) && service.GetStatus(second.Package).Phase == PackagePhase.Ready);
        });
        Test("damaged receipt stays failed without overwriting existing files", async path =>
        {
            var directory = Path.Combine(path, Key.Id, Key.Version, "win-x64"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "receipt.json"), "broken");
            File.WriteAllText(Path.Combine(directory, "keep.txt"), "original");
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            Equal(service.GetStatus(Key).Phase, PackagePhase.Failed);
            await Throws<IOException>(() => service.InstallAsync(Key));
            Require(File.ReadAllText(Path.Combine(directory, "keep.txt")) == "original");
        });
        foreach (var unsafeName in new[] { "../escape.exe", "C:/escape.exe", "bin/CON", "bin/tool.exe:extra", "BIN/other.exe" })
            Test("reject ZIP path " + unsafeName, async path =>
            {
                var bytes = Archive(unsafeName); using var http = Client(bytes); var probe = new Probe();
                await using var service = Create(path, http, probe, Definition(bytes));
                await Throws<InvalidDataException>(() => service.InstallAsync(Key)); Require(probe.Calls == 0);
            });
        Test("reject ZIP symlink", async path =>
        {
            var bytes = Archive("link", true); using var http = Client(bytes); var probe = new Probe();
            await using var service = Create(path, http, probe, Definition(bytes));
            await Throws<InvalidDataException>(() => service.InstallAsync(Key)); Require(probe.Calls == 0);
        });
        Test("wrong version fails before commit", async path =>
        {
            using var http = Client(Archive());
            await using var service = Create(path, http, new Probe { Output = "wrong version" });
            await Throws<InvalidDataException>(() => service.InstallAsync(Key)); Equal(service.GetStatus(Key).Phase, PackagePhase.Failed);
        });
        Test("tool verification honors selected output and rejects truncated selected streams", async path =>
        {
            var bytes = Archive(); var definition = Definition(bytes) with
            { Tools = new Dictionary<string, PackageTool> { ["tool"] = new("bin/tool.exe", "tool version 1.0.0", [], "stderr") } };
            using var http = Client(bytes); var probe = new Probe { Output = "tool version 1.0.0", Error = "wrong stderr" };
            await using var service = Create(path, http, probe, definition);
            await Throws<InvalidDataException>(() => service.InstallAsync(Key));
            probe.Error = "tool version 1.0.0"; probe.ErrorTruncated = true;
            await Throws<InvalidDataException>(() => service.InstallAsync(Key));
            probe.ErrorTruncated = false; probe.OutputTruncated = true;
            await service.InstallAsync(Key); Equal(service.GetStatus(Key).Phase, PackagePhase.Ready);
            Require(probe.Requests.All(p => p.Arguments.Count == 0));
        });
        Test("invalid probe metadata is rejected before execution", async path =>
        {
            var definition = Definition(Archive()); var probe = new Probe(); using var http = Client(Archive());
            foreach (var tool in new[]
            {
                new PackageTool("bin/tool.exe", "tool version 1.0.0", ["--version"], "combined"),
                new PackageTool("../tool.exe", "tool version 1.0.0"),
                new PackageTool("bin/tool.exe", "tool version 1.0.0", Enumerable.Repeat("x", 17).ToArray()),
                new PackageTool("bin/tool.exe", "tool version 1.0.0", [new string('x', 1025)]),
                new PackageTool("bin/tool.exe", "tool version 1.0.0", ["--version\0bad"]),
                new PackageTool("bin/tool.exe", new string('x', 513)),
                new PackageTool("bin/tool.exe", " ")
            })
                await Throws<InvalidDataException>(async () =>
                { await using var service = Create(path, http, probe, definition with { Tools = new Dictionary<string, PackageTool> { ["tool"] = tool } }); });
            Require(probe.Calls == 0);
        });
        Test("definition at metadata budget installs with a readable receipt and lease", async path =>
        {
            var bytes = Archive(); var definition = Definition(bytes);
            definition = definition with
            { Description = new string('d', 48 * 1024 - JsonSerializer.SerializeToUtf8Bytes(definition).Length + definition.Description.Length) };
            Require(JsonSerializer.SerializeToUtf8Bytes(definition).Length == 48 * 1024);
            using var http = Client(bytes); var probe = new Probe();
            await using var service = Create(path, http, probe, definition);
            await service.InstallAsync(Key);
            var receipt = Path.Combine(service.GetStatus(Key).Directory, "receipt.json");
            Require(new FileInfo(receipt).Length <= 64 * 1024);
            using var scope = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
            using var lease = await scope.AcquireAsync(Key);
            Require(File.Exists(lease.Tools["tool"]) && probe.Calls == 1);
        });
        Test("network timeout remains a visible failure", async path =>
        {
            using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("response timeout")));
            await using var service = Create(path, http, new Probe());
            await Throws<TimeoutException>(() => service.InstallAsync(Key));
            Require(service.GetStatus(Key).Phase == PackagePhase.Failed && service.GetStatus(Key).Error.Length != 0);
        });
        Test("coalesced installs survive one waiter's cancellation", async path =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                Interlocked.Increment(ref requests); entered.TrySetResult(); await release.Task.WaitAsync(token);
                return Response(request, Archive());
            }));
            await using var service = Create(path, http, new Probe());
            using var cancel = new CancellationTokenSource();
            var first = service.InstallAsync(Key, cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.InstallAsync(Key); cancel.Cancel();
            await Throws<OperationCanceledException>(() => first);
            release.TrySetResult(); await second.WaitAsync(TimeSpan.FromSeconds(5));
            Require(requests == 1 && service.GetStatus(Key).Phase == PackagePhase.Ready);
        });
        Test("explicit cancel cleans staged files and retry succeeds", async path =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int requests = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                if (Interlocked.Increment(ref requests) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return Response(request, Archive());
            }));
            await using var service = Create(path, http, new Probe());
            var first = service.InstallAsync(Key); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); service.CancelInstall(Key);
            await Throws<OperationCanceledException>(() => first);
            Equal(service.GetStatus(Key).Phase, PackagePhase.Missing);
            Require(!Directory.EnumerateDirectories(Path.Combine(path, ".work")).Any());
            await service.InstallAsync(Key); Equal(service.GetStatus(Key).Phase, PackagePhase.Ready);
        });
        Test("scope rejects undeclared dependency and releases forgotten lease", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            await service.InstallAsync(Key);
            using (var scope = service.CreateScope("Test.Plugin", [], CancellationToken.None))
                await Throws<InvalidOperationException>(() => scope.AcquireAsync(Key));
            var allowed = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
            var lease = await allowed.AcquireAsync(Key); allowed.Dispose(); lease.Dispose();
            Require(service.GetStatus(Key).Users.Count == 0); await service.RemoveAsync(Key);
        });
        Test("tampered tool fails acquisition without stale Ready notification", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            await service.InstallAsync(Key);
            File.WriteAllText(Path.Combine(path, Key.Id, Key.Version, "win-x64", "bin", "tool.exe"), "changed");
            using var scope = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
            var reserved = false; var exposedReady = false;
            service.Changed += () =>
            {
                var status = service.GetStatus(Key);
                if (status.Users.Count > 0) reserved = true;
                if (reserved && status.Users.Count == 0 && status.Phase == PackagePhase.Ready) exposedReady = true;
            };
            await Throws<InvalidDataException>(() => scope.AcquireAsync(Key));
            Require(!exposedReady && service.GetStatus(Key).Phase == PackagePhase.Failed);
            await service.RemoveAsync(Key); Equal(service.GetStatus(Key).Phase, PackagePhase.Missing);
        });
        Test("shutdown cancels download and seals new operations", async path =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (request, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Response(request, Archive()); }));
            var service = Create(path, http, new Probe());
            var operation = service.InstallAsync(Key); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Throws<OperationCanceledException>(() => operation);
            await Throws<ObjectDisposedException>(() => service.InstallAsync(Key));
        });
        Test("recover only marked abandoned work", async path =>
        {
            var owned = Path.Combine(path, ".work", Guid.NewGuid().ToString("N"));
            var foreign = Path.Combine(path, ".work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(owned); Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(owned, "owner.txt"), "TractorTool.PackageWork.v1");
            File.WriteAllText(Path.Combine(owned, "download.zip"), "partial");
            File.WriteAllText(Path.Combine(foreign, "user.txt"), "keep");
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            await service.InstallAsync(Key);
            Require(!Directory.Exists(owned) && File.ReadAllText(Path.Combine(foreign, "user.txt")) == "keep");
        });
    }

    private static readonly PackageReference Key = new("Fixture", "1.0.0");
    private static PackageDownload Recipe(byte[] bytes) => new("win-x64", "Fixture/tool", "v1.0.0", "tool.zip",
        Definition(bytes).Sha256, "Fixture", "Fixture", "Fixture", Definition(bytes).Tools);
    private static byte[] Release(PackageDownload recipe, string? digest = null, string? asset = null,
        string? download = null, string? tag = null, long size = 100) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = tag ?? recipe.Tag, draft = false,
            assets = new[] { new { name = asset ?? recipe.Asset, state = "uploaded", size,
                browser_download_url = download ?? $"{(recipe.Source == DependencySource.GitHubUrl ? "https://github.com" : new Uri(recipe.Source).GetLeftPart(UriPartial.Authority))}/{recipe.Repository}/releases/download/{recipe.Tag}/{recipe.Asset}",
                digest = digest ?? "sha256:" + recipe.Sha256 } }
        });
    private static ExternalPackageService GitHub(string root, HttpClient http, PackageDownload recipe)
    {
        var service = new ExternalPackageService(root, "win-x64", http, new Probe(), new Log());
        service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin", [recipe])], true)); return service;
    }
    private static byte[] Archive(string? extra = null, bool link = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            // Stable zip timestamps ensure repeated fixture hashes match.
            void Add(string name, string value, bool symlink)
            {
                var entry = zip.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var writer = new StreamWriter(entry.Open()); writer.Write(value);
            }
            Add("bin/tool.exe", "fixture executable bytes, never executed", false);
            Add("LICENSE.txt", "fixture", false);
            if (extra is not null) Add(extra, "extra", link);
        }
        return memory.ToArray();
    }
    private static PackageDefinition Definition(byte[] bytes) => new(Key, "win-x64", "Fixture", "Fixture",
        new("https://example.invalid/tool.zip"), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "Fixture",
        new("https://example.invalid/source"), new Dictionary<string, PackageTool> { ["tool"] = new("bin/tool.exe", "tool version 1.0.0") });
    private static ExternalPackageService Create(string root, HttpClient http, Probe runner, PackageDefinition? definition = null)
    {
        var service = new ExternalPackageService(root, "win-x64", http, runner, new Log(), [definition ?? Definition(Archive())]);
        service.UpdateReferences(new([], true));
        return service;
    }
    private static HttpClient Client(byte[] bytes) => new(new Handler((request, _) => Task.FromResult(Response(request, bytes))));
    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes) => new(HttpStatusCode.OK)
    { RequestMessage = request, Content = new ByteArrayContent(bytes) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Probe : IProcessRunner
    {
        public int Calls;
        public string Output = "tool version 1.0.0\n";
        public string Error = "";
        public bool OutputTruncated;
        public bool ErrorTruncated;
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls);
            lock (Requests) Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, Output, Error, OutputTruncated, ErrorTruncated));
        }
    }
    private sealed class Log(Action<string, Exception?>? written = null) : IAppLog
    { public void Write(string source, string message, Exception? error = null) => written?.Invoke(message, error); }
    private static void Require(bool value) { if (!value) throw new Exception("Package assertion failed."); }
    private static void Equal<T>(T value, T expected) => Require(EqualityComparer<T>.Default.Equals(value, expected));
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
