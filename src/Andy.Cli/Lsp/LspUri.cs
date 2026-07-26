using System;
using System.IO;

namespace Andy.Cli.Lsp;

/// <summary>
/// Conversions between filesystem paths and the <c>file://</c> URIs LSP speaks.
///
/// Servers key everything on the URI string they were given, so round-tripping has to be exact:
/// a path that opens as "file:///a/b.cs" must not come back as "file:///a/b%2Ecs" or the publish
/// notification will look like it is about a different document.
/// </summary>
public static class LspUri
{
    public static string FromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var full = Path.GetFullPath(path);
        return new Uri(full).AbsoluteUri;
    }

    public static string? ToPath(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        return parsed.LocalPath;
    }
}
