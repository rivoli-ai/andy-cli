using System;
using System.Collections.Generic;
using System.IO;
using Andy.Cli.Services.Formatting;
using Xunit;

namespace Andy.Cli.Tests.Services.Formatting;

public class FormatterAvailabilityTests
{
    private static string? Resolve(string? command, string? path, params string[] existing)
    {
        var files = new HashSet<string>(existing, StringComparer.Ordinal);
        return FormatterAvailability.Resolve(
            command,
            name => name == "PATH" ? path : null,
            files.Contains);
    }

    [Fact]
    public void ACommandOnPath_Resolves()
    {
        var pathValue = string.Join(Path.PathSeparator, "/opt/bin", "/usr/bin");
        var resolved = Resolve("gofmt", pathValue, "/usr/bin/gofmt");

        Assert.Equal(Path.Combine("/usr/bin", "gofmt"), resolved);
    }

    [Fact]
    public void TheFirstPathEntryWins()
    {
        var pathValue = string.Join(Path.PathSeparator, "/opt/bin", "/usr/bin");
        var resolved = Resolve("gofmt", pathValue, "/opt/bin/gofmt", "/usr/bin/gofmt");

        Assert.Equal(Path.Combine("/opt/bin", "gofmt"), resolved);
    }

    [Fact]
    public void ACommandThatIsNotInstalled_DoesNotResolve_AndNothingIsInstalledForIt()
    {
        Assert.Null(Resolve("nonexistent-formatter", "/usr/bin"));
    }

    [Fact]
    public void AnEmptyPath_ResolvesNothing()
    {
        Assert.Null(Resolve("gofmt", null, "/usr/bin/gofmt"));
    }

    [Fact]
    public void APathQualifiedCommand_IsProbedDirectlyAndNeverViaPath()
    {
        // Present at the given path: resolves.
        Assert.NotNull(Resolve("/tools/fmt", "/usr/bin", "/tools/fmt"));

        // Absent at the given path: NOT resolved from PATH, even though a same-named binary is there.
        Assert.Null(Resolve("/tools/fmt", "/usr/bin", "/usr/bin/fmt"));
    }

    [Fact]
    public void BlankCommands_ResolveToNothing()
    {
        Assert.Null(Resolve(null, "/usr/bin"));
        Assert.Null(Resolve("   ", "/usr/bin"));
    }

    [Fact]
    public void AMalformedPathEntry_DoesNotBreakResolution()
    {
        var pathValue = string.Join(Path.PathSeparator, "\0bad", "/usr/bin");
        Assert.Equal(Path.Combine("/usr/bin", "gofmt"), Resolve("gofmt", pathValue, "/usr/bin/gofmt"));
    }
}
