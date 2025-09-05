using System.Text.Json;
using Andy.Llm.Models;

namespace Andy.Cli.Tests.TestData;

/// <summary>
/// Helper class for working with test LLM responses
/// </summary>
public static class TestResponseHelper
{
    /// <summary>
    /// Creates an LlmResponse from a sample response string
    /// </summary>
    public static LlmResponse CreateResponse(string sampleResponse)
    {
        return new LlmResponse
        {
            Content = sampleResponse,
            Model = "test-model",
            FinishReason = "stop"
        };
    }

    /// <summary>
    /// Creates a sequence of LlmResponses from an array of sample responses
    /// </summary>
    public static LlmResponse[] CreateResponseSequence(params string[] sampleResponses)
    {
        return sampleResponses.Select(r => CreateResponse(r)).ToArray();
    }

    /// <summary>
    /// Extracts tool call JSON from a response that contains [Tool Request] marker
    /// </summary>
    public static string? ExtractToolCallJson(string response)
    {
        const string marker = "[Tool Request]";
        var markerIndex = response.IndexOf(marker);
        
        if (markerIndex == -1)
            return null;
            
        var jsonStart = markerIndex + marker.Length;
        var jsonContent = response.Substring(jsonStart).Trim();
        
        // Find the JSON object (starts with { and ends with })
        var braceCount = 0;
        var startIndex = jsonContent.IndexOf('{');
        if (startIndex == -1)
            return null;
            
        var endIndex = -1;
        for (int i = startIndex; i < jsonContent.Length; i++)
        {
            if (jsonContent[i] == '{')
                braceCount++;
            else if (jsonContent[i] == '}')
            {
                braceCount--;
                if (braceCount == 0)
                {
                    endIndex = i;
                    break;
                }
            }
        }
        
        if (endIndex == -1)
            return null;
            
        return jsonContent.Substring(startIndex, endIndex - startIndex + 1);
    }

    /// <summary>
    /// Validates that a tool call JSON is well-formed
    /// </summary>
    public static bool IsValidToolCall(string toolCallJson)
    {
        try
        {
            var json = JsonDocument.Parse(toolCallJson);
            return json.RootElement.TryGetProperty("tool", out _) &&
                   json.RootElement.TryGetProperty("parameters", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates streaming response chunks from a string
    /// </summary>
    public static List<LlmStreamResponse> CreateStreamingChunks(string response, int chunkSize = 10)
    {
        var chunks = new List<LlmStreamResponse>();
        
        for (int i = 0; i < response.Length; i += chunkSize)
        {
            var chunk = response.Substring(i, Math.Min(chunkSize, response.Length - i));
            chunks.Add(new LlmStreamResponse
            {
                TextDelta = chunk,
                IsComplete = i + chunkSize >= response.Length
            });
        }
        
        return chunks;
    }

    /// <summary>
    /// Common test scenarios for easy reuse
    /// </summary>
    public static class Scenarios
    {
        public static LlmResponse SimpleListFiles()
            => CreateResponse(SampleLlmResponses.SingleToolCalls.ListDirectory);

        public static LlmResponse SimpleReadFile()
            => CreateResponse(SampleLlmResponses.SingleToolCalls.ReadFile);

        public static LlmResponse SimpleWriteFile()
            => CreateResponse(SampleLlmResponses.SingleToolCalls.WriteFile);

        public static LlmResponse SimpleCreateDirectory()
            => CreateResponse(SampleLlmResponses.SingleToolCalls.CreateDirectory);

        public static LlmResponse[] ProjectSetupSequence()
            => CreateResponseSequence(SampleLlmResponses.MultiStepSequences.CreateProjectStructure);

        public static LlmResponse[] BackupSequence()
            => CreateResponseSequence(SampleLlmResponses.MultiStepSequences.BackupAndCleanup);

        public static LlmResponse[] FileOrganizationSequence()
            => CreateResponseSequence(SampleLlmResponses.MultiStepSequences.FileOrganization);

        public static LlmResponse NonToolGreeting()
            => CreateResponse(SampleLlmResponses.NonToolResponses.Greeting);

        public static LlmResponse AskingForClarification()
            => CreateResponse(SampleLlmResponses.NonToolResponses.AskingForClarification);
        
        public static LlmResponse NonToolClarification()
            => CreateResponse(SampleLlmResponses.NonToolResponses.Clarification);
    }
}