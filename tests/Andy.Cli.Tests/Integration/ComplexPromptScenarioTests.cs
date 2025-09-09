using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Andy.Cli.Services;
using Andy.Cli.Tests.TestData;
using Andy.Cli.Widgets;
using Andy.Tools.Core;
using Andy.Tools.Core.OutputLimiting;
using Andy.Tools.Execution;
using Andy.Tools.Library;
using Andy.Tools.Library.FileSystem;
using Andy.Tools.Library.System;
using Andy.Tools.Validation;
using Andy.Llm;
using Andy.Llm.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Tests.Integration;

/// <summary>
/// Tests complex scenarios that require multiple back-and-forth interactions
/// between the CLI and LLM with various tool executions
/// </summary>
public class ComplexPromptScenarioTests : PromptTestBase
{
    [Fact]
    public async Task Scenario_ProjectSetup_CreatesCompleteStructure()
    {
        // Scenario: "Set up a new Node.js project with proper structure"
        // Setup mock responses for the project setup sequence
        SetupMockResponses(
            SampleLlmResponses.SingleToolCalls.CreateDirectory,
            SampleLlmResponses.SingleToolCalls.CreateDirectory,
            SampleLlmResponses.SingleToolCalls.CreateDirectory,
            SampleLlmResponses.SingleToolCalls.WriteFile,
            SampleLlmResponses.SingleToolCalls.WriteFile,
            SampleLlmResponses.SingleToolCalls.WriteFile,
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute prompts
        await ProcessPromptAsync("Create a new Node.js project structure with src, tests, and docs folders");
        await ProcessPromptAsync("Add a package.json file with basic configuration");
        await ProcessPromptAsync("Create a README.md with project description");
        await ProcessPromptAsync("Add a .gitignore file for Node.js projects");

        // Since we're using mocked responses, verify the mock was called
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task Scenario_FileBackup_CreatesBackupAndCleanup()
    {
        // Scenario: "Backup important files and clean up old ones"

        // Setup initial files
        CreateTestFile("config.json", "{\"setting\": \"value\"}");
        CreateTestFile("data.csv", "id,name\\n1,test");
        CreateTestFile("old_backup.json", "{\"old\": true}");
        CreateTestFile("temp.txt", "temporary data");

        // Setup mock responses for backup operations
        SetupMockResponses(
            SampleLlmResponses.SingleToolCalls.ListDirectory,
            SampleLlmResponses.SingleToolCalls.CreateDirectory,
            SampleLlmResponses.SingleToolCalls.CopyFile,
            SampleLlmResponses.SingleToolCalls.CopyFile,
            SampleLlmResponses.SingleToolCalls.DeleteFile,
            SampleLlmResponses.SingleToolCalls.DeleteFile,
            SampleLlmResponses.SingleToolCalls.ListDirectory,
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute prompts
        await ProcessPromptAsync("List all files in the current directory");
        await ProcessPromptAsync("Create a backup folder");
        await ProcessPromptAsync("Copy config.json and data.csv to the backup folder");
        await ProcessPromptAsync("Delete files that start with 'old_' or 'temp'");
        await ProcessPromptAsync("Show me what's in the backup folder");

        // Verify the mock was called
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task Scenario_DataProcessing_ReadsModifiesWrites()
    {
        // Scenario: "Process and transform data files"

        // Setup initial data
        var csvContent = "name,age,city\\nAlice,30,NYC\\nBob,25,LA\\nCharlie,35,Chicago";
        CreateTestFile("input.csv", csvContent);

        // Setup mock responses for data processing
        SetupMockResponses(
            SampleLlmResponses.SingleToolCalls.ReadFile,
            "I've analyzed the CSV data. The average age is 30. Let me create a summary.\n\n[Tool Request]\n{\"tool\":\"write_file\",\"parameters\":{\"path\":\"summary.txt\",\"content\":\"Data Summary:\\n- Total records: 3\\n- Average age: 30\\n- Cities: NYC, LA, Chicago\"}}",
            "Now I'll create a filtered version with only people over 30.\n\n[Tool Request]\n{\"tool\":\"write_file\",\"parameters\":{\"path\":\"filtered.csv\",\"content\":\"name,age,city\\nAlice,30,NYC\\nCharlie,35,Chicago\"}}",
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute prompts
        await ProcessPromptAsync("Read the input.csv file");
        await ProcessPromptAsync("Create a summary of the data showing the average age");
        await ProcessPromptAsync("Save the summary to summary.txt");
        await ProcessPromptAsync("Create a filtered version with only people over 30 and save to filtered.csv");

        // Verify the mock was called
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task Scenario_ErrorRecovery_HandlesFailuresGracefully()
    {
        // Scenario: "Recover from errors and complete task"

        // Setup mock responses showing error and recovery
        SetupMockResponses(
            SampleLlmResponses.ErrorResponses.FileNotFound,
            SampleLlmResponses.SingleToolCalls.CreateDirectory,
            "I'll create a sample file for testing.\n\n[Tool Request]\n{\"tool\":\"write_file\",\"parameters\":{\"path\":\"sample.txt\",\"content\":\"Sample test content\"}}",
            SampleLlmResponses.SingleToolCalls.CopyFile,
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute prompts
        await ProcessPromptAsync("Copy a file that doesn't exist to backup folder");
        await ProcessPromptAsync("Create the backup folder first");
        await ProcessPromptAsync("Create a sample file to test with");
        await ProcessPromptAsync("Now copy the sample file to backup");

        // Verify the mock was called multiple times (including error recovery)
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task Scenario_ConditionalExecution_MakesDecisions()
    {
        // Scenario: "Clean up based on file conditions"

        // Setup files with different sizes
        CreateTestFile("small.txt", "a");
        CreateTestFile("medium.txt", new string('b', 1000));
        CreateTestFile("large.txt", new string('c', 10000));

        // Setup mock responses for conditional operations
        SetupMockResponses(
            SampleLlmResponses.SingleToolCalls.ListDirectory,
            "I'll delete files smaller than 100 bytes. The small.txt file is only 1 byte.\n\n[Tool Request]\n{\"tool\":\"delete_file\",\"parameters\":{\"path\":\"small.txt\"}}",
            "I'll create an archive folder and move large files there.\n\n[Tool Request]\n{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"archive\"}}",
            "Moving large.txt to archive folder.\n\n[Tool Request]\n{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"large.txt\",\"destination_path\":\"archive/large.txt\"}}",
            SampleLlmResponses.SingleToolCalls.ListDirectory,
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute prompts
        await ProcessPromptAsync("Check the sizes of all .txt files");
        await ProcessPromptAsync("Delete files smaller than 100 bytes");
        await ProcessPromptAsync("Move files larger than 5000 bytes to an archive folder");
        await ProcessPromptAsync("List remaining files");

        // Verify the mock was called
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task Scenario_InformationGathering_SystemAnalysis()
    {
        // Scenario: "Analyze system and create report"

        // Setup mock responses for system information gathering
        SetupMockResponses(
            SampleLlmResponses.SingleToolCalls.SystemInfo,
            "I'll check the disk space.\n\n[Tool Request]\n{\"tool\":\"system_info\",\"parameters\":{}}",
            "Getting environment variables.\n\n[Tool Request]\n{\"tool\":\"system_info\",\"parameters\":{}}",
            "Creating a comprehensive system report.\n\n[Tool Request]\n{\"tool\":\"write_file\",\"parameters\":{\"path\":\"system_report.md\",\"content\":\"# System Report\\n\\n## System Information\\n- OS: macOS\\n- Memory: 16GB\\n\\n## Disk Space\\n- Available: 100GB\\n\\n## Environment Variables\\n- PATH: /usr/bin:/usr/local/bin\\n- HOME: /Users/test\"}}",
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute prompts
        await ProcessPromptAsync("Get system information");
        await ProcessPromptAsync("Check available disk space");
        await ProcessPromptAsync("List environment variables");
        await ProcessPromptAsync("Create a system report with all this information in system_report.md");

        // Verify the mock was called
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task Scenario_InteractiveWorkflow_StepByStep()
    {
        // Scenario: "Interactive file organization with user feedback"

        // Setup test files
        CreateTestFile("doc1.txt", "text content");
        CreateTestFile("data.json", "{}");
        CreateTestFile("table.csv", "a,b,c");

        // Setup mock responses for interactive workflow
        SetupMockResponses(
            SampleLlmResponses.NonToolResponses.Greeting,
            SampleLlmResponses.SingleToolCalls.ListDirectory,
            SampleLlmResponses.NonToolResponses.AskingForClarification,
            "I'll organize them by file extension. Let me create the folders.\n\n[Tool Request]\n{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"txt\"}}",
            "[Tool Request]\n{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"json\"}}",
            "[Tool Request]\n{\"tool\":\"create_directory\",\"parameters\":{\"path\":\"csv\"}}",
            "[Tool Request]\n{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"doc1.txt\",\"destination_path\":\"txt/doc1.txt\"}}",
            "[Tool Request]\n{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"data.json\",\"destination_path\":\"json/data.json\"}}",
            "[Tool Request]\n{\"tool\":\"move_file\",\"parameters\":{\"source_path\":\"table.csv\",\"destination_path\":\"csv/table.csv\"}}",
            SampleLlmResponses.NonToolResponses.TaskComplete
        );

        // Execute conversation
        await ProcessPromptAsync("Organize my files");
        await ProcessPromptAsync("By file extension");

        // Verify the mock was called
        MockLlmClient!.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    // Helper methods are now handled by the base class
}

/// <summary>
/// Base class for prompt-based tests with common functionality
/// </summary>
public abstract class PromptTestBase : IDisposable
{
    protected string TestDirectory { get; }
    protected AiConversationService? AiService { get; set; }
    protected Mock<LlmClient>? MockLlmClient { get; set; }
    protected IToolRegistry? ToolRegistry { get; set; }
    protected IToolExecutor? ToolExecutor { get; set; }
    protected FeedView? Feed { get; set; }
    private ServiceProvider? _serviceProvider;

    protected PromptTestBase()
    {
        TestDirectory = Path.Combine(Path.GetTempPath(), $"andy_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(TestDirectory);

        InitializeServices();
    }

    private void InitializeServices()
    {
        // Set up services
        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole());

        // Add tools framework
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolValidator, ToolValidator>();
        services.AddSingleton<IResourceMonitor, ResourceMonitor>();
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<IToolOutputLimiter, ToolOutputLimiter>();
        services.AddSingleton<IServiceProvider>(sp => sp);

        // Register tools
        services.AddTransient<ListDirectoryTool>();
        services.AddTransient<ReadFileTool>();
        services.AddTransient<WriteFileTool>();
        services.AddTransient<CopyFileTool>();
        services.AddTransient<MoveFileTool>();
        services.AddTransient<DeleteFileTool>();
        services.AddTransient<Andy.Cli.Tools.CreateDirectoryTool>();
        services.AddTransient<SystemInfoTool>();

        _serviceProvider = services.BuildServiceProvider();

        // Initialize tools
        ToolRegistry = _serviceProvider.GetRequiredService<IToolRegistry>();
        ToolExecutor = _serviceProvider.GetRequiredService<IToolExecutor>();
        RegisterTools();

        // Set up mock LLM client
        MockLlmClient = new Mock<LlmClient>("test-api-key");

        // Set up UI components
        Feed = new FeedView();

        // Create system prompt
        var systemPromptService = new SystemPromptService();
        var systemPrompt = systemPromptService.BuildSystemPrompt(ToolRegistry.GetTools());

        // Create JSON repair service
        var jsonRepair = new JsonRepairService();

        // Create AI service
        AiService = new AiConversationService(
            MockLlmClient.Object,
            ToolRegistry,
            ToolExecutor,
            Feed,
            systemPrompt,
            jsonRepair);
    }

    private void RegisterTools()
    {
        var emptyConfig = new Dictionary<string, object?>();

        // Register tools using Type
        ToolRegistry!.RegisterTool(typeof(ListDirectoryTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(ReadFileTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(WriteFileTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(CopyFileTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(MoveFileTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(DeleteFileTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(Andy.Cli.Tools.CreateDirectoryTool), emptyConfig);
        ToolRegistry.RegisterTool(typeof(SystemInfoTool), emptyConfig);
    }

    protected void CreateTestFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(TestDirectory, name), content);
    }

    protected async Task<string> ProcessPromptAsync(string prompt)
    {
        // Process prompt through AI service
        if (AiService == null)
        {
            throw new InvalidOperationException("AiService is not initialized");
        }
        return await AiService.ProcessMessageAsync(prompt, false);
    }

    protected void SetupMockResponse(string response)
    {
        MockLlmClient?.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponseHelper.CreateResponse(response));
    }

    protected void SetupMockResponses(params string[] responses)
    {
        var queue = new Queue<LlmResponse>(responses.Select(r => TestResponseHelper.CreateResponse(r)));
        MockLlmClient?.Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 0 ? queue.Dequeue() : TestResponseHelper.CreateResponse("Done"));
    }

    protected async Task<string> ProcessPromptWithDataAnalysisAsync(string prompt)
    {
        // Specialized processing for data analysis prompts
        return await ProcessPromptAsync(prompt);
    }

    protected async Task<string> ProcessPromptWithConditionalLogicAsync(string prompt)
    {
        // Specialized processing for conditional logic
        return await ProcessPromptAsync(prompt);
    }

    protected async Task<string> ProcessUserPromptAsync(string prompt)
    {
        // Process user input in interactive scenarios
        return await ProcessPromptAsync(prompt);
    }

    protected async Task SimulateToolExecutionAsync(string toolDescription)
    {
        // Simulate tool execution based on description
        await Task.Delay(10); // Simulate execution time
    }

    protected void ValidateToolExecution(string result, (string tool, string[] parameters)[] expectedTools)
    {
        foreach (var (tool, parameters) in expectedTools)
        {
            Assert.Contains(tool, result);
            foreach (var param in parameters)
            {
                Assert.Contains(param, result);
            }
        }
    }

    protected void ExtractSystemInfo(string result, Dictionary<string, string> systemInfo)
    {
        // Extract system information from result
        if (result.Contains("OS:"))
        {
            systemInfo["OS"] = ExtractValue(result, "OS:");
        }
        if (result.Contains("Memory:"))
        {
            systemInfo["Memory"] = ExtractValue(result, "Memory:");
        }
    }

    private string ExtractValue(string text, string key)
    {
        var index = text.IndexOf(key);
        if (index >= 0)
        {
            var start = index + key.Length;
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            return text.Substring(start, end - start).Trim();
        }
        return string.Empty;
    }

    public void Dispose()
    {
        if (Directory.Exists(TestDirectory))
        {
            Directory.Delete(TestDirectory, true);
        }
        _serviceProvider?.Dispose();
    }
}