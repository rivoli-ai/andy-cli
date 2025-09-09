using System;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Parsers;
using Andy.Cli.Services;
using Microsoft.Extensions.Logging;

namespace ToolParsingTest
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Testing tool call parsing...");
            
            // Create a mock JSON repair service
            var mockJsonRepair = new MockJsonRepairService();
            var parser = new QwenParser(mockJsonRepair);
            
            // Test cases from system prompt
            string[] testInputs = {
                // Format A - tool_call in XML tags
                "<tool_call>\n{\"name\":\"read_file\",\"arguments\":{\"file_path\":\"/test/file.txt\"}}\n</tool_call>",
                
                // Format B - direct tool JSON
                "{\"tool\":\"list_directory\",\"parameters\":{\"path\":\"/test\"}}",
                
                // Mixed content with tool call
                "I need to read a file. <tool_call>\n{\"name\":\"read_file\",\"arguments\":{\"file_path\":\"/test/file.txt\"}}\n</tool_call>",
                
                // No tool call - should not trigger
                "Hello, this is just a regular response with no tools."
            };
            
            foreach (var input in testInputs)
            {
                Console.WriteLine($"\n--- Testing Input ---");
                Console.WriteLine(input);
                
                var result = parser.Parse(input, null);
                var toolCalls = result.Children.OfType<ToolCallNode>().ToList();
                
                Console.WriteLine($"Tool calls detected: {toolCalls.Count}");
                foreach (var toolCall in toolCalls)
                {
                    Console.WriteLine($"  - {toolCall.ToolName}: {System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments)}");
                }
            }
        }
    }
    
    public class MockJsonRepairService : IJsonRepairService
    {
        public T SafeParse<T>(string json) where T : class
        {
            try 
            {
                return System.Text.Json.JsonSerializer.Deserialize<T>(json);
            }
            catch 
            {
                return null;
            }
        }
        
        public string RepairJson(string input) => input;
    }
}