using TractorTool.Contracts.Tasks;
using TractorTool.Core.Logging;
using TractorTool.Core.Tasks;
using TractorTool.ViewModels;

internal static class TaskUiChecks
{
    public static void Run(Action<string, Action> check, Action<Task> pump, string output)
    {
        check("Public task rows route by owner, refresh availability and keep batch cancellation scoped", () =>
        {
            var root = Path.Combine(output, "task-ui-" + Guid.NewGuid().ToString("N"));
            var service = new BackgroundTaskService(Path.Combine(root, "tasks.json"), new AppLog(root));
            using var archive = service.CreateScope("Fixture.Archive");
            using var images = service.CreateScope("Fixture.Images");
            var names = new Dictionary<string, string> { ["Fixture.Archive"] = "压缩工具", ["Fixture.Images"] = "图片工具" };
            var available = new HashSet<string>(names.Keys);
            string? opened = null;
            using var vm = new TasksViewModel(service, id => opened = id, id => names[id], available.Contains);
            var batch = Guid.NewGuid(); // Even a shared batch ID must not cancel another plugin's task.
            var queuedArchive = archive.QueueBatchAsync([new(batch, "归档一"), new(batch, "归档二")]);
            var queuedImages = images.QueueAsync(new(batch, "图片处理"));
            try
            {
                pump(Task.WhenAll(queuedArchive, queuedImages)); vm.Refresh();
                var a = vm.Items.Single(x => x.Snapshot.Title == "归档一");
                var b = vm.Items.Single(x => x.Snapshot.Title == "图片处理");
                Require(a.OpenLabel == "打开压缩工具" && b.OpenLabel == "打开图片工具");
                a.OpenCommand.Execute(null); Require(opened == "Fixture.Archive");
                b.OpenCommand.Execute(null); Require(opened == "Fixture.Images");
                available.Remove("Fixture.Images"); vm.Refresh();
                Require(b.OpenLabel == "查看插件" && b.OpenDescription.Contains("不可用"));
                b.OpenCommand.Execute(null); Require(opened == "Fixture.Images");
                a.CancelBatchCommand.Execute(null);
                foreach (var task in queuedArchive.Result)
                {
                    var cancelled = task.RunAsync((_, _) => throw new Exception("Cancelled task must not execute"));
                    pump(cancelled); Require(cancelled.Result.State == BackgroundTaskState.Cancelled);
                }
                Require(service.Snapshot.Single(x => x.PluginId == "Fixture.Images").State == BackgroundTaskState.Queued);
                var completed = queuedImages.Result.RunAsync((_, _) => Task.FromResult<string?>("图片已处理"));
                pump(completed); Require(completed.Result.State == BackgroundTaskState.Succeeded);
            }
            finally { pump(service.BeginShutdownAsync()); }
        });
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Task UI assertion failed."); }
}
