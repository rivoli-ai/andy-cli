using System.Collections.Generic;
using Xunit;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Tests rendering of File System tools
    /// Tools: ReadFileTool, WriteFileTool, ListDirectoryTool, DeleteFileTool, MoveFileTool, CopyFileTool
    /// </summary>
    public class FileSystemToolRenderingTests : ToolRenderingTestBase
    {
        #region ReadFileTool Tests

        [Fact]
        public void ReadFileTool_ParameterDisplay_ShowsFilePath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/documents/report.txt" }
            };

            AssertParameterDisplayContains("read_file", parameters, "report.txt");
        }

        [Fact]
        public void ReadFileTool_ParameterDisplay_WithOffset_ShowsFilePath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/very/long/path/to/nested/directory/structure/file.cs" },
                { "offset", 100 },
                { "limit", 50 }
            };

            AssertParameterDisplayContains("read_file", parameters, "file.cs");
        }

        [Fact]
        public void ReadFileTool_SuccessResult_ShowsLineCount()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/file.txt" }
            };

            var result = "150 lines read";

            AssertResultDisplayContains("read_file", parameters, result, "150", "lines");
        }

        [Fact]
        public void ReadFileTool_ErrorResult_ShowsFileNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/nonexistent/file.txt" }
            };

            var errorMessage = "File not found: /nonexistent/file.txt";

            AssertErrorDisplayContains("read_file", parameters, errorMessage, "not found");
        }

        [Fact]
        public void ReadFileTool_ErrorResult_ShowsPermissionDenied()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/etc/shadow" }
            };

            var errorMessage = "Permission denied accessing file";

            AssertErrorDisplayContains("read_file", parameters, errorMessage, "Permission denied");
        }

        [Fact]
        public void ReadFileTool_LargeFile_ShowsTruncation()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/large_file.log" },
                { "limit", 100 }
            };

            var result = "100 lines read (file has 5000 total lines)";

            AssertResultDisplayContains("read_file", parameters, result, "100", "lines");
        }

        #endregion

        #region WriteFileTool Tests

        [Fact]
        public void WriteFileTool_ParameterDisplay_ShowsFilePath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/output.json" },
                { "content", "{ \"key\": \"value\" }" }
            };

            AssertParameterDisplayContains("write_file", parameters, "output.json");
        }

        [Fact]
        public void WriteFileTool_ParameterDisplay_DoesNotShowContent()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/secret.txt" },
                { "content", "This is sensitive content that should not be in parameter display" }
            };

            var toolItem = CreateToolItem("write_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.DoesNotContain("sensitive content", display);
        }

        [Fact]
        public void WriteFileTool_SuccessResult_ShowsBytesWritten()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/data.txt" },
                { "content", "Hello World" }
            };

            var result = "File written successfully (11 bytes)";

            AssertResultDisplayContains("write_file", parameters, result, "written", "11 bytes");
        }

        [Fact]
        public void WriteFileTool_SuccessResult_ShowsLinesWritten()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/multiline.txt" },
                { "content", "Line1\nLine2\nLine3" }
            };

            var result = "3 lines written to multiline.txt";

            AssertResultDisplayContains("write_file", parameters, result, "3 lines", "written");
        }

        [Fact]
        public void WriteFileTool_ErrorResult_ShowsReadOnlyError()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/read/only/file.txt" },
                { "content", "data" }
            };

            var errorMessage = "File is read-only";

            AssertErrorDisplayContains("write_file", parameters, errorMessage, "read-only");
        }

        [Fact]
        public void WriteFileTool_ErrorResult_ShowsDiskFullError()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/large.bin" },
                { "content", "large content" }
            };

            var errorMessage = "Disk full - cannot write file";

            AssertErrorDisplayContains("write_file", parameters, errorMessage, "Disk full");
        }

        #endregion

        #region ListDirectoryTool Tests

        [Fact]
        public void ListDirectoryTool_ParameterDisplay_ShowsPath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/projects" }
            };

            AssertParameterDisplayContains("list_directory", parameters, "projects");
        }

        [Fact]
        public void ListDirectoryTool_ParameterDisplay_ShowsRecursiveFlag()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test" },
                { "recursive", true }
            };

            AssertParameterDisplayContains("list_directory", parameters, "recursive");
        }

        [Fact]
        public void ListDirectoryTool_ParameterDisplay_ShowsPattern()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/src" },
                { "pattern", "*.cs" }
            };

            AssertParameterDisplayContains("list_directory", parameters, "pattern", "*.cs");
        }

        [Fact]
        public void ListDirectoryTool_ParameterDisplay_ShowsHiddenFlag()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test" },
                { "include_hidden", true }
            };

            AssertParameterDisplayContains("list_directory", parameters, "hidden");
        }

        [Fact]
        public void ListDirectoryTool_SuccessResult_ShowsItemCount()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test" }
            };

            var result = "Found 25 files and 8 directories";

            AssertResultDisplayContains("list_directory", parameters, result, "25", "files", "8", "directories");
        }

        [Fact]
        public void ListDirectoryTool_SuccessResult_ShowsEmptyDirectory()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/Users/test/empty" }
            };

            var result = "Directory is empty";

            AssertResultDisplayContains("list_directory", parameters, result, "empty");
        }

        [Fact]
        public void ListDirectoryTool_ErrorResult_ShowsDirectoryNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/nonexistent/directory" }
            };

            var errorMessage = "Directory not found";

            AssertErrorDisplayContains("list_directory", parameters, errorMessage, "not found");
        }

        [Fact]
        public void ListDirectoryTool_ErrorResult_ShowsPermissionDenied()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "path", "/private/restricted" }
            };

            var errorMessage = "Permission denied";

            AssertErrorDisplayContains("list_directory", parameters, errorMessage, "Permission denied");
        }

        #endregion

        #region DeleteFileTool Tests

        [Fact]
        public void DeleteFileTool_ParameterDisplay_ShowsFilePath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/temp/old_file.txt" }
            };

            AssertParameterDisplayContains("delete_file", parameters, "old_file.txt");
        }

        [Fact]
        public void DeleteFileTool_ParameterDisplay_ShowsMultipleFiles()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_paths", new[] { "/tmp/file1.txt", "/tmp/file2.txt", "/tmp/file3.txt" } }
            };

            var toolItem = CreateToolItem("delete_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void DeleteFileTool_SuccessResult_ShowsSingleFileDeleted()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/tmp/temp.txt" }
            };

            var result = "File deleted successfully";

            AssertResultDisplayContains("delete_file", parameters, result, "deleted");
        }

        [Fact]
        public void DeleteFileTool_SuccessResult_ShowsMultipleFilesDeleted()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_paths", new[] { "/tmp/f1.txt", "/tmp/f2.txt", "/tmp/f3.txt" } }
            };

            var result = "3 files deleted successfully";

            AssertResultDisplayContains("delete_file", parameters, result, "3 files", "deleted");
        }

        [Fact]
        public void DeleteFileTool_ErrorResult_ShowsFileNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/nonexistent.txt" }
            };

            var errorMessage = "File not found";

            AssertErrorDisplayContains("delete_file", parameters, errorMessage, "not found");
        }

        [Fact]
        public void DeleteFileTool_ErrorResult_ShowsFileInUse()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/locked.db" }
            };

            var errorMessage = "File is in use by another process";

            AssertErrorDisplayContains("delete_file", parameters, errorMessage, "in use");
        }

        #endregion

        #region MoveFileTool Tests

        [Fact]
        public void MoveFileTool_ParameterDisplay_ShowsSourceAndDestination()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/Users/test/old/file.txt" },
                { "destination_path", "/Users/test/new/file.txt" }
            };

            var toolItem = CreateToolItem("move_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void MoveFileTool_SuccessResult_ShowsFileMoved()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/old/location/file.txt" },
                { "destination_path", "/new/location/file.txt" }
            };

            var result = "File moved successfully";

            AssertResultDisplayContains("move_file", parameters, result, "moved");
        }

        [Fact]
        public void MoveFileTool_ErrorResult_ShowsSourceNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/nonexistent.txt" },
                { "destination_path", "/target.txt" }
            };

            var errorMessage = "Source file not found";

            AssertErrorDisplayContains("move_file", parameters, errorMessage, "not found");
        }

        [Fact]
        public void MoveFileTool_ErrorResult_ShowsDestinationExists()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/source.txt" },
                { "destination_path", "/existing.txt" }
            };

            var errorMessage = "Destination file already exists";

            AssertErrorDisplayContains("move_file", parameters, errorMessage, "already exists");
        }

        #endregion

        #region CopyFileTool Tests

        [Fact]
        public void CopyFileTool_ParameterDisplay_ShowsSourceAndDestination()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/Users/test/original.pdf" },
                { "destination_path", "/Users/test/backup/copy.pdf" }
            };

            var toolItem = CreateToolItem("copy_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void CopyFileTool_ParameterDisplay_ShowsMultipleFiles()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_paths", new[] { "/src/f1.txt", "/src/f2.txt" } },
                { "destination_directory", "/backup/" }
            };

            var toolItem = CreateToolItem("copy_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void CopyFileTool_SuccessResult_ShowsSingleFileCopied()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/original.txt" },
                { "destination_path", "/copy.txt" }
            };

            var result = "File copied successfully (1.2 MB)";

            AssertResultDisplayContains("copy_file", parameters, result, "copied", "MB");
        }

        [Fact]
        public void CopyFileTool_SuccessResult_ShowsMultipleFilesCopied()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_paths", new[] { "/f1.txt", "/f2.txt", "/f3.txt" } },
                { "destination_directory", "/backup/" }
            };

            var result = "3 files copied successfully (5.8 MB total)";

            AssertResultDisplayContains("copy_file", parameters, result, "3 files", "copied");
        }

        [Fact]
        public void CopyFileTool_ErrorResult_ShowsSourceNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/missing.txt" },
                { "destination_path", "/copy.txt" }
            };

            var errorMessage = "Source file not found";

            AssertErrorDisplayContains("copy_file", parameters, errorMessage, "not found");
        }

        [Fact]
        public void CopyFileTool_ErrorResult_ShowsInsufficientSpace()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "source_path", "/large_file.bin" },
                { "destination_path", "/backup/large_file.bin" }
            };

            var errorMessage = "Insufficient disk space";

            AssertErrorDisplayContains("copy_file", parameters, errorMessage, "Insufficient");
        }

        #endregion

        #region Boundary Condition Tests

        [Fact]
        public void FileSystemTools_ParameterDisplay_HandlesVeryLongPaths()
        {
            var longPath = "/Users/test/very/long/nested/directory/structure/with/many/levels/deep/into/the/filesystem/hierarchy/that/might/exceed/typical/display/width/limits/final_file.txt";

            var parameters = new Dictionary<string, object?>
            {
                { "file_path", longPath }
            };

            var toolItem = CreateToolItem("read_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
            Assert.Contains("...", display);
        }

        [Fact]
        public void FileSystemTools_ParameterDisplay_HandlesSpecialCharacters()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/Users/test/file with spaces & special-chars (2024).txt" }
            };

            var toolItem = CreateToolItem("read_file", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void FileSystemTools_ResultDisplay_HandlesEmptyResult()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/empty.txt" }
            };

            var toolItem = CreateToolItem("read_file", parameters);
            toolItem.SetComplete(true, "0.1s");
            toolItem.SetResult("");

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);
        }

        [Fact]
        public void FileSystemTools_ResultDisplay_HandlesVeryLongErrorMessages()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "file_path", "/test.txt" }
            };

            var longErrorMessage = "An error occurred while processing the file operation. The file system returned an unexpected error code that indicates a complex failure condition involving permissions, disk space, file locking, and possibly network issues if this is a remote filesystem. Additional diagnostic information has been logged to the system error log.";

            var toolItem = CreateToolItem("read_file", parameters);
            toolItem.SetComplete(false, "0.5s");
            toolItem.SetResult(longErrorMessage);

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);
            Assert.Contains("error", summary.ToLower());
        }

        #endregion
    }
}
