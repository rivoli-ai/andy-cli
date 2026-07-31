using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.Cli.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Backwards compatibility of the schema-version-1 session envelope and the cross-platform
/// path metadata added in issue #285.
/// </summary>
public class SessionCompatibilityAndOriginTests : SessionArchiveTestBase
{
    public SessionCompatibilityAndOriginTests() : base("compat-origin") { }

    /// <summary>
    /// A byte-for-byte reproduction of the envelope shape written BEFORE issue #285: schema
    /// version 1 with no title, lineage, origin, or usage.
    /// </summary>
    private string WriteLegacyEnvelope(string sessionId)
    {
        var envelope = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["sessionId"] = sessionId,
            ["createdUtc"] = "2026-01-02T03:04:05.0000000Z",
            ["updatedUtc"] = "2026-01-02T04:05:06.0000000Z",
            ["provider"] = "openai",
            ["model"] = "gpt-4o",
            ["turnCount"] = 2,
            ["firstUserMessage"] = "question 1",
            ["transcript"] = JsonNode.Parse(SessionArchiveTestData.Snapshot(2).ToJson())
        };
        var path = Path.Combine(StoreDirectory, sessionId + ".json");
        File.WriteAllText(path, envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    [Fact]
    public void LegacySchemaVersion1Session_StillLoads()
    {
        var id = SessionStore.NewSessionId();
        WriteLegacyEnvelope(id);

        var record = Store.Load(id);

        Assert.NotNull(record);
        Assert.Equal(2, record!.Snapshot.Turns!.Count);
        Assert.Equal("openai", record.Summary.Provider);
        Assert.Equal("", record.Summary.Title);
        Assert.Null(record.Summary.Lineage);
        Assert.Null(record.Summary.Origin);
        Assert.Null(record.Summary.Usage);
    }

    [Fact]
    public void LegacySession_ListsAndExportsAndImports()
    {
        var id = SessionStore.NewSessionId();
        WriteLegacyEnvelope(id);

        Assert.Contains(Store.List(), s => s.SessionId == id);

        var export = SessionArchiveExporter.Export(Store, id, WorkPath("legacy.json"));
        var result = SessionArchiveImporter.ImportFile(Store, export.Path);

        Assert.True(result.IdWasRemapped);
        Assert.Equal(2, Store.Load(result.SessionId)!.Snapshot.Turns!.Count);
    }

    [Fact]
    public void ResavingALegacySession_KeepsItsCreationTimestamp()
    {
        var id = SessionStore.NewSessionId();
        WriteLegacyEnvelope(id);

        Store.Save(id, SessionArchiveTestData.Snapshot(3), "openai", "gpt-4o");

        var reloaded = Store.Load(id)!;
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T03:04:05.0000000Z").ToUniversalTime(),
            reloaded.Summary.CreatedUtc.ToUniversalTime());
        Assert.Equal(3, reloaded.Summary.TurnCount);
    }

