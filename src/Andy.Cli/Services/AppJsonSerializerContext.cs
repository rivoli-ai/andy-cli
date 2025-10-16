using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.Model.Model;

namespace Andy.Cli.Services;

/// <summary>
/// JSON serializer context for trim-safe serialization.
/// This uses source generation instead of reflection, which is compatible with trimming.
/// </summary>
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(List<Message>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(object))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
