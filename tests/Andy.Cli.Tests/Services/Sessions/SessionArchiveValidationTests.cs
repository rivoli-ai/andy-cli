using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Andy.Cli.Services.Sessions;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Archive rejection paths (issue #285). Every case must fail ATOMICALLY: after a rejected
/// import the session directory is exactly as it was, with no partially installed session
/// and no stray temp file.
/// </summary>
public class SessionArchiveValidationTests : SessionArchiveTestBase
{
    public SessionArchiveValidationTests() : base("archive-validation") { }

    private string WriteValidArchive(string fileName = "valid.json")
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.RichSnapshot(), "openai", "gpt-4o");
        var export = SessionArchiveExporter.Export(Store, id, WorkPath(fileName));
        // Start each rejection case from an empty store so "nothing was installed" is
        // unambiguous.
        File.Delete(Path.Combine(StoreDirectory, id + ".json"));
        return export.Path;
    }

    private static JsonObject Load(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidOperationException("not an object");

    private static void Rewrite(string path, JsonObject document) =>
        File.WriteAllText(path, document.ToJsonString());

    private void AssertNothingInstalled()
    {
        Assert.Empty(SessionArchiveTestData.SessionFiles(StoreDirectory));
    }

    [Fact]
    public void CorruptJson_IsRejected()
    {
        var path = WorkPath("corrupt.json");
        File.WriteAllText(path, "{ this is not json ");

        var ex = Assert.Throws<SessionArchiveException>(
            () => SessionArchiveImporter.ImportFile(Store, path));
        Assert.Contains("not valid JSON", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void TruncatedArchive_IsRejected()
    {
        var path = WriteValidArchive();
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text[..(text.Length / 2)]);

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Fact]
    public void TamperedPayload_FailsTheChecksum()
    {
        var path = WriteValidArchive();
        var document = Load(path);
        // Change the transcript but leave the recorded checksum alone.
        document["session"]!["firstUserMessage"] = "tampered";
        Rewrite(path, document);

        var ex = Assert.Throws<SessionArchiveException>(
            () => SessionArchiveImporter.ImportFile(Store, path));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        AssertNothingInstalled();
    }

    [Fact]
    public void MissingChecksum_IsRejected()
    {
        var path = WriteValidArchive();
        var document = Load(path);
        document.Remove("checksum");
        Rewrite(path, document);

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Fact]
    public void UnknownChecksumAlgorithm_IsRejected()
    {
        var path = WriteValidArchive();
        var document = Load(path);
        document["checksum"]!["algorithm"] = "crc32";
        Rewrite(path, document);

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Fact]
    public void UnsupportedFutureSchemaVersion_FailsSafelyWithoutInstalling()
    {
        var path = WriteValidArchive();
        var document = Load(path);
        document["schemaVersion"] = SessionArchive.SchemaVersion + 1;
        Rewrite(path, document);

        var ex = Assert.Throws<NotSupportedException>(
            () => SessionArchiveImporter.ImportFile(Store, path));
        Assert.Contains("newer than this build supports", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void ForeignFormat_IsRejected()
    {
        var path = WorkPath("foreign.json");
        File.WriteAllText(path, "{\"format\":\"some-other-tool\",\"schemaVersion\":1}");

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("../evil")]
    [InlineData("dir/evil")]
    [InlineData("dir\\evil")]
    [InlineData("..")]
    [InlineData("")]
    public void PathTraversalSessionId_IsRejectedEvenWithAValidChecksum(string hostileId)
    {
        var path = WriteValidArchive();
        var document = Load(path);
        var session = (JsonObject)document["session"]!;
        session["sessionId"] = hostileId;
        // Recompute the checksum so the ONLY thing wrong is the hostile id: this proves the
        // id itself is validated, not merely protected by the integrity check.
        document["checksum"]!["value"] = SessionArchive.ComputeChecksum(session);
        Rewrite(path, document);

        var ex = Assert.Throws<SessionArchiveException>(
            () => SessionArchiveImporter.ImportFile(Store, path));
        Assert.Contains("safe session id", ex.Message);
        AssertNothingInstalled();

        // Nothing escaped into the parent of the session directory either.
        var parent = Directory.GetParent(StoreDirectory)!.FullName;
        Assert.False(File.Exists(Path.Combine(parent, "evil.json")));
    }

    [Fact]
    public void EmptyTranscript_IsRejected()
    {
        var path = WriteValidArchive();
        var document = Load(path);
        var session = (JsonObject)document["session"]!;
        session["transcript"] = new JsonObject { ["version"] = 1, ["turns"] = new JsonArray() };
        document["checksum"]!["value"] = SessionArchive.ComputeChecksum(session);
        Rewrite(path, document);

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Fact]
    public void OversizedArchive_IsRejectedBeforeParsing()
    {
        var path = WriteValidArchive();
        var size = new FileInfo(path).Length;

        var ex = Assert.Throws<SessionArchiveException>(
            () => SessionArchiveImporter.ImportFile(Store, path, maxBytes: size - 1));
        Assert.Contains("limit", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void OversizedJsonText_IsRejectedByParse()
    {
        var json = new string('x', 64);
        var ex = Assert.Throws<SessionArchiveException>(() => SessionArchive.Parse(json, maxBytes: 16));
        Assert.Contains("limit", ex.Message);
    }

    [Fact]
    public void MissingArchiveFile_IsRejected()
    {
        var ex = Assert.Throws<SessionArchiveException>(
            () => SessionArchiveImporter.ImportFile(Store, WorkPath("nope.json")));
        Assert.Contains("not found", ex.Message);
        AssertNothingInstalled();
    }

    [Fact]
    public void EmptyArchiveFile_IsRejected()
    {
        var path = WorkPath("empty.json");
        File.WriteAllText(path, "");

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Fact]
    public void ArchiveWithoutSessionPayload_IsRejected()
    {
        var path = WorkPath("nosession.json");
        File.WriteAllText(path, new JsonObject
        {
            ["format"] = SessionArchive.FormatId,
            ["schemaVersion"] = SessionArchive.SchemaVersion
        }.ToJsonString());

        Assert.Throws<SessionArchiveException>(() => SessionArchiveImporter.ImportFile(Store, path));
        AssertNothingInstalled();
    }

    [Fact]
    public void Checksum_IsStableAcrossPrettyPrinting()
    {
        var path = WriteValidArchive();
        var document = Load(path);
        var expected = document["checksum"]!["value"]!.GetValue<string>();

        // Re-serialize the whole archive with indentation: the payload checksum covers the
        // compact form of the session object, so formatting must not affect it.
        File.WriteAllText(path, document.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var reread = SessionArchiveImporter.ReadFile(path);
        Assert.Equal(expected, reread.Checksum);
    }
}
