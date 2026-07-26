using System;
using System.IO;
using Andy.Cli.Services.Sessions;
using Andy.Engine;
using Xunit;

namespace Andy.Cli.Tests.Services.Sessions;

/// <summary>
/// Human-readable Markdown export (issue #285): content, the tool-detail and model-metadata
/// options, and the guarantee that it leaks no more than the machine-readable archive.
/// </summary>
public class SessionMarkdownExporterTests : SessionArchiveTestBase
{
    public SessionMarkdownExporterTests() : base("markdown") { }

    private string SaveRich(string? title = null, SessionUsage? usage = null, SessionOrigin? origin = null)
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.RichSnapshot(), "openai", "gpt-4o",
            new SessionSaveOptions { Title = title, Usage = usage, Origin = origin });
        return id;
    }

    [Fact]
    public void Markdown_ContainsEveryUserAndAssistantTurn()
    {
        var id = SaveRich();
        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!);

        Assert.Contains("## Turn 1", markdown);
        Assert.Contains("## Turn 2", markdown);
        Assert.Contains("read my notes", markdown);
        Assert.Contains("Your notes say: line one", markdown);
        Assert.Contains("You are welcome.", markdown);
    }

    [Fact]
    public void WithoutToolDetails_ToolPayloadsAreOmittedButSummarized()
    {
        var id = SaveRich();
        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!, new SessionMarkdownOptions
        {
            IncludeToolDetails = false
        });

        Assert.DoesNotContain("/tmp/notes.txt", markdown);
        Assert.Contains("1 tool call executed (details omitted)", markdown);
    }

    [Fact]
    public void WithToolDetails_ArgumentsAndResultsAreIncluded()
    {
        var id = SaveRich();
        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!, new SessionMarkdownOptions
        {
            IncludeToolDetails = true
        });

        Assert.Contains("### Tool call: `read_file`", markdown);
        Assert.Contains("/tmp/notes.txt", markdown);
        Assert.Contains("### Tool result: `read_file`", markdown);
        Assert.Contains("line one", markdown);
    }

    [Fact]
    public void WithoutModelMetadata_TheHeaderIsMinimal()
    {
        var id = SaveRich(title: "My session");
        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!);

        Assert.Contains("# My session", markdown);
        Assert.DoesNotContain("## Metadata", markdown);
        Assert.DoesNotContain("gpt-4o", markdown);
    }

    [Fact]
    public void WithModelMetadata_ProviderModelLineageOriginAndUsageAreIncluded()
    {
        var rootId = SaveRich(title: "Root",
            usage: new SessionUsage
            {
                InputTokens = 1200,
                OutputTokens = 340,
                ReasoningTokens = 55,
                CacheReadTokens = 900,
                CacheWriteTokens = 12,
                EstimatedCostUsd = 0.0087m
            },
            origin: new SessionOrigin { Platform = "linux", WorkspacePath = "/home/dev/proj" });
        var fork = SessionForker.Fork(Store, rootId, atTurn: 2);

        var markdown = SessionMarkdownExporter.Render(Store.Load(fork.SessionId)!, new SessionMarkdownOptions
        {
            IncludeModelMetadata = true
        });

        Assert.Contains("## Metadata", markdown);
        Assert.Contains("openai/gpt-4o", markdown);
        Assert.Contains("Forked from: `" + rootId + "`", markdown);
        Assert.Contains("(before turn 2)", markdown);
        Assert.Contains("/home/dev/proj", markdown);
    }

    [Fact]
    public void MetadataDistinguishesUnknownPricingFromZeroCost()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.Snapshot(1), "someprovider", "unpriced-model-x",
            new SessionSaveOptions
            {
                Usage = SessionUsage.FromTokenCounts(100, 100).WithEstimatedCost("someprovider", "unpriced-model-x")
            });

        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!, new SessionMarkdownOptions
        {
            IncludeModelMetadata = true
        });

        Assert.Contains("Estimated cost: unknown (no pricing data)", markdown);
    }

    [Fact]
    public void SecretShapedContent_IsAbsentFromBothTheArchiveAndTheMarkdown()
    {
        // Synthetic, never-valid credentials assembled at runtime so the repository's
        // secret scanner does not flag this fixture.
        var apiKey = SessionArchiveTestData.SyntheticApiKey();
        var bearer = SessionArchiveTestData.SyntheticBearerToken();
        const string literalSecret = "fixture-literal-secret-value";

        var snapshot = new TranscriptSnapshot
        {
            Version = TranscriptSnapshot.CurrentVersion,
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = SessionArchiveTestData.Message("user", $"use api_key={apiKey} for me"),
                    Interleaved = new[]
                    {
                        SessionArchiveTestData.Message("assistant", $"Calling with {bearer}", calls: new[]
                        {
                            new TranscriptToolCall
                            {
                                Id = "c1",
                                Name = "http_request",
                                ArgumentsJson = "{\"authorization\":\"" + bearer + "\"}"
                            }
                        }),
                        SessionArchiveTestData.Message("tool", "", results: new[]
                        {
                            new TranscriptToolResult
                            {
                                CallId = "c1",
                                Name = "http_request",
                                ResultJson = "{\"note\":\"" + literalSecret + "\"}"
                            }
                        })
                    },
                    FinalAssistant = SessionArchiveTestData.Message("assistant", $"Stored {literalSecret} for later")
                }
            }
        };

        // A store whose redactor also knows the literal secret value, as the real one does
        // for secret-looking environment variables.
        var store = new SessionStore(StoreDirectory, new SessionRedactor(new[] { literalSecret }));
        var id = SessionStore.NewSessionId();
        store.Save(id, snapshot, "openai", "gpt-4o");

        var archive = SessionArchiveExporter.Export(store, id, WorkPath("secrets.json"),
            new SessionRedactor(new[] { literalSecret }));
        var archiveText = File.ReadAllText(archive.Path);

        var markdown = SessionMarkdownExporter.Render(
            store.Load(id)!,
            new SessionMarkdownOptions { IncludeToolDetails = true, IncludeModelMetadata = true },
            new SessionRedactor(new[] { literalSecret }));

        foreach (var text in new[] { archiveText, markdown })
        {
            Assert.DoesNotContain(apiKey, text);
            Assert.DoesNotContain(literalSecret, text);
            Assert.DoesNotContain(bearer, text);
            Assert.Contains(SessionRedactor.Replacement, text);
        }
    }

    [Fact]
    public void FencedToolPayloads_CannotBreakOutOfTheirCodeBlock()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.RichSnapshot(toolResultJson: "before ``` after"),
            "openai", "gpt-4o");

        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!, new SessionMarkdownOptions
        {
            IncludeToolDetails = true
        });

        Assert.Contains("````", markdown);
    }

    [Fact]
    public void LongToolPayloads_AreTruncated()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, SessionArchiveTestData.RichSnapshot(toolResultJson: new string('a', 5000)),
            "openai", "gpt-4o");

        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!, new SessionMarkdownOptions
        {
            IncludeToolDetails = true,
            MaxToolPayloadChars = 100
        });

        Assert.Contains("(truncated)", markdown);
        Assert.DoesNotContain(new string('a', 200), markdown);
    }

    [Fact]
    public void ExportMarkdown_WritesTheFileAtomicallyToDisk()
    {
        var id = SaveRich(title: "Written out");

        var result = SessionArchiveExporter.ExportMarkdown(Store, id, WorkDirectory);

        Assert.Equal(Path.Combine(WorkDirectory, SessionArchive.DefaultMarkdownFileName(id)), result.Path);
        Assert.Contains("# Written out", File.ReadAllText(result.Path));
        Assert.DoesNotContain(Directory.GetFiles(WorkDirectory),
            f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void TurnWithoutAFinalAnswer_IsMarked()
    {
        var id = SessionStore.NewSessionId();
        Store.Save(id, new TranscriptSnapshot
        {
            Version = TranscriptSnapshot.CurrentVersion,
            Turns = new[]
            {
                new TranscriptTurn
                {
                    User = SessionArchiveTestData.Message("user", "hello"),
                    Interleaved = Array.Empty<TranscriptMessage>(),
                    FinalAssistant = null
                }
            }
        }, "openai", "gpt-4o");

        var markdown = SessionMarkdownExporter.Render(Store.Load(id)!);
        Assert.Contains("_Turn ended without a final answer._", markdown);
    }
}
