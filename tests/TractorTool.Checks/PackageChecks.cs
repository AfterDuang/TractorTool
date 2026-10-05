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
        Test("source catalog is not an installed catalog; missing dependency stays visible", async path =>
        {
            using var http = Client(Archive()); await using var service = Create(path, http, new Probe());
            Require(service.GetPackages().Count == 0);
            var missing = new PackageReference("Unavailable", "1.0.0");
            service.UpdateReferences(new([new(missing, "Test.Plugin", "Plugin")], true));
            Require(service.GetPackages().Single().Package == missing && !service.GetPackages().Single().CanInstall);
            using var scope = service.CreateScope("Test.Plugin", [missing], CancellationToken.None);
            Equal(scope.GetStatus(missing).Phase, PackagePhase.Missing);
            await Throws<NotSupportedException>(() => scope.RequestAsync(missing));
        });
        Test("source refresh coalesces, caller cancellation keeps the shared refresh alive", async path =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            using var http = new HttpClient(new Handler(async (request, token) =>
            {
                Require(request.RequestUri == IndexUri);
                Interlocked.Increment(ref requests); entered.TrySetResult(); await release.Task.WaitAsync(token);
                return Response(request, Index(Definition(Archive())));
            }));
            await using var service = Remote(path, http, new Probe());
            Require(service.CanRefreshSource && !service.IsRefreshingSource);
            using var cancel = new CancellationTokenSource();
            var first = service.RefreshSourceAsync(cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.RefreshSourceAsync();
            Require(service.IsRefreshingSource);
            cancel.Cancel(); await Throws<OperationCanceledException>(() => first);
            release.TrySetResult(); Require(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Require(requests == 1 && !service.IsRefreshingSource && service.SourceError == "");
            Require(service.SourceStatus.Length != 0 && service.GetPackages().Count == 0);
            service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin")], true));
            Require(service.GetPackages().Single().CanInstall);
        });
        Test("unknown dependency discovers source automatically and verifies custom stderr arguments", async path =>
        {
            var bytes = Archive(); var definition = Definition(bytes) with
            {
                Tools = new Dictionary<string, PackageTool> { ["tool"] = new("bin/tool.exe", "tool\nversion 1.0.0", ["--version"], "stderr") }
            };
            var indexRequests = 0; var downloads = 0; var prompts = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (request.RequestUri == IndexUri) { indexRequests++; return Task.FromResult(Response(request, Index(definition))); }
                Require(request.RequestUri == definition.DownloadUri); downloads++; return Task.FromResult(Response(request, bytes));
            }));
            var probe = new Probe { Output = "unrelated stdout", Error = "tool\r\nversion 1.0.0\r\n" };
            await using var service = Remote(path, http, probe);
            service.ConfirmInstallAsync = (prompt, _) =>
            { prompts++; Require(prompt.Definition.Package == Key && prompt.Definition.DownloadUri == definition.DownloadUri); return Task.FromResult(true); };
            using var scope = service.CreateScope("Test.Plugin", [Key], CancellationToken.None);
            Equal(scope.GetStatus(Key).Phase, PackagePhase.Missing);
            using (var lease = await scope.TryRequestAsync(Key)) Require(lease is not null && File.Exists(lease.Tools["tool"]));
            using (var lease = await scope.TryRequestAsync(Key)) Require(lease is not null);
            Require(indexRequests == 1 && downloads == 1 && prompts == 1);
            Require(probe.Requests.Single().Arguments.SequenceEqual(new[] { "--version" }));
        });
        Test("valid source cache survives bad refreshes and remains isolated by source URI", async path =>
        {
            var original = Definition(Archive()); var other = original with { Package = new("NewPackage", "1.0.0") };
            byte[] payload = Index(original); var networkFailure = false; var requests = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                requests++; if (networkFailure) throw new HttpRequestException("offline");
                return Task.FromResult(Response(request, payload));
            }));
            await using (var service = Remote(path, http, new Probe()))
            {
                service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin"), new(other.Package, "Test.Plugin", "Plugin")], true));
                Require(await service.RefreshSourceAsync());
                foreach (var invalid in new[]
                {
                    Index(other, original with { Sha256 = new string('0', 64) }),
                    Index(original, original with { Package = new("fixture", Key.Version) }),
                    Index(original with { DownloadUri = new("http://example.invalid/tool.zip") }),
                    Index(original with { SourceUri = new("http://example.invalid/source") }),
                    JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, packages = new[] { original } }),
                    new byte[1024 * 1024 + 1]
                })
                {
                    payload = invalid; Require(!await service.RefreshSourceAsync());
                    Require(service.SourceError.Length != 0 && !service.IsRefreshingSource);
                    var packages = service.GetPackages();
                    Require(packages.Single(p => p.Package == Key).CanInstall && packages.Single(p => p.Package == Key).Definition?.Sha256 == original.Sha256);
                    Require(!packages.Single(p => p.Package == other.Package).CanInstall); // Reject the complete update, including its valid first entry.
                }
                networkFailure = true; Require(!await service.RefreshSourceAsync());
                Require(service.SourceError.Length != 0 && service.GetPackages().Single(p => p.Package == Key).CanInstall);
            }
            var beforeReload = requests;
            await using (var reload = Remote(path, http, new Probe()))
            {
                reload.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin")], true));
                Require(reload.GetPackages().Single().CanInstall && reload.GetPackages().Single().Definition?.Sha256 == original.Sha256);
                Require(requests == beforeReload);
            }
            await using var isolated = Remote(path, http, new Probe(), new("https://example.invalid/other-index.json"));
            isolated.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin")], true));
            Require(!isolated.GetPackages().Single().CanInstall && requests == beforeReload);
        });
        Test("remote metadata overrides bundled source and removal restores its fallback", async path =>
        {
            var bundled = Definition(Archive()); var revised = bundled with { Description = "Remote description", SourceUri = new("https://example.invalid/new-source") };
            var payload = Index(revised);
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request, payload))));
            await using var service = new ExternalPackageService(Path.Combine(path, "installed"), "win-x64", http, new Probe(), new Log(),
                [bundled], IndexUri, Path.Combine(path, "cache"));
            service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin")], true));
            Require(await service.RefreshSourceAsync());
            Require(service.GetPackages().Single().Definition?.Description == revised.Description &&
                service.GetPackages().Single().Definition?.SourceUri == revised.SourceUri);
            payload = Index(); Require(await service.RefreshSourceAsync());
            Require(service.GetPackages().Single().CanInstall && service.GetPackages().Single().Definition?.Description == bundled.Description);
        });
        Test("non-HTTPS source configurations and redirects are refused before adopting content", async path =>
        {
            var redirect = false; var calls = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            {
                calls++;
                var response = Response(request, Index(Definition(Archive())));
                if (redirect) response.RequestMessage = new(HttpMethod.Get, "http://example.invalid/index.json");
                return Task.FromResult(response);
            }));
            foreach (var uri in new[] { "http://example.invalid/index.json", "file:///index.json", "https://user:pass@example.invalid/index.json", "https://example.invalid/index.json#fragment" })
                await Throws<InvalidDataException>(async () => { await using var rejected = Remote(path, http, new Probe(), new(uri)); });
            Require(calls == 0);
            await using var service = Remote(path, http, new Probe());
            service.UpdateReferences(new([new(Key, "Test.Plugin", "Plugin")], true));
            Require(await service.RefreshSourceAsync());
            redirect = true; Require(!await service.RefreshSourceAsync());
            Require(service.SourceError.Length != 0 && service.GetPackages().Single().CanInstall && calls == 2);
        });
        Test("installed package stays offline reusable and rejects same-version source hash changes", async path =>
        {
            var original = Definition(Archive()); var payload = Index(original); var requests = 0;
            using var http = new HttpClient(new Handler((request, _) =>
            { requests++; return Task.FromResult(Response(request, request.RequestUri == IndexUri ? payload : Archive())); }));
            await using (var install = Remote(path, http, new Probe()))
            {
                install.ConfirmInstallAsync = (_, _) => Task.FromResult(true);
                using (await install.TryRequestAsync(Key, "Test.Plugin")) { }
                payload = Index(original with { Sha256 = new string('0', 64) });
                Require(!await install.RefreshSourceAsync() && install.SourceError.Length != 0);
                Equal(install.GetStatus(Key).Phase, PackagePhase.Ready);
                payload = Index(); Require(await install.RefreshSourceAsync());
            }
            var networkCalls = 0;
            using var offline = new HttpClient(new Handler((_, _) => { networkCalls++; throw new HttpRequestException("offline"); }));
            await using var service = Remote(path, offline, new Probe());
            using (await service.TryRequestAsync(Key, "Test.Plugin")) { }
            Require(networkCalls == 0 && service.GetPackages().Single().IsInstalled && service.GetPackages().Single().CanRemove);
            // The installed receipt still protects the hash after the source entry was removed.
            payload = Index(original with { Sha256 = new string('0', 64) });
            await using (var changedSource = Remote(path, http, new Probe()))
            {
                Require(!await changedSource.RefreshSourceAsync());
                Equal(changedSource.GetStatus(Key).Phase, PackagePhase.Ready);
            }
            await service.RemoveAsync(Key); Require(!service.GetPackages().Any() && networkCalls == 0);
        });
        Test("refresh during consent preserves the approved download snapshot", async path =>
        {
            var original = Definition(Archive()); var revised = original with { DownloadUri = new("https://example.invalid/revised.zip"), Description = "Updated metadata" };
            var payload = Index(original); var downloads = new List<Uri>();
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (request.RequestUri == IndexUri) return Task.FromResult(Response(request, payload));
                downloads.Add(request.RequestUri!); return Task.FromResult(Response(request, Archive()));
            }));
            await using var service = Remote(path, http, new Probe());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.ConfirmInstallAsync = async (prompt, token) =>
            { Require(prompt.Definition.DownloadUri == original.DownloadUri); entered.TrySetResult(); return await approve.Task.WaitAsync(token); };
            var request = service.TryRequestAsync(Key, "Test.Plugin");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            payload = Index(revised); Require(await service.RefreshSourceAsync());
            approve.TrySetResult(true);
            using (var lease = await request.WaitAsync(TimeSpan.FromSeconds(5))) Require(lease is not null);
            Require(downloads.Single() == original.DownloadUri);
            await service.RemoveAsync(Key);
            service.ConfirmInstallAsync = (prompt, _) =>
            { Require(prompt.Definition.DownloadUri == revised.DownloadUri); return Task.FromResult(true); };
            using (await service.TryRequestAsync(Key, "Test.Plugin")) { }
            Require(downloads.SequenceEqual(new[] { original.DownloadUri, revised.DownloadUri }));
        });
        Test("shutdown cancels a shared source refresh and rejects later refreshes", async path =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new Handler(async (request, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Response(request, Index()); }));
            var service = Remote(path, http, new Probe());
            var refresh = service.RefreshSourceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Throws<OperationCanceledException>(() => refresh);
            await Throws<ObjectDisposedException>(() => service.RefreshSourceAsync());
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
        Test("source rejects invalid version probe metadata before execution", async path =>
        {
            var definition = Definition(Archive()); var probe = new Probe(); byte[] payload = Index();
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Response(request, payload))));
            await using var service = Remote(path, http, probe);
            foreach (var tool in new[]
            {
                new PackageTool("bin/tool.exe", "tool version 1.0.0", ["--version"], "combined"),
                new PackageTool("bin/tool.exe", "tool version 1.0.0", Enumerable.Repeat("x", 17).ToArray()),
                new PackageTool("bin/tool.exe", "tool version 1.0.0", [new string('x', 1025)]),
                new PackageTool("bin/tool.exe", "tool version 1.0.0", ["--version\0bad"]),
                new PackageTool("bin/tool.exe", new string('x', 513)),
                new PackageTool("bin/tool.exe", " ")
            })
            {
                payload = Index(definition with { Tools = new Dictionary<string, PackageTool> { ["tool"] = tool } });
                Require(!await service.RefreshSourceAsync() && service.SourceError.Length != 0);
            }
            var oversized = definition with
            {
                Description = new string('d', 33 * 1024),
                Tools = new Dictionary<string, PackageTool>
                { ["tool"] = new("bin/tool.exe", "tool version 1.0.0", Enumerable.Repeat(new string('x', 1024), 16).ToArray()) }
            };
            Require(JsonSerializer.SerializeToUtf8Bytes(oversized).Length > 48 * 1024);
            payload = Index(oversized);
            Require(!await service.RefreshSourceAsync() && service.SourceError.Length != 0);
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
    private static readonly Uri IndexUri = new("https://example.invalid/index.json");
    private static byte[] Index(params PackageDefinition[] definitions) => JsonSerializer.SerializeToUtf8Bytes(
        new { schemaVersion = 1, packages = definitions }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static ExternalPackageService Remote(string root, HttpClient http, Probe runner, Uri? source = null)
    {
        var service = new ExternalPackageService(Path.Combine(root, "installed"), "win-x64", http, runner, new Log(), [],
            source ?? IndexUri, Path.Combine(root, "cache"));
        service.UpdateReferences(new([], true));
        return service;
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
    private sealed class Log : IAppLog { public void Write(string source, string message, Exception? error = null) { } }
    private static void Require(bool value) { if (!value) throw new Exception("Package assertion failed."); }
    private static void Equal<T>(T value, T expected) => Require(EqualityComparer<T>.Default.Equals(value, expected));
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
