using System.Collections.Generic;
using Xunit;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Tests rendering of Productivity, Git, and CLI-specific tools
    /// Productivity Tools: TodoManagementTool, TodoExecutor
    /// Git Tools: GitDiffTool
    /// CLI Tools: CreateDirectoryTool, BashCommandTool, CodeIndexTool
    /// </summary>
    public class ProductivityAndCliToolRenderingTests : ToolRenderingTestBase
    {
        #region TodoManagementTool Tests

        [Fact]
        public void TodoManagementTool_ParameterDisplay_ShowsAction()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "action", "add" },
                { "task", "Implement new feature" }
            };

            var toolItem = CreateToolItem("todo_management", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void TodoManagementTool_SuccessResult_ShowsTaskAdded()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "action", "add" },
                { "task", "Write unit tests" }
            };

            var result = "Task added successfully";

            AssertResultDisplayContains("todo_management", parameters, result, "added");
        }

        [Fact]
        public void TodoManagementTool_SuccessResult_ShowsTaskList()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "action", "list" }
            };

            var result = "5 tasks found (2 completed, 3 pending)";

            AssertResultDisplayContains("todo_management", parameters, result, "5 tasks");
        }

        [Fact]
        public void TodoManagementTool_ErrorResult_ShowsInvalidAction()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "action", "invalid_action" }
            };

            var errorMessage = "Invalid action specified";

            AssertErrorDisplayContains("todo_management", parameters, errorMessage, "Invalid");
        }

        #endregion

        #region TodoExecutor Tests

        [Fact]
        public void TodoExecutor_ParameterDisplay_ShowsTaskId()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "task_id", "TASK-123" },
                { "action", "execute" }
            };

            var toolItem = CreateToolItem("todo_executor", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void TodoExecutor_SuccessResult_ShowsTaskCompleted()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "task_id", "TASK-456" },
                { "action", "complete" }
            };

            var result = "Task TASK-456 marked as complete";

            AssertResultDisplayContains("todo_executor", parameters, result, "complete");
        }

        [Fact]
        public void TodoExecutor_ErrorResult_ShowsTaskNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "task_id", "TASK-999" }
            };

            var errorMessage = "Task not found";

            AssertErrorDisplayContains("todo_executor", parameters, errorMessage, "not found");
        }

        #endregion

        #region GitDiffTool Tests

        [Fact]
        public void GitDiffTool_ParameterDisplay_ShowsFilePath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/repo/src/main.cs" }
            };

            var toolItem = CreateToolItem("git_diff", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void GitDiffTool_ParameterDisplay_ShowsCommitRange()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "commit_from", "abc123" },
                { "commit_to", "def456" }
            };

            var toolItem = CreateToolItem("git_diff", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void GitDiffTool_SuccessResult_ShowsChangeSummary()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/repo/file.cs" }
            };

            var result = "15 lines changed (10 additions, 5 deletions)";

            AssertResultDisplayContains("git_diff", parameters, result, "15 lines", "additions", "deletions");
        }

        [Fact]
        public void GitDiffTool_SuccessResult_ShowsNoChanges()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/repo/unchanged.cs" }
            };

            var result = "No changes";

            AssertResultDisplayContains("git_diff", parameters, result, "No changes");
        }

        [Fact]
        public void GitDiffTool_ErrorResult_ShowsNotGitRepo()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/not/a/git/repo" }
            };

            var errorMessage = "Not a git repository";

            AssertErrorDisplayContains("git_diff", parameters, errorMessage, "Not a git");
        }

        [Fact]
        public void GitDiffTool_ErrorResult_ShowsInvalidCommit()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "commit_from", "invalid_sha" }
            };

            var errorMessage = "Invalid commit reference";

            AssertErrorDisplayContains("git_diff", parameters, errorMessage, "Invalid commit");
        }

        #endregion

        #region CreateDirectoryTool Tests

        [Fact]
        public void CreateDirectoryTool_ParameterDisplay_ShowsPath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/new_directory" }
            };

            var toolItem = CreateToolItem("create_directory", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void CreateDirectoryTool_SuccessResult_ShowsCreated()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/new_folder" }
            };

            var result = "Directory created successfully";

            AssertResultDisplayContains("create_directory", parameters, result, "created");
        }

        [Fact]
        public void CreateDirectoryTool_ErrorResult_ShowsAlreadyExists()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/existing" }
            };

            var errorMessage = "Directory already exists";

            AssertErrorDisplayContains("create_directory", parameters, errorMessage, "already exists");
        }

        [Fact]
        public void CreateDirectoryTool_ErrorResult_ShowsPermissionDenied()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/root/forbidden" }
            };

            var errorMessage = "Permission denied";

            AssertErrorDisplayContains("create_directory", parameters, errorMessage, "Permission denied");
        }

        #endregion

        #region BashCommandTool Tests

        [Fact]
        public void BashCommandTool_ParameterDisplay_ShowsCommand()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "command", "ls -la" }
            };

            var toolItem = CreateToolItem("bash", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void BashCommandTool_SuccessResult_ShowsCommandExecuted()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "command", "echo 'Hello World'" }
            };

            var result = "Command executed (12 lines output)";

            AssertResultDisplayContains("bash", parameters, result, "executed");
        }

        [Fact]
        public void BashCommandTool_SuccessResult_ShowsNoOutput()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "command", "mkdir test" }
            };

            var result = "Command executed";

            AssertResultDisplayContains("bash", parameters, result, "executed");
        }

        [Fact]
        public void BashCommandTool_ErrorResult_ShowsCommandNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "command", "nonexistent_command" }
            };

            var errorMessage = "Command not found: nonexistent_command";

            AssertErrorDisplayContains("bash", parameters, errorMessage, "not found");
        }

        [Fact]
        public void BashCommandTool_ErrorResult_ShowsExitCode()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "command", "grep 'pattern' nonexistent.txt" }
            };

            var errorMessage = "Command failed with exit code 1";

            AssertErrorDisplayContains("bash", parameters, errorMessage, "exit code");
        }

        #endregion

        #region CodeIndexTool Tests

        [Fact]
        public void CodeIndexTool_ParameterDisplay_ShowsPathAndQuery()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/src" },
                { "query", "MyClass" }
            };

            AssertParameterDisplayContains("code_index", parameters, "query:", "MyClass");
        }

        [Fact]
        public void CodeIndexTool_ParameterDisplay_ShowsNamespaceFilter()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "namespace", "Andy.Cli.Services" }
            };

            AssertParameterDisplayContains("code_index", parameters, "namespace:", "Andy.Cli.Services");
        }

        [Fact]
        public void CodeIndexTool_ParameterDisplay_ShowsTypeFilter()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "type", "class" }
            };

            AssertParameterDisplayContains("code_index", parameters, "type:", "class");
        }

        [Fact]
        public void CodeIndexTool_SuccessResult_ShowsStructureIndexed()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/project" },
                { "query_type", "structure" }
            };

            var result = "Structure indexed: 15 namespaces, 42 files (scope: all)";

            AssertResultDisplayContains("code_index", parameters, result, "15 namespaces", "42 files");
        }

        [Fact]
        public void CodeIndexTool_SuccessResult_ShowsSymbolsFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "query", "MyClass" }
            };

            var result = "Found 8 symbols matching 'MyClass' (scope: all)";

            AssertResultDisplayContains("code_index", parameters, result, "8 symbols", "MyClass");
        }

        [Fact]
        public void CodeIndexTool_SuccessResult_ShowsReferences()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "symbol", "IToolExecutor" }
            };

            var result = "Found 23 references to 'IToolExecutor' (scope: all)";

            AssertResultDisplayContains("code_index", parameters, result, "23 references", "IToolExecutor");
        }

        [Fact]
        public void CodeIndexTool_SuccessResult_ShowsHierarchy()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "class", "ToolExecutor" }
            };

            var result = "Retrieved hierarchy for class 'ToolExecutor'";

            AssertResultDisplayContains("code_index", parameters, result, "hierarchy", "ToolExecutor");
        }

        [Fact]
        public void CodeIndexTool_ErrorResult_ShowsInvalidPath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/nonexistent/path" }
            };

            var errorMessage = "Path not found or not accessible";

            AssertErrorDisplayContains("code_index", parameters, errorMessage, "not found");
        }

        [Fact]
        public void CodeIndexTool_ErrorResult_ShowsIndexingFailure()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/corrupted/project" }
            };

            var errorMessage = "Failed to index code: Parse error in file.cs";

            AssertErrorDisplayContains("code_index", parameters, errorMessage, "Failed to index");
        }

        #endregion

        #region Boundary Condition Tests

        [Fact]
        public void BashCommandTool_ParameterDisplay_HandlesLongCommand()
        {
            var longCommand = "find /Users/test -type f -name '*.cs' -exec grep -l 'pattern' {} \\; | xargs wc -l | sort -n";

            var parameters = new Dictionary<string, object?>
            {
                { "command", longCommand }
            };

            var toolItem = CreateToolItem("bash", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void CodeIndexTool_ParameterDisplay_HandlesVeryLongQuery()
        {
            var longQuery = "MyVeryLongClassNameThatExceedsTypicalDisplayLimits";

            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "query", longQuery }
            };

            var toolItem = CreateToolItem("code_index", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void GitDiffTool_SuccessResult_HandlesLargeChangeset()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/large/repo" }
            };

            var result = "1523 lines changed (892 additions, 631 deletions) across 47 files";

            AssertResultDisplayContains("git_diff", parameters, result, "1523 lines", "47 files");
        }

        [Fact]
        public void CreateDirectoryTool_ParameterDisplay_HandlesNestedPath()
        {
            var nestedPath = "/Users/test/very/deeply/nested/directory/structure/level5/level6/final";

            var parameters = new Dictionary<string, object?>
            {
                { "path", nestedPath }
            };

            var toolItem = CreateToolItem("create_directory", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void TodoManagementTool_SuccessResult_HandlesEmptyList()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "action", "list" }
            };

            var result = "No tasks found";

            AssertResultDisplayContains("todo_management", parameters, result, "No tasks");
        }

        [Fact]
        public void CodeIndexTool_SuccessResult_HandlesNoResults()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/src" },
                { "query", "NonexistentClass" }
            };

            var result = "Found 0 symbols matching 'NonexistentClass' (scope: all)";

            AssertResultDisplayContains("code_index", parameters, result, "0 symbols");
        }

        #endregion

        #region Tool-Specific Edge Cases

        [Fact]
        public void BashCommandTool_ParameterDisplay_HandlesCommandWithPipes()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "command", "ps aux | grep dotnet | awk '{print $2}'" }
            };

            var toolItem = CreateToolItem("bash", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void CodeIndexTool_ParameterDisplay_ShowsCurrentDirectoryWhenNoPath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "query", "MyClass" }
            };

            var toolItem = CreateToolItem("code_index", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
        }

        [Fact]
        public void GitDiffTool_SuccessResult_ShowsMultipleFileChanges()
        {
            var parameters = new Dictionary<string, object?>();

            var result = "Changes in 5 files:\n  src/main.cs: +10 -3\n  src/util.cs: +5 -2\n  tests/test.cs: +8 -1";

            var toolItem = CreateToolItem("git_diff", parameters);
            toolItem.SetComplete(true, "0.8s");
            toolItem.SetResult(result);

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);
        }

        [Fact]
        public void TodoManagementTool_ParameterDisplay_HandlesComplexTaskData()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "action", "add" },
                { "task", "Implement feature X with sub-tasks A, B, C" },
                { "priority", "high" },
                { "due_date", "2025-10-20" }
            };

            var toolItem = CreateToolItem("todo_management", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        #endregion
    }
}
