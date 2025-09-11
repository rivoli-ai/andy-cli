using System;
using System.Text.Json;
using Andy.Llm;
using Andy.Llm.Models;

class Program
{
    static void Main()
    {
        var conversation = new ConversationContext
        {
            SystemInstruction = "Test system prompt",
            MaxContextMessages = 20
        };
        
        // Add a user message
        conversation.AddUserMessage("Hello, world!");
        
        // Create request
        var request = conversation.CreateRequest();
        
        // Serialize to JSON to see what we get
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        
        var json = JsonSerializer.Serialize(request, options);
        Console.WriteLine(json);
    }
}