    [Fact]
    public void NewEnvelopeStillDeclaresSchemaVersion1()
    {
        // The #285 fields are ADDITIVE within version 1; bumping the version would make
        // every existing session unreadable by this build.
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o",
            new SessionSaveOptions { Title = "t", Usage = new SessionUsage { InputTokens = 1 } });

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(StoreDirectory, id + ".json")));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void SessionWithoutOptionalFields_OmitsThemFromTheFile()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o");

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(StoreDirectory, id + ".json")));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("lineage", out _));
        Assert.False(root.TryGetProperty("usage", out _));
    }

    [Theory]
    [InlineData("windows", @"C:\Users\dev\projects\andy")]
    [InlineData("macos", "/Users/dev/projects/andy")]
    [InlineData("linux", "/home/dev/projects/andy")]
    public void OriginPathMetadata_RoundTripsThroughAnArchiveUnchanged(string platform, string path)
    {
        var id = SessionStore.NewSessionId();
        var origin = new SessionOrigin { Platform = platform, WorkspacePath = path };
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o",
            new SessionSaveOptions { Origin = origin });

        var export = SessionArchiveExporter.Export(Store, id, WorkPath($"origin-{platform}.json"));
        var result = SessionArchiveImporter.ImportFile(Store, export.Path);

        var imported = Store.Load(result.SessionId)!.Summary.Origin!;
        Assert.Equal(platform, imported.Platform);
        Assert.Equal(path, imported.WorkspacePath);
    }

    [Theory]
    [InlineData("windows", @"C:\Users\dev\projects\andy")]
    [InlineData("macos", "/Users/dev/nonexistent-workspace-9f2a")]
    [InlineData("linux", "/home/dev/nonexistent-workspace-9f2a")]
    public void ForeignWorkspacePath_IsInformationalAndNeverResolvesLocally(string platform, string path)
    {
        var origin = new SessionOrigin { Platform = platform, WorkspacePath = path };

        Assert.Null(origin.ResolveLocalWorkspace());
        Assert.Contains("informational", origin.Describe());
    }

    [Fact]
    public void LocalWorkspacePath_ResolvesWhenItActuallyExists()
    {
        var origin = new SessionOrigin
        {
            Platform = SessionOrigin.CurrentPlatform(),
            WorkspacePath = WorkDirectory
        };

        Assert.Equal(WorkDirectory, origin.ResolveLocalWorkspace());
        Assert.DoesNotContain("informational", origin.Describe());
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("relative/path")]
    [InlineData("")]
    public void TraversalOrRelativeWorkspacePaths_NeverResolve(string path)
    {
        var origin = new SessionOrigin { Platform = "linux", WorkspacePath = path };
        Assert.Null(origin.ResolveLocalWorkspace());
    }

    [Fact]
    public void ImportingAnArchiveWithAHostileWorkspacePath_StillWritesOnlyIntoTheSessionDirectory()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o",
            new SessionSaveOptions
            {
                Origin = new SessionOrigin { Platform = "linux", WorkspacePath = "../../../../tmp/escape" }
            });
        var export = SessionArchiveExporter.Export(Store, id, WorkPath("hostile-origin.json"));

        var otherDirectory = SessionArchiveTestData.NewTempDirectory("compat-hostile");
        try
        {
            var otherStore = SessionArchiveTestData.CreateStore(otherDirectory);
            var result = SessionArchiveImporter.ImportFile(otherStore, export.Path);

            var files = SessionArchiveTestData.SessionFiles(otherDirectory);
            Assert.Single(files);
            Assert.Equal(result.SessionId + ".json", files[0]);
            // The path travelled with the archive but is inert.
            Assert.Equal("../../../../tmp/escape",
                otherStore.Load(result.SessionId)!.Summary.Origin!.WorkspacePath);
            Assert.Null(otherStore.Load(result.SessionId)!.Summary.Origin!.ResolveLocalWorkspace());
        }
        finally
        {
            SessionArchiveTestData.DeleteDirectory(otherDirectory);
        }
    }

    [Fact]
    public void Rename_SetsAndClearsTheTitleWithoutTouchingTheTranscript()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(3), "openai", "gpt-4o");

        Assert.True(Store.Rename(id, "  Interesting work  "));
        var renamed = Store.Load(id)!;
        Assert.Equal("Interesting work", renamed.Summary.Title);
        Assert.Equal(3, renamed.Snapshot.Turns!.Count);

        Assert.True(Store.Rename(id, null));
        Assert.Equal("", Store.Load(id)!.Summary.Title);
    }

    [Fact]
    public void Rename_OfMissingSession_ReturnsFalse()
    {
        Assert.False(Store.Rename(SessionStore.NewSessionId(), "nope"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    public void Rename_RejectsUnsafeIds(string id)
    {
        Assert.Throws<ArgumentException>(() => Store.Rename(id, "x"));
    }

    [Fact]
    public void TitleSurvivesAnOrdinaryResave()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o",
            new SessionSaveOptions { Title = "Keep me" });

        Store.Save(id, SessionArchiveTestData.Snapshot(2), "openai", "gpt-4o");

        Assert.Equal("Keep me", Store.Load(id)!.Summary.Title);
    }

    [Fact]
    public void List_IgnoresSiblingApprovalFilesThatCarryTheirOwnSessionId()
    {
        // SessionApprovalStore writes <id>.approvals.json into the SAME directory and that
        // file also has a "sessionId" property; counting it as a transcript would double
        // count every session in the listing and in the usage totals.
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(2), "openai", "gpt-4o");
        File.WriteAllText(
            Path.Combine(StoreDirectory, id + ".approvals.json"),
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["sessionId"] = id,
                ["approvals"] = new JsonArray()
            }.ToJsonString());

        var listed = Store.List();

        Assert.Single(listed);
        Assert.Equal(id, listed[0].SessionId);
        Assert.Equal(2, listed[0].TurnCount);
    }

    [Fact]
    public void NewUniqueSessionId_NeverCollidesWithAnExistingFile()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "openai", "gpt-4o");

        for (var i = 0; i < 20; i++)
        {
            var candidate = Store.NewUniqueSessionId();
            Assert.NotEqual(id, candidate);
            Assert.False(Store.Exists(candidate));
        }
    }
}
