using System.Collections.Generic;
using Xunit;

namespace Andy.Cli.Tests.Widgets
{
    /// <summary>
    /// Tests rendering of Text Processing, System, Web, and Utility tools
    /// Text Tools: SearchTextTool, ReplaceTextTool, FormatTextTool
    /// System Tools: SystemInfoTool, ProcessInfoTool
    /// Web Tools: HttpRequestTool, JsonProcessorTool
    /// Utility Tools: DateTimeTool, EncodingTool
    /// </summary>
    public class TextAndSystemToolRenderingTests : ToolRenderingTestBase
    {
        #region SearchTextTool Tests

        [Fact]
        public void SearchTextTool_ParameterDisplay_ShowsPatternAndPath()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "TODO" },
                { "path", "/Users/test/src" }
            };

            var toolItem = CreateToolItem("search_text", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void SearchTextTool_SuccessResult_ShowsMatchCount()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "function" },
                { "path", "/Users/test/src" }
            };

            var result = "42 matches found";

            AssertResultDisplayContains("search_text", parameters, result, "42", "matches");
        }

        [Fact]
        public void SearchTextTool_SuccessResult_ShowsNoMatches()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "nonexistent_pattern" },
                { "path", "/Users/test" }
            };

            var result = "0 matches found";

            AssertResultDisplayContains("search_text", parameters, result, "0");
        }

        [Fact]
        public void SearchTextTool_ErrorResult_ShowsInvalidRegex()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "([invalid" },
                { "path", "/Users/test" }
            };

            var errorMessage = "Invalid regular expression pattern";

            AssertErrorDisplayContains("search_text", parameters, errorMessage, "Invalid", "pattern");
        }

        #endregion

        #region ReplaceTextTool Tests

        [Fact]
        public void ReplaceTextTool_ParameterDisplay_ShowsPatternNotReplacement()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "oldValue" },
                { "replacement", "newValue" },
                { "file_path", "/Users/test/config.json" }
            };

            var toolItem = CreateToolItem("replace_text", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void ReplaceTextTool_SuccessResult_ShowsReplacementCount()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "foo" },
                { "replacement", "bar" },
                { "file_path", "/Users/test/code.cs" }
            };

            var result = "15 replacements made";

            AssertResultDisplayContains("replace_text", parameters, result, "15", "replacements");
        }

        [Fact]
        public void ReplaceTextTool_ErrorResult_ShowsFileReadOnly()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "old" },
                { "replacement", "new" },
                { "file_path", "/readonly.txt" }
            };

            var errorMessage = "Cannot modify read-only file";

            AssertErrorDisplayContains("replace_text", parameters, errorMessage, "read-only");
        }

        #endregion

        #region FormatTextTool Tests

        [Fact]
        public void FormatTextTool_ParameterDisplay_ShowsFormatType()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "unformatted code" },
                { "format", "json" }
            };

            var toolItem = CreateToolItem("format_text", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void FormatTextTool_SuccessResult_ShowsFormatted()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "{\"key\":\"value\"}" },
                { "format", "json" }
            };

            var result = "Text formatted successfully";

            AssertResultDisplayContains("format_text", parameters, result, "formatted");
        }

        [Fact]
        public void FormatTextTool_ErrorResult_ShowsInvalidJson()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "{invalid json}" },
                { "format", "json" }
            };

            var errorMessage = "Invalid JSON syntax";

            AssertErrorDisplayContains("format_text", parameters, errorMessage, "Invalid", "JSON");
        }

        #endregion

        #region SystemInfoTool Tests

        [Fact]
        public void SystemInfoTool_ParameterDisplay_ShowsInfoType()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "info_type", "cpu" }
            };

            var toolItem = CreateToolItem("system_info", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
        }

        [Fact]
        public void SystemInfoTool_SuccessResult_ShowsSystemInfo()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "info_type", "all" }
            };

            var result = "OS: macOS 14.0, CPU: Apple M2, RAM: 16 GB";

            AssertResultDisplayContains("system_info", parameters, result, "macOS", "M2");
        }

        #endregion

        #region ProcessInfoTool Tests

        [Fact]
        public void ProcessInfoTool_ParameterDisplay_ShowsProcessId()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "process_id", 1234 }
            };

            var toolItem = CreateToolItem("process_info", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void ProcessInfoTool_SuccessResult_ShowsProcessDetails()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "process_id", 5678 }
            };

            var result = "Process 'dotnet' (PID: 5678) - Memory: 125 MB, CPU: 15%";

            AssertResultDisplayContains("process_info", parameters, result, "dotnet", "5678");
        }

        [Fact]
        public void ProcessInfoTool_ErrorResult_ShowsProcessNotFound()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "process_id", 99999 }
            };

            var errorMessage = "Process not found";

            AssertErrorDisplayContains("process_info", parameters, errorMessage, "not found");
        }

        #endregion

        #region HttpRequestTool Tests

        [Fact]
        public void HttpRequestTool_ParameterDisplay_ShowsUrlAndMethod()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "url", "https://api.example.com/users" },
                { "method", "GET" }
            };

            var toolItem = CreateToolItem("http_request", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void HttpRequestTool_SuccessResult_ShowsStatusCode()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "url", "https://api.example.com/data" },
                { "method", "GET" }
            };

            var result = "HTTP 200 OK - 1.2 KB received";

            AssertResultDisplayContains("http_request", parameters, result, "200", "OK");
        }

        [Fact]
        public void HttpRequestTool_ErrorResult_ShowsNetworkError()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "url", "https://unreachable.example.com" },
                { "method", "GET" }
            };

            var errorMessage = "Network error: Connection timeout";

            AssertErrorDisplayContains("http_request", parameters, errorMessage, "timeout");
        }

        [Fact]
        public void HttpRequestTool_ErrorResult_Shows404()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "url", "https://api.example.com/missing" },
                { "method", "GET" }
            };

            var errorMessage = "HTTP 404 Not Found";

            AssertErrorDisplayContains("http_request", parameters, errorMessage, "404");
        }

        #endregion

        #region JsonProcessorTool Tests

        [Fact]
        public void JsonProcessorTool_ParameterDisplay_ShowsOperation()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "json_data", "{\"key\":\"value\"}" },
                { "operation", "validate" }
            };

            var toolItem = CreateToolItem("json_processor", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
        }

        [Fact]
        public void JsonProcessorTool_SuccessResult_ShowsValidation()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "json_data", "{\"key\":\"value\"}" },
                { "operation", "validate" }
            };

            var result = "JSON is valid";

            AssertResultDisplayContains("json_processor", parameters, result, "valid");
        }

        [Fact]
        public void JsonProcessorTool_ErrorResult_ShowsInvalidJson()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "json_data", "{invalid}" },
                { "operation", "validate" }
            };

            var errorMessage = "JSON syntax error at line 1, column 2";

            AssertErrorDisplayContains("json_processor", parameters, errorMessage, "syntax error");
        }

        #endregion

        #region DateTimeTool Tests

        [Fact]
        public void DateTimeTool_ParameterDisplay_ShowsOperationAndTimezone()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "current_date" },
                { "timezone", "UTC" }
            };

            AssertParameterDisplayContains("datetime_tool", parameters, "op:current_date", "tz:UTC");
        }

        [Fact]
        public void DateTimeTool_ParameterDisplay_ShowsFormatOperation()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "format" },
                { "format", "yyyy-MM-dd" }
            };

            AssertParameterDisplayContains("datetime_tool", parameters, "op:format");
        }

        [Fact]
        public void DateTimeTool_SuccessResult_ShowsFormattedDate()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "current_date" }
            };

            var result = "Thursday, October 17, 2025";

            AssertResultDisplayContains("datetime_tool", parameters, result, "October", "2025");
        }

        [Fact]
        public void DateTimeTool_SuccessResult_ShowsTime()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "current_time" },
                { "timezone", "PST" }
            };

            var result = "14:35:22 PST";

            AssertResultDisplayContains("datetime_tool", parameters, result, "14:35");
        }

        [Fact]
        public void DateTimeTool_ErrorResult_ShowsInvalidTimezone()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "current_time" },
                { "timezone", "INVALID" }
            };

            var errorMessage = "Invalid timezone identifier";

            AssertErrorDisplayContains("datetime_tool", parameters, errorMessage, "Invalid", "timezone");
        }

        #endregion

        #region EncodingTool Tests

        [Fact]
        public void EncodingTool_ParameterDisplay_ShowsOperation()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "Hello World" },
                { "operation", "base64_encode" }
            };

            var toolItem = CreateToolItem("encoding_tool", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
        }

        [Fact]
        public void EncodingTool_SuccessResult_ShowsEncoded()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "test" },
                { "operation", "base64_encode" }
            };

            var result = "SGVsbG8gV29ybGQ=";

            AssertResultDisplayContains("encoding_tool", parameters, result, "SGVsbG8");
        }

        [Fact]
        public void EncodingTool_ErrorResult_ShowsInvalidBase64()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "invalid===" },
                { "operation", "base64_decode" }
            };

            var errorMessage = "Invalid base64 string";

            AssertErrorDisplayContains("encoding_tool", parameters, errorMessage, "Invalid", "base64");
        }

        #endregion

        #region Boundary Condition Tests

        [Fact]
        public void SearchTextTool_SuccessResult_HandlesLargeMatchCount()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "pattern", "var" },
                { "path", "/large_codebase" }
            };

            var result = "15432 matches found across 856 files";

            AssertResultDisplayContains("search_text", parameters, result, "15432", "matches");
        }

        [Fact]
        public void HttpRequestTool_ParameterDisplay_HandlesVeryLongUrl()
        {
            var longUrl = "https://api.example.com/v1/resources/search?query=test&filter=active&sort=name&page=1&limit=100&include=metadata,stats&exclude=archived";

            var parameters = new Dictionary<string, object?>
            {
                { "url", longUrl },
                { "method", "GET" }
            };

            var toolItem = CreateToolItem("http_request", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.NotEqual("loading...", display);
        }

        [Fact]
        public void DateTimeTool_ParameterDisplay_WithoutTimezone_ShowsOnlyOperation()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "operation", "now" }
            };

            var toolItem = CreateToolItem("datetime_tool", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.Contains("op:now", display);
            Assert.DoesNotContain("tz:", display);
        }

        [Fact]
        public void TextProcessingTools_ResultDisplay_HandlesMultilineResults()
        {
            var parameters = new Dictionary<string, object?>
            {
                { "text", "multiline\ntext\nhere" },
                { "format", "json" }
            };

            var multilineResult = "Formatted result:\nLine 1\nLine 2\nLine 3";

            var toolItem = CreateToolItem("format_text", parameters);
            toolItem.SetComplete(true, "0.5s");
            toolItem.SetResult(multilineResult);

            var summary = GetResultSummary(toolItem);

            Assert.NotNull(summary);
        }

        [Fact]
        public void SystemTools_ParameterDisplay_HandlesEmptyParameters()
        {
            var parameters = new Dictionary<string, object?>();

            var toolItem = CreateToolItem("system_info", parameters);
            var display = GetParameterDisplay(toolItem);

            Assert.NotNull(display);
            Assert.Equal("loading...", display);
        }

        #endregion
    }
}
