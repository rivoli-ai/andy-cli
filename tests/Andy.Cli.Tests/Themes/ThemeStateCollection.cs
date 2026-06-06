using Xunit;

namespace Andy.Cli.Tests.Themes;

/// <summary>
/// Tests that read or mutate the process-wide <see cref="Andy.Cli.Themes.Theme.Current"/> singleton must not
/// run concurrently with one another: xUnit parallelizes distinct test classes, so two such classes racing on
/// the shared static caused intermittent failures (a render reading a theme another test had just reset).
/// Marking the affected classes with this collection forces them to run sequentially.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ThemeStateCollection
{
    public const string Name = "ThemeState";
}
