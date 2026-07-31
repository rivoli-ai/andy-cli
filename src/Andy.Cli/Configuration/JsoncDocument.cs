using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Andy.Cli.Configuration;

/// <summary>
/// A parsed andy.jsonc document plus a map from dotted key path to the exact
/// (line, column) the value starts at.
///
/// System.Text.Json can parse JSONC (comments + trailing commas) but throws the
/// position information away, so every diagnostic would degrade to "somewhere in
/// this file". We therefore make a second pass with <see cref="Utf8JsonReader"/>,
/// which does expose <c>TokenStartIndex</c>, and record a position for every
/// property and array element. Both passes use identical reader options, so the
/// two views of the document cannot disagree.
///
/// Paths use the same dotted/indexed form the user sees in diagnostics and in
/// <c>config show</c> ("mcp.servers.filesystem.args[0]"), so a path can be echoed
/// straight back without translation.
/// </summary>
public sealed class JsoncDocument
{
    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    private static readonly JsonReaderOptions s_readerOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    private readonly Dictionary<string, (int Line, int Column)> _valuePositions;
    private readonly Dictionary<string, (int Line, int Column)> _keyPositions;

    private JsoncDocument(
        JsonObject root,
        Dictionary<string, (int, int)> valuePositions,
        Dictionary<string, (int, int)> keyPositions)
    {
        Root = root;
        _valuePositions = valuePositions;
        _keyPositions = keyPositions;
    }

    /// <summary>The document body. Always an object; a non-object root is a parse error.</summary>
    public JsonObject Root { get; }

    /// <summary>
    /// Parses JSONC text. Throws <see cref="JsoncParseException"/> with a 1-based
    /// line and column for malformed input or a non-object root.
    /// </summary>
    public static JsoncDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, nodeOptions: null, documentOptions: s_documentOptions);
        }
        catch (JsonException ex)
        {
            throw new JsoncParseException(
                CleanJsonMessage(ex.Message),
                (int)(ex.LineNumber ?? 0) + 1,
                (int)(ex.BytePositionInLine ?? 0) + 1);
        }

        if (node is null)
        {
            throw new JsoncParseException("The file is empty or contains only 'null'.", 1, 1);
        }

        if (node is not JsonObject root)
        {
            throw new JsoncParseException(
                $"The top level of an Andy config file must be a JSON object, but this file starts with a {DescribeKind(node)}.",
                1,
                1);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var lineIndex = BuildLineIndex(bytes);
        var valuePositions = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var keyPositions = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        RecordPositions(bytes, lineIndex, valuePositions, keyPositions);

        return new JsoncDocument(root, valuePositions, keyPositions);
    }

    /// <summary>Position of the value at <paramref name="keyPath"/>, or null when unknown.</summary>
    public (int Line, int Column)? ValuePosition(string keyPath) =>
        _valuePositions.TryGetValue(keyPath, out var position) ? position : null;

    /// <summary>
    /// Position of the property NAME at <paramref name="keyPath"/>. Unknown-key
    /// diagnostics point here so the caret lands on the offending key, not its value.
    /// </summary>
    public (int Line, int Column)? KeyPosition(string keyPath) =>
        _keyPositions.TryGetValue(keyPath, out var position) ? position : null;

    /// <summary>Key position when known, otherwise the value position, otherwise (0, 0).</summary>
    public (int Line, int Column) BestPosition(string keyPath) =>
        KeyPosition(keyPath) ?? ValuePosition(keyPath) ?? (0, 0);

    private static void RecordPositions(
        byte[] bytes,
        List<int> lineIndex,
        Dictionary<string, (int, int)> valuePositions,
        Dictionary<string, (int, int)> keyPositions)
    {
        var reader = new Utf8JsonReader(bytes, s_readerOptions);

        // One frame per open container. For objects, PendingName holds the property
        // name whose value comes next; for arrays, NextIndex is the element counter.
        var stack = new List<Frame>();
        string currentPath = string.Empty;

        string PathForNextValue()
        {
            if (stack.Count == 0)
            {
                return string.Empty;
            }
            var frame = stack[^1];
            return frame.IsArray
                ? $"{frame.Path}[{frame.NextIndex}]"
                : frame.PendingName is null ? frame.Path : Join(frame.Path, frame.PendingName);
        }

        void AfterValue()
        {
            if (stack.Count == 0)
            {
                return;
            }
            var frame = stack[^1];
            if (frame.IsArray)
            {
                frame.NextIndex++;
            }
            else
            {
                frame.PendingName = null;
            }
        }

        while (reader.Read())
        {
            var start = (int)reader.TokenStartIndex;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    {
                        var name = reader.GetString() ?? string.Empty;
                        var frame = stack.Count > 0 ? stack[^1] : null;
                        var parentPath = frame?.Path ?? string.Empty;
                        var path = Join(parentPath, name);
                        keyPositions[path] = Position(lineIndex, bytes, start);
                        if (frame is not null)
                        {
                            frame.PendingName = name;
                        }
                        break;
                    }

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    {
                        currentPath = PathForNextValue();
                        valuePositions[currentPath] = Position(lineIndex, bytes, start);
                        stack.Add(new Frame
                        {
                            Path = currentPath,
                            IsArray = reader.TokenType == JsonTokenType.StartArray,
                        });
                        break;
                    }

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    {
                        if (stack.Count > 0)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }
                        AfterValue();
                        break;
                    }

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    {
                        valuePositions[PathForNextValue()] = Position(lineIndex, bytes, start);
                        AfterValue();
                        break;
                    }
            }
        }
    }

    private sealed class Frame
    {
        public string Path = string.Empty;
        public bool IsArray;
        public string? PendingName;
        public int NextIndex;
    }

    /// <summary>Appends a property name to a dotted path.</summary>
    public static string Join(string parentPath, string name) =>
        parentPath.Length == 0 ? name : $"{parentPath}.{name}";

    private static List<int> BuildLineIndex(byte[] bytes)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    private static (int Line, int Column) Position(List<int> lineStarts, byte[] bytes, int offset)
    {
        // Binary search for the last line start at or before the offset.
        var low = 0;
        var high = lineStarts.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (lineStarts[mid] <= offset)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        var lineStart = lineStarts[low];
        // Column is counted in characters, not bytes, so a comment containing
        // non-ASCII text above the key does not shift the caret.
        var columnChars = offset > lineStart
            ? Encoding.UTF8.GetCharCount(bytes, lineStart, offset - lineStart)
            : 0;
        return (low + 1, columnChars + 1);
    }

    private static string DescribeKind(JsonNode node) => node switch
    {
        JsonArray => "JSON array",
        JsonValue => "JSON scalar value",
        _ => "non-object value",
    };

    private static string CleanJsonMessage(string message)
    {
        // System.Text.Json appends " Path: $.x | LineNumber: 3 | BytePositionInLine: 5."
        // We report those separately, so trim the duplicate tail.
        var marker = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return marker < 0 ? message : message[..marker].TrimEnd();
    }
}

/// <summary>Thrown when an andy.jsonc file cannot be parsed. Carries a 1-based line and column.</summary>
public sealed class JsoncParseException : Exception
{
    public JsoncParseException(string message, int line, int column)
        : base(message)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }
}
