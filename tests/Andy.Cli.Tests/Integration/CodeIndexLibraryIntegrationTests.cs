using Andy.CodeIndex.Application.Interfaces;
using Andy.CodeIndex.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Confirms andy-cli integrates the published Andy.CodeIndex library: its
/// DB-free Roslyn analysis and chunking services run in-process and produce
/// meaningful results, both directly and when resolved through DI the way
/// Program.cs registers them. The Postgres-backed parts of the library are
/// out of scope for this in-process integration check.
/// </summary>
public class CodeIndexLibraryIntegrationTests
{
    private const string SampleSource = @"
namespace Sample;

public class Calculator
{
    public int Add(int a, int b) { return a + b; }
    public int Subtract(int a, int b) { return a - b; }
}

public interface IGreeter
{
    string Greet(string name);
}
";

    [Fact]
    public void CodeAnalysisService_AnalyzesCSharp_ExtractsTypesAndMethods()
    {
        ICodeAnalysisService analysis = new CodeAnalysisService();

        Assert.True(analysis.SupportsLanguage("csharp"));

        var result = analysis.Analyze(SampleSource, "Calculator.cs", "csharp");

        var calculator = Assert.Single(result.Classes, c => c.Name == "Calculator");
        Assert.Contains(calculator.Methods, m => m.Name == "Add");
        Assert.Contains(calculator.Methods, m => m.Name == "Subtract");
        Assert.Contains(result.Interfaces, i => i.Name == "IGreeter");
    }

    [Fact]
    public void ChunkingService_ChunksCSharp_ProducesNonEmptyChunks()
    {
        IChunkingService chunking = new ChunkingService();

        var chunks = chunking.ChunkText(SampleSource, "Calculator.cs");

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Content)));
        Assert.Contains(chunks, c => c.Content.Contains("Calculator"));
    }

    [Fact]
    public void DependencyInjection_ResolvesLibraryServices_AsRegisteredInProgram()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<ICodeAnalysisService, CodeAnalysisService>()
            .AddSingleton<IChunkingService, ChunkingService>()
            .BuildServiceProvider();

        var analysis = provider.GetRequiredService<ICodeAnalysisService>();
        var chunking = provider.GetRequiredService<IChunkingService>();

        Assert.True(analysis.SupportsLanguage("csharp"));
        // The chunker resolves and uses the DI-supplied analyzer.
        Assert.NotEmpty(chunking.ChunkText(SampleSource, "Calculator.cs"));
    }
}
