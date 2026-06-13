// A SWE-Verified-style end-to-end test: stand up a small repository with a
// known bug, run the headless agent loop against a REAL model (OpenRouter /
// MiMo v2.5), and assert the agent actually edited the file on disk to fix the
// bug — with a clean, BOM-free result.
//
// This is the regression that motivated the work: the file-write tools in
// Andy.Tools emitted a UTF-8 BOM (Encoding.UTF8 has encoderShouldEmitUTF8Identifier
// = true), so even a correct edit corrupted the file with a spurious leading
// EF BB BF — every produced patch was wrong at byte zero. The deterministic
// unit-level coverage lives in Andy.Tools (WriteFileToolBomTests /
// ReplaceTextToolBomTests); this test proves the whole headless pipeline —
// permission allow-list, tool dispatch, real LLM tool calls, file write — works
// against a real model.
//
// The test needs a network model, so it self-skips when OPENROUTER_API_KEY is
// not present (CI without the secret stays green); it runs locally where the key
// is set.

using System.IO;
using System.Text;
using Andy.Cli.Headless;
using Andy.Cli.HeadlessConfig;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Cli.Tests.Headless;

public class HeadlessSweTaskTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public async Task HeadlessAgent_FixesBuggyFunction_WritesCleanNonBomEdit()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // No model credentials available (e.g. CI without the secret). This
            // test needs a real network model, so treat the absent key as a skip
            // rather than a failure. Deterministic BOM coverage lives in
            // Andy.Tools' WriteFileToolBomTests / ReplaceTextToolBomTests.
            return;
        }

        using var workspace = new TempDir();
        var repo = Path.Combine(workspace.Path, "repo");
        Directory.CreateDirectory(repo);

        // The "broken" repository state: add(a, b) subtracts. Written as raw
        // bytes with no BOM so we can prove the agent's edit doesn't add one.
        var calcPath = Path.Combine(repo, "calc.py");
        const string buggy =
            "def add(a, b):\n    # BUG: subtraction instead of addition\n    return a - b\n\n\ndef multiply(a, b):\n    return a * b\n";
        await File.WriteAllBytesAsync(calcPath, Encoding.UTF8.GetBytes(buggy));

        var outputPath = Path.Combine(workspace.Path, "output.txt");
        var config = new HeadlessRunConfig
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Agent = new HeadlessAgent
            {
                Slug = "swe-fixer",
                Instructions =
                    $"You are a coding agent operating in the repository at {repo}. "
                    + "Task: the function add(a, b) in calc.py is wrong — it returns a - b but "
                    + "should return a + b. Fix it using the available file tools (read the file, "
                    + "then edit it). Do not change any other behavior. When the fix is written to "
                    + "disk, reply with the single word DONE.",
            },
            Model = new HeadlessModel { Provider = "openrouter", Id = "xiaomi/mimo-v2.5" },
            Tools = Array.Empty<HeadlessTool>(),
            Workspace = new HeadlessWorkspace { Root = repo },
            Output = new HeadlessOutput { File = outputPath, Stream = "stdout" },
            // Mutating built-ins are fail-closed in headless; the run must opt them in.
            Permissions = new HeadlessPermissions
            {
                AllowedTools = ["read_file", "write_file", "replace_text", "list_directory", "search_text"],
            },
            Limits = new HeadlessLimits { MaxIterations = 25, TimeoutSeconds = 240 },
        };

        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());

        var code = await HeadlessAgentRunner.ExecuteAsync(
            config, stdout, stderr, NullLoggerFactory.Instance);

        Assert.True(
            code == HeadlessExitCode.Success,
            $"Expected Success, got {code}. Events:\n{stdout}\nStderr:\n{stderr}");

        // The agent must have written the fix to disk.
        var resultBytes = await File.ReadAllBytesAsync(calcPath);
        Assert.False(
            StartsWithBom(resultBytes),
            "The edited file must not start with a UTF-8 BOM (the bug under test).");

        var resultText = Encoding.UTF8.GetString(resultBytes);
        Assert.Contains("return a + b", resultText);
        Assert.DoesNotContain("return a - b", resultText);
        // The unrelated function must be untouched.
        Assert.Contains("def multiply(a, b):", resultText);
        Assert.Contains("return a * b", resultText);
    }

    private static bool StartsWithBom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"andy-swe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
