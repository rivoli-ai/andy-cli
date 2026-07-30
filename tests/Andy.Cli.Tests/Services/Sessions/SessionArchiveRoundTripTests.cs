using System;
using System.IO;
using System.Linq;
using Andy.Cli.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Export/import of the portable session archive (issue #285): fidelity of turns, parts,
/// metadata, and lineage; conflict-safe id assignment; the dry-run summary.
/// </summary>
public class SessionArchiveRoundTripTests : SessionArchiveTestBase
{
    public SessionArchiveRoundTripTests() : base("archive-roundtrip") { }

    private string SaveRichSession(string? title = null, SessionUsage? usage = null)
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.RichSnapshot(), "openai", "gpt-4o",
            new SessionSaveOptions { Title = title, Usage = usage });
        return id;
    }

    [Fact]
    public void ExportThenImport_PreservesTurnsPartsAndMetadata()
    {
        var sourceId = SaveRichSession(title: "Notes review");
        var archivePath = WorkPath("archive.json");

        var export = SessionArchiveExporter.Export(Store, sourceId, archivePath);
        Assert.True(File.Exists(export.Path));
        Assert.Equal(2, export.TurnCount);
        Assert.False(string.IsNullOrEmpty(export.Checksum));

        // Import into a SEPARATE store, mimicking a different machine.
        var otherDirectory = SessionArchiveTestData.NewTempDirectory("archive-roundtrip-other");
        try
        {
            var otherStore = SessionArchiveTestData.CreateStore(otherDirectory);
            var result = SessionArchiveImporter.ImportFile(otherStore, export.Path);

            Assert.True(result.Installed);
            Assert.Equal(sourceId, result.SessionId);
            Assert.False(result.IdWasRemapped);

            var imported = otherStore.Load(result.SessionId);
            Assert.NotNull(imported);
            Assert.Equal("Notes review", imported!.Summary.Title);
            Assert.Equal("openai", imported.Summary.Provider);
            Assert.Equal("gpt-4o", imported.Summary.Model);
            Assert.Equal(2, imported.Summary.TurnCount);

            var original = Store.Load(sourceId)!;
            var originalTurns = original.Snapshot.Turns!;
            var importedTurns = imported.Snapshot.Turns!;
            Assert.Equal(originalTurns.Count, importedTurns.Count);
            for (var i = 0; i < originalTurns.Count; i++)
            {
                Assert.Equal(originalTurns[i].User!.Content, importedTurns[i].User!.Content);
                Assert.Equal(originalTurns[i].FinalAssistant!.Content, importedTurns[i].FinalAssistant!.Content);
            }

            // Structured parts survive.
            var parts = importedTurns[0].User!.Parts;
            Assert.NotNull(parts);
            Assert.Equal("text", parts![0].Type);
            Assert.Equal("read my notes", parts[0].Text);

            // Tool calls and results survive.
            var interleaved = importedTurns[0].Interleaved!;
            var call = interleaved.SelectMany(m => m.ToolCalls ?? Array.Empty<Andy.Engine.TranscriptToolCall>()).Single();
            Assert.Equal("read_file", call.Name);
            Assert.Contains("/tmp/notes.txt", call.ArgumentsJson);
            var toolResult = interleaved.SelectMany(m => m.ToolResults ?? Array.Empty<Andy.Engine.TranscriptToolResult>()).Single();
            Assert.Equal("call_1", toolResult.CallId);
            Assert.Contains("line one", toolResult.ResultJson);

            // Lineage records where the session came from.
            Assert.Equal(sourceId, imported.Summary.Lineage!.ImportedFromSessionId);
        }
        finally
        {
            SessionArchiveTestData.DeleteDirectory(otherDirectory);
        }
    }

    [Fact]
    public void Import_IntoStoreThatAlreadyHasTheId_AssignsConflictSafeId()
    {
        var sourceId = SaveRichSession();
        var export = SessionArchiveExporter.Export(Store, sourceId, WorkPath("a.json"));

        // Import back into the SAME store: the id is taken, so a new one must be minted
        // rather than overwriting the original.
        var result = SessionArchiveImporter.ImportFile(Store, export.Path);

        Assert.True(result.IdWasRemapped);
        Assert.NotEqual(sourceId, result.SessionId);
        Assert.Equal(sourceId, result.OriginalSessionId);
        Assert.True(SessionStore.IsValidSessionId(result.SessionId));

        Assert.NotNull(Store.Load(sourceId));
        Assert.NotNull(Store.Load(result.SessionId));
        Assert.Equal(2, SessionArchiveTestData.SessionFiles(StoreDirectory).Length);

        // Lineage keeps the original identity so the copy is still traceable.
        var imported = Store.Load(result.SessionId)!;
        Assert.Equal(sourceId, imported.Summary.Lineage!.ImportedFromSessionId);
    }

    [Fact]
    public void Import_RepeatedIntoSameStore_KeepsMintingDistinctIds()
    {
        var sourceId = SaveRichSession();
        var export = SessionArchiveExporter.Export(Store, sourceId, WorkPath("a.json"));

        var first = SessionArchiveImporter.ImportFile(Store, export.Path);
        var second = SessionArchiveImporter.ImportFile(Store, export.Path);

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(3, SessionArchiveTestData.SessionFiles(StoreDirectory).Length);
    }

    [Fact]
    public void Import_PreservesForkLineageAcrossMachines()
    {
        var rootId = SaveRichSession();
        var fork = SessionForker.Fork(Store, rootId, atTurn: 2);
        var export = SessionArchiveExporter.Export(Store, fork.SessionId, WorkPath("fork.json"));

        var otherDirectory = SessionArchiveTestData.NewTempDirectory("archive-lineage-other");
        try
        {
            var otherStore = SessionArchiveTestData.CreateStore(otherDirectory);
            var result = SessionArchiveImporter.ImportFile(otherStore, export.Path);
            var imported = otherStore.Load(result.SessionId)!;

            Assert.Equal(rootId, imported.Summary.Lineage!.ParentSessionId);
            Assert.Equal(rootId, imported.Summary.Lineage.RootSessionId);
            Assert.Equal(2, imported.Summary.Lineage.ForkedAtTurn);
        }
        finally
        {
            SessionArchiveTestData.DeleteDirectory(otherDirectory);
        }
    }

    [Fact]
    public void ImportDryRun_ReportsWhatWouldHappenAndWritesNothing()
    {
        var sourceId = SaveRichSession(title: "Dry run subject");
        var export = SessionArchiveExporter.Export(Store, sourceId, WorkPath("a.json"));

        var otherDirectory = SessionArchiveTestData.NewTempDirectory("archive-dryrun-other");
        try
        {
            var otherStore = SessionArchiveTestData.CreateStore(otherDirectory);
            var result = SessionArchiveImporter.ImportFile(otherStore, export.Path, dryRun: true);

            Assert.False(result.Installed);
            Assert.Equal(sourceId, result.SessionId);
            Assert.Equal(2, result.TurnCount);
            Assert.Contains("Dry run", result.Describe());
            Assert.Empty(SessionArchiveTestData.SessionFiles(otherDirectory));
            Assert.Null(otherStore.Load(sourceId));
        }
        finally
        {
            SessionArchiveTestData.DeleteDirectory(otherDirectory);
        }
    }

    [Fact]
    public void Import_DoesNotExecuteToolsOrReplaySideEffects()
    {
        // The archive's transcript contains a tool call that, if replayed, would create a
        // file. Import must only write the session file.
        var marker = WorkPath("side-effect-marker.txt");
        Assert.False(File.Exists(marker));

        var id = SessionStore.NewSessionId();
        var snapshot = SessionArchiveTestData.RichSnapshot(
            toolArgumentsJson: "{\"path\":\"" + marker.Replace("\\", "\\\\") + "\",\"content\":\"boom\"}");
        Store.Save(id, snapshot, "openai", "gpt-4o");
        var export = SessionArchiveExporter.Export(Store, id, WorkPath("tools.json"));

        var otherDirectory = SessionArchiveTestData.NewTempDirectory("archive-noexec-other");
        try
        {
            var otherStore = SessionArchiveTestData.CreateStore(otherDirectory);
            SessionArchiveImporter.ImportFile(otherStore, export.Path);

            Assert.False(File.Exists(marker));
            Assert.Single(SessionArchiveTestData.SessionFiles(otherDirectory));
        }
        finally
        {
            SessionArchiveTestData.DeleteDirectory(otherDirectory);
        }
    }

    [Fact]
    public void Export_ToDirectory_UsesConventionalFileName()
    {
        var id = SaveRichSession();
        var result = SessionArchiveExporter.Export(Store, id, WorkDirectory);

        Assert.Equal(Path.Combine(WorkDirectory, SessionArchive.DefaultFileName(id)), result.Path);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void Export_LeavesNoTempFileBehind()
    {
        var id = SaveRichSession();
        SessionArchiveExporter.Export(Store, id, WorkPath("clean.json"));

        Assert.DoesNotContain(Directory.GetFiles(WorkDirectory),
            f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_UnknownSession_Throws()
    {
        var missing = SessionStore.NewSessionId();
        var ex = Assert.Throws<SessionArchiveException>(
            () => SessionArchiveExporter.Export(Store, missing, WorkPath("x.json")));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void ImportWithTitleOverride_RenamesTheInstalledSession()
    {
        var sourceId = SaveRichSession(title: "Original title");
        var export = SessionArchiveExporter.Export(Store, sourceId, WorkPath("a.json"));

        var result = SessionArchiveImporter.ImportFile(Store, export.Path, title: "Renamed on import");

        Assert.Equal("Renamed on import", result.Title);
        Assert.Equal("Renamed on import", Store.Load(result.SessionId)!.Summary.Title);
    }
}
