using System.IO.Compression;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;

internal static partial class InteractionChecks
{
    internal static void CheckOfficePreview(string fixture)
    {
        const string name = "交付说明.DOCX";
        var original = Sample("旧条款", large: false);
        var revised = Sample("新条款", large: true);
        Require(revised.Length > TextComparison.MaxBytes, "the fixture must exceed the plain text preview limit");
        var invalid = OfficeComparison.Compare(OfficeFormat.Word, original, [0, 1, 2], default);
        Require(!invalid.IsText && invalid.Summary.Contains("另存"), "a damaged document appeared unchanged");
        var wrongFormat = OfficeComparison.Compare(OfficeFormat.Excel, null, original, default);
        Require(!wrongFormat.IsText, "a Word document with an Excel extension was accepted");
        var metadataOnly = OfficeComparison.Compare(OfficeFormat.Word, original, Sample("旧条款", large: true), default);
        Require(metadataOnly.IsText && metadataOnly.Lines.Count == 0 && metadataOnly.Summary.StartsWith("文件有变化"),
            "a change outside the document body was presented as identical files");
        using (var oversized = new MemoryStream())
        {
            using (var archive = new ZipArchive(oversized, ZipArchiveMode.Create, leaveOpen: true))
            using (var part = archive.CreateEntry("word/document.xml").Open())
            {
                var block = new byte[1024 * 1024];
                for (var mb = 0; mb < 33; mb++) part.Write(block);
            }
            var limited = OfficeComparison.Compare(OfficeFormat.Word, null, oversized.ToArray(), default);
            Require(!limited.IsText && limited.Summary.Contains("上限"), "oversized XML was passed to the Office reader");
        }
        Require(OfficeComparison.GetFormat("旧文档.doc") is null && OfficeComparison.GetFormat("表格.xls") is null,
            "legacy formats were routed to Open XML");

        var host = new ServiceChecks.TestHost(fixture) { AllowPackage = true };
        var log = new EngineChecks.FakeLog();
        using var service = new FolderHistoryService(new("checks", host, host, log, CancellationToken.None)
            { Packages = host, Processes = host, Tasks = host });
        var root = Directory.CreateDirectory(Path.Combine(fixture, "office")).FullName;
        var source = Path.Combine(root, name);
        File.WriteAllBytes(source, revised);
        service.AddFolderAsync(root).GetAwaiter().GetResult();
        var repository = Directory.CreateDirectory(Path.Combine(HistoryPaths.HistoryDirectory(root), "repository")).FullName;
        var state = HistoryStore.Read(root);
        foreach (var (id, bytes) in new[] { (Earlier, original), (Later, revised) })
        {
            EngineChecks.WriteVolume(Path.Combine(repository, id), true, "SHA256",
                new { type = "Folder", path = root + Path.DirectorySeparatorChar },
                new { type = "File", path = source, size = bytes.Length, hash = Convert.ToBase64String(SHA256.HashData(bytes)) });
            state.AddVersion(id, root, "", false);
        }
        HistoryStore.Save(root, state);
        host.ProcessHandler = (request, _) =>
        {
            Require(request.Arguments[0] == "restore", "preview changed the repository");
            var staging = request.Arguments.Single(a => a.StartsWith("--restore-path=", StringComparison.Ordinal))[15..];
            File.WriteAllBytes(Path.Combine(staging, name), request.Arguments.Contains("--version=0") ? revised : original);
            return Task.FromResult(host.ProcessResult);
        };

        CreateHeadlessApplication();
        var model = new FolderHistoryViewModel(service, CancellationToken.None);
        try
        {
            var load = model.InitializeAsync();
            Until(() => load.IsCompleted, "office workspace did not initialize");
            load.GetAwaiter().GetResult();
            model.SetSelection([model.VisibleTreeItems.Single(item => item.Path == name)]);
            model.SelectedTab = 1;
            bool ShowsRevisions() => model.TextLines.Any(row => row.BeforeText == "旧条款" && row.AfterText == "新条款");
            Until(ShowsRevisions,
                "comparing snapshots did not render the Word change for a file above 4 MiB");
            Require(host.Processes == 2 && File.ReadAllBytes(source).AsSpan().SequenceEqual(revised),
                "preview read extra snapshots or modified the working file");

            var earlier = model.Versions.Single(row => row.Id == Earlier);
            var later = model.Versions.Single(row => row.Id == Later);
            // Keep rapid selection changes in one UI turn; assert extraction count, not elapsed time.
            model.SelectedVersion = earlier;
            model.SelectedVersion = later;
            model.ComparisonBase = null;
            model.ComparisonBase = earlier;
            Until(ShowsRevisions, "rapid version/base changes did not show the final comparison");
            Require(host.Processes == 4, "rapid selection extracted an intermediate preview");

            var restore = host.ProcessHandler!;
            var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            const string lateFailure = "injected stale preview stderr";
            host.ProcessHandler = async (request, cancellation) =>
            {
                if (started.TrySetResult(cancellation))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    return new(1, "", lateFailure, false, false);
                }
                return await restore(request, cancellation).ConfigureAwait(false);
            };
            model.SelectedVersion = earlier;
            Until(() => started.Task.IsCompleted, "the old preview never reached extraction");
            model.SelectedVersion = later;
            Require(started.Task.Result.IsCancellationRequested, "changing selection did not cancel the active extraction");
            Until(ShowsRevisions, "a cancelled extraction replaced or blocked the final comparison");
            Require(host.Processes == 7 && !model.HasError, "cancelled preview restarted or reported a page error");
            Require(log.Entries.Count(entry => entry.Level == AppLogLevel.Warning &&
                entry.Error?.ToString().Contains(lateFailure, StringComparison.Ordinal) == true) == 1,
                "a superseded preview lost or duplicated the engine failure warning");

            model.SelectedVersion = earlier;
            model.Detach();
            var settled = Task.Delay(TimeSpan.FromSeconds(1));
            Until(() => settled.IsCompleted, "pending preview cancellation did not settle");
            Require(host.Processes == 7 && model.TextLines.Count == 0 && !model.HasError,
                "leaving during the selection delay started an extraction or applied a stale preview");
        }
        finally { model.Detach(); }
    }

    private static byte[] Sample(string text, bool large)
    {
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
            package.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
        if (large)
        {
            // A valid uncompressed XML part makes the package large without increasing the compared body.
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
            using var extra = new StreamWriter(archive.CreateEntry("customXml/item1.xml", CompressionLevel.NoCompression).Open());
            extra.Write("<data>");
            extra.Write(new string(' ', TextComparison.MaxBytes));
            extra.Write("</data>");
        }
        return stream.ToArray();
    }
}
