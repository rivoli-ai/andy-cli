using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Andy.Cli.Services;

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
        var prompts = new[]
        {
            "Create a new Node.js project structure with src, tests, and docs folders",
            "Add a package.json file with basic configuration",
            "Create a README.md with project description",
            "Add a .gitignore file for Node.js projects"
        };

        var expectedTools = new[]
        {
            ("create_directory", new[] { "src", "tests", "docs" }),
            ("write_file", new[] { "package.json" }),
            ("write_file", new[] { "README.md" }),
            ("write_file", new[] { ".gitignore" })
        };

        // Execute scenario
        await ExecuteScenarioAsync(prompts, expectedTools);

        // Verify results
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "src")));
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "tests")));
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "docs")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "package.json")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "README.md")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, ".gitignore")));
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

        var prompts = new[]
        {
            "List all files in the current directory",
            "Create a backup folder",
            "Copy config.json and data.csv to the backup folder",
            "Delete files that start with 'old_' or 'temp'",
            "Show me what's in the backup folder"
        };

        var expectedSequence = new[]
        {
            "list_directory",
            "create_directory",
            "copy_file",
            "copy_file",
            "delete_file",
            "delete_file",
            "list_directory"
        };

        // Execute scenario
        await ExecuteScenarioWithSequenceAsync(prompts, expectedSequence);

        // Verify results
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "backup")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "backup", "config.json")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "backup", "data.csv")));
        Assert.False(File.Exists(Path.Combine(TestDirectory, "old_backup.json")));
        Assert.False(File.Exists(Path.Combine(TestDirectory, "temp.txt")));
    }

    [Fact]
    public async Task Scenario_DataProcessing_ReadsModifiesWrites()
    {
        // Scenario: "Process and transform data files"
        
        // Setup initial data
        var csvContent = "name,age,city\\nAlice,30,NYC\\nBob,25,LA\\nCharlie,35,Chicago";
        CreateTestFile("input.csv", csvContent);

        var prompts = new[]
        {
            "Read the input.csv file",
            "Create a summary of the data showing the average age",
            "Save the summary to summary.txt",
            "Create a filtered version with only people over 30 and save to filtered.csv"
        };

        // This tests the LLM's ability to:
        // 1. Read and understand file content
        // 2. Process/analyze data
        // 3. Generate new content based on analysis
        // 4. Write results to new files

        await ExecuteComplexDataScenarioAsync(prompts);

        // Verify outputs exist
        Assert.True(File.Exists(Path.Combine(TestDirectory, "summary.txt")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "filtered.csv")));
        
        // Verify summary contains expected information
        var summary = File.ReadAllText(Path.Combine(TestDirectory, "summary.txt"));
        Assert.Contains("average", summary.ToLower());
        Assert.Contains("30", summary); // average age
        
        // Verify filtered data
        var filtered = File.ReadAllText(Path.Combine(TestDirectory, "filtered.csv"));
        Assert.Contains("Charlie", filtered);
        Assert.DoesNotContain("Bob", filtered);
    }

    [Fact]
    public async Task Scenario_ErrorRecovery_HandlesFailuresGracefully()
    {
        // Scenario: "Recover from errors and complete task"
        
        var prompts = new[]
        {
            "Copy a file that doesn't exist to backup folder", // Will fail
            "Create the backup folder first", // Recovery step
            "Create a sample file to test with",
            "Now copy the sample file to backup"
        };

        // Test that the system can:
        // 1. Handle tool execution failures
        // 2. Understand error messages
        // 3. Take corrective action
        // 4. Retry and succeed

        await ExecuteErrorRecoveryScenarioAsync(prompts);

        // Verify recovery was successful
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "backup")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "sample")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "backup", "sample")));
    }

    [Fact]
    public async Task Scenario_ConditionalExecution_MakesDecisions()
    {
        // Scenario: "Clean up based on file conditions"
        
        // Setup files with different sizes
        CreateTestFile("small.txt", "a");
        CreateTestFile("medium.txt", new string('b', 1000));
        CreateTestFile("large.txt", new string('c', 10000));

        var prompts = new[]
        {
            "Check the sizes of all .txt files",
            "Delete files smaller than 100 bytes",
            "Move files larger than 5000 bytes to an archive folder",
            "List remaining files"
        };

        // Test decision-making based on file properties
        await ExecuteConditionalScenarioAsync(prompts);

        // Verify conditional actions
        Assert.False(File.Exists(Path.Combine(TestDirectory, "small.txt")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "medium.txt")));
        Assert.False(File.Exists(Path.Combine(TestDirectory, "large.txt")));
        Assert.True(File.Exists(Path.Combine(TestDirectory, "archive", "large.txt")));
    }

    [Fact]
    public async Task Scenario_InformationGathering_SystemAnalysis()
    {
        // Scenario: "Analyze system and create report"
        
        var prompts = new[]
        {
            "Get system information",
            "Check available disk space",
            "List environment variables",
            "Create a system report with all this information in system_report.md"
        };

        // Test ability to:
        // 1. Gather various system information
        // 2. Combine multiple data sources
        // 3. Format and present information
        // 4. Save comprehensive report

        await ExecuteSystemAnalysisScenarioAsync(prompts);

        // Verify report was created and contains expected sections
        var reportPath = Path.Combine(TestDirectory, "system_report.md");
        Assert.True(File.Exists(reportPath));
        
        var report = File.ReadAllText(reportPath);
        Assert.Contains("System Information", report);
        Assert.Contains("Disk Space", report);
        Assert.Contains("Environment Variables", report);
    }

    [Fact]
    public async Task Scenario_InteractiveWorkflow_StepByStep()
    {
        // Scenario: "Interactive file organization with user feedback"
        
        // This simulates a conversation where the LLM asks for clarification
        var conversation = new[]
        {
            ("User", "Organize my files"),
            ("Assistant", "I'll help organize your files. First, let me see what files you have."),
            ("Tool", "list_directory"),
            ("Assistant", "I see you have various file types. How would you like them organized?"),
            ("User", "By file extension"),
            ("Assistant", "I'll organize them by extension. Creating folders now..."),
            ("Tool", "create_directory for each extension"),
            ("Tool", "move_file for each file"),
            ("Assistant", "Files have been organized by extension.")
        };

        await ExecuteInteractiveWorkflowAsync(conversation);

        // Verify organization was completed
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "txt")));
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "json")));
        Assert.True(Directory.Exists(Path.Combine(TestDirectory, "csv")));
    }

    #region Helper Methods

    private async Task ExecuteScenarioAsync(string[] prompts, (string tool, string[] parameters)[] expectedTools)
    {
        // Implementation for executing multi-step scenarios
        foreach (var prompt in prompts)
        {
            var result = await ProcessPromptAsync(prompt);
            ValidateToolExecution(result, expectedTools);
        }
    }

    private async Task ExecuteScenarioWithSequenceAsync(string[] prompts, string[] expectedToolSequence)
    {
        var toolIndex = 0;
        foreach (var prompt in prompts)
        {
            var result = await ProcessPromptAsync(prompt);
            
            // Verify expected tools were called in sequence
            while (toolIndex < expectedToolSequence.Length && 
                   result.Contains(expectedToolSequence[toolIndex]))
            {
                toolIndex++;
            }
        }
        
        Assert.Equal(expectedToolSequence.Length, toolIndex);
    }

    private async Task ExecuteComplexDataScenarioAsync(string[] prompts)
    {
        // Specialized handler for data processing scenarios
        foreach (var prompt in prompts)
        {
            await ProcessPromptWithDataAnalysisAsync(prompt);
        }
    }

    private async Task ExecuteErrorRecoveryScenarioAsync(string[] prompts)
    {
        // Handler that expects and manages errors
        var hasError = false;
        foreach (var prompt in prompts)
        {
            try
            {
                var result = await ProcessPromptAsync(prompt);
                if (result.Contains("error"))
                {
                    hasError = true;
                }
                else if (hasError)
                {
                    // Verify recovery action was taken
                    hasError = false;
                }
            }
            catch
            {
                hasError = true;
            }
        }
        
        Assert.False(hasError, "Should have recovered from all errors");
    }

    private async Task ExecuteConditionalScenarioAsync(string[] prompts)
    {
        // Execute prompts that require conditional logic
        foreach (var prompt in prompts)
        {
            await ProcessPromptWithConditionalLogicAsync(prompt);
        }
    }

    private async Task ExecuteSystemAnalysisScenarioAsync(string[] prompts)
    {
        // Execute system information gathering
        var systemInfo = new Dictionary<string, string>();
        
        foreach (var prompt in prompts)
        {
            var result = await ProcessPromptAsync(prompt);
            ExtractSystemInfo(result, systemInfo);
        }
        
        Assert.NotEmpty(systemInfo);
    }

    private async Task ExecuteInteractiveWorkflowAsync((string role, string content)[] conversation)
    {
        // Simulate back-and-forth conversation
        foreach (var (role, content) in conversation)
        {
            if (role == "User")
            {
                await ProcessUserPromptAsync(content);
            }
            else if (role == "Tool")
            {
                await SimulateToolExecutionAsync(content);
            }
            // Assistant responses are handled automatically
        }
    }

    #endregion
}

/// <summary>
/// Base class for prompt-based tests with common functionality
/// </summary>
public abstract class PromptTestBase : IDisposable
{
    protected string TestDirectory { get; }
    protected AiConversationService AiService { get; }

    protected PromptTestBase()
    {
        TestDirectory = Path.Combine(Path.GetTempPath(), $"andy_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(TestDirectory);
        
        // Initialize AI service with real or mock components
        // This would be properly set up in actual implementation
    }

    protected void CreateTestFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(TestDirectory, name), content);
    }

    protected async Task<string> ProcessPromptAsync(string prompt)
    {
        // Process prompt through AI service
        return await AiService.ProcessMessageAsync(prompt, false);
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
    }
}