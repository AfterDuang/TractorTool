using System.Text.Json;
using TractorTool.Plugins.SystemHud;

internal static class HudSamplingChecks
{
    internal static async Task Run()
    {
        var fast = new HudProfile { Metric = 2, SamplingSeconds = .2 };
        var slow = new HudProfile { Metric = 3, SamplingSeconds = 600 };
        Check(fast.Error.Length == 0 && slow.Error.Length == 0, "Both interval bounds must be valid.");
        foreach (var invalid in new[] { .19, 601, double.NaN, double.PositiveInfinity })
            Check((fast with { SamplingSeconds = invalid }).Error.Length > 0, "Invalid intervals must be rejected.");

        var legacy = new HudProfiles
        {
            Profiles = [fast with { RefreshSeconds = 30 }, new() { Metric = 6, Address = "https://example.test", RefreshSeconds = 120 },
                new() { Metric = 9, RefreshSeconds = 3600 }], ActiveId = fast.Id, Rotate = true, Seconds = 5
        };
        var imported = JsonSerializer.Deserialize<HudProfiles>(JsonSerializer.Serialize(legacy))!.Normalize(0);
        Check(imported.SchemaVersion == 1 && imported.ActiveId == fast.Id && imported.Rotate && imported.Seconds == 5
            && imported.Profiles.Select(p => p.SamplingSeconds).SequenceEqual(new double[] { 1, 120, 600 })
            && imported.Profiles.All(p => p.RefreshSeconds is null), "Legacy migration must retain identity/carousel and import only meaningful intervals.");
        var settings = new HudProfiles { Profiles = [fast, slow], ActiveId = slow.Id };
        var json = JsonSerializer.Serialize(settings);
        var reloaded = JsonSerializer.Deserialize<HudProfiles>(json)!.Normalize(0);
        Check(!json.Contains("RefreshSeconds") && reloaded.Profiles.SequenceEqual(settings.Profiles)
            && reloaded.Active == slow, "Fractional and long intervals must round-trip without obsolete fields.");
        try
        {
            (legacy with { Profiles = [new() { Metric = 9, RefreshSeconds = double.PositiveInfinity }] }).Normalize(0);
            throw new InvalidOperationException("Invalid legacy network interval was accepted.");
        }
        catch (InvalidDataException) { }

        var calls = new Dictionary<Guid, int>();
        using var sampler = new HudSampler(readLocal: p =>
        {
            calls.TryGetValue(p.Id, out var count);
            calls[p.Id] = ++count;
            return new("测试", count.ToString(), "仅内存数据");
        });
        var first = await sampler.ReadAsync(fast, false, false, "", default);
        var cached = await sampler.ReadAsync(fast, true, false, "", default);
        Check(calls[fast.Id] == 1 && first == cached, "Repeated switches must reuse a fresh reading and its timestamp.");
        await Task.Delay(sampler.GetRefreshDelay(fast) + TimeSpan.FromMilliseconds(30));
        var second = await sampler.ReadAsync(fast, false, false, "", default);
        Check(calls[fast.Id] == 2 && second?.Reading.ObservedAt > first?.Reading.ObservedAt,
            "A 0.2-second interval must become eligible after its deadline.");
        var slowReading = await sampler.ReadAsync(slow, true, false, "", default);
        await sampler.ReadAsync(fast, true, false, "", default);
        Check(await sampler.ReadAsync(slow, true, false, "", default) == slowReading && calls[slow.Id] == 1
            && sampler.GetRefreshDelay(slow).TotalSeconds is > 599 and <= 600,
            "Ten-minute intervals must survive carousel switches and retain their independent deadline.");
        await sampler.ReadAsync(slow with { SamplingSeconds = 1 }, false, false, "", default);
        Check(calls[slow.Id] == 2, "Editing an interval must not reuse an incompatible cached profile.");
        sampler.ResetCache();
        await sampler.ReadAsync(fast, false, false, "", default);
        Check(calls[fast.Id] >= 3, "Applying changes must invalidate the previous cache.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmupCalls = 0;
        using var warming = new HudSampler(readLocal: _ =>
        {
            if (Interlocked.Increment(ref warmupCalls) != 1) return new("测试", "42%", "就绪", 42);
            started.SetResult();
            return new("测试", "—", "建立基线") { IsInitializing = true };
        });
        using var cancellation = new CancellationTokenSource();
        var pending = warming.ReadAsync(slow, false, false, "", cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        Check(await pending is null, "Cancellation must stop initial warm-up.");
        await warming.ReadAsync(slow, true, false, "", default);
        Check(warmupCalls == 1 && warming.GetRefreshDelay(slow).TotalSeconds <= .25,
            "An interrupted first read must retain a bounded cooldown, not wait ten minutes.");
        await Task.Delay(warming.GetRefreshDelay(slow) + TimeSpan.FromMilliseconds(30));
        var ready = await warming.ReadAsync(slow, false, false, "", default);
        Check(ready is { Reading.IsInitializing: false } && warmupCalls == 2
            && warming.GetRefreshDelay(slow).TotalSeconds > 599, "The first usable reading must restore the configured long interval.");
        var unreadyCalls = 0;
        using var unready = new HudSampler(readLocal: _ =>
        {
            unreadyCalls++;
            return new("测试", "—", "计数器仍不可比较") { IsInitializing = true };
        });
        await unready.ReadAsync(slow, false, false, "", default);
        await unready.ReadAsync(slow, false, false, "", default);
        Check(unreadyCalls == 2 && unready.GetRefreshDelay(slow).TotalSeconds > 599,
            "A completed but unready warm-up must not retry continuously at a higher frequency than configured.");
        Console.WriteLine("CHECK sampling: in-memory sources only; bounds, legacy import, fractional persistence, independent deadlines, cache reuse/invalidation and cancelled warm-up recovery.");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
