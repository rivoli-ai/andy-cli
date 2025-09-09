namespace Andy.Cli.Tests.TestData;

/// <summary>
/// Realistic sample LLM responses for testing tool invocation scenarios
/// </summary>
public static class SampleLlmResponses
{
    /// <summary>
    /// Simple single tool invocation responses
    /// </summary>
    public static class SingleToolCalls
    {
        public const string ListDirectory = @"I'll list the files in the current directory for you.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":"".""}}";

        public const string ListSpecificDirectory = @"Let me check what files are in the src directory.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":""src""}}";

        public const string ReadFile = @"I'll read the contents of that file for you.

[Tool Request]
{""tool"":""read_file"",""parameters"":{""file_path"":""README.md""}}";

        public const string WriteFile = @"I'll create that file with the content you specified.

[Tool Request]
{""tool"":""write_file"",""parameters"":{""file_path"":""test.txt"",""content"":""Hello World""}}";

        public const string CreateDirectory = @"I'll create the new directory for you.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""new-folder"",""create_parents"":true}}";

        public const string CopyFile = @"I'll copy that file to the destination.

[Tool Request]
{""tool"":""copy_file"",""parameters"":{""source_path"":""source.txt"",""destination_path"":""backup/source.txt""}}";

        public const string MoveFile = @"I'll move the file to the new location.

[Tool Request]
{""tool"":""move_file"",""parameters"":{""source_path"":""old-location.txt"",""destination_path"":""new-location.txt""}}";

        public const string DeleteFile = @"I'll delete that file for you. Please note this action cannot be undone.

[Tool Request]
{""tool"":""delete_file"",""parameters"":{""file_path"":""temp.txt""}}";

        public const string SystemInfo = @"Let me get the system information for you.

[Tool Request]
{""tool"":""system_info"",""parameters"":{}}";
    }

    /// <summary>
    /// Multi-step tool invocation sequences
    /// </summary>
    public static class MultiStepSequences
    {
        public static readonly string[] CreateProjectStructure = new[]
        {
            @"I'll help you set up a new project structure. First, let me create the main directories.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""src"",""create_parents"":true}}",

            @"Now I'll create the tests directory.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""tests"",""create_parents"":true}}",

            @"Let me create the docs folder as well.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""docs"",""create_parents"":true}}",

            @"Now I'll create a basic README file for your project.

[Tool Request]
{""tool"":""write_file"",""parameters"":{""file_path"":""README.md"",""content"":""# New Project\n\nThis is a new project created with Andy CLI.\n\n## Structure\n\n- `src/` - Source code\n- `tests/` - Test files\n- `docs/` - Documentation\n""}}",

            @"Let me also add a .gitignore file for common files.

[Tool Request]
{""tool"":""write_file"",""parameters"":{""file_path"":"".gitignore"",""content"":""# Build outputs\nbin/\nobj/\n\n# IDE files\n.vs/\n.vscode/\n*.user\n\n# OS files\n.DS_Store\nThumbs.db\n""}}",

            @"Perfect! I've created a basic project structure with:
- Main directories (src, tests, docs)
- README.md with project description
- .gitignore with common exclusions

Your project is ready to go!"
        };

        public static readonly string[] BackupAndCleanup = new[]
        {
            @"I'll help you backup important files and clean up old ones. First, let me see what files we have.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":"".""}}",

            @"I can see the files. Now let me create a backup directory.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""backup"",""create_parents"":true}}",

            @"I'll copy the config.json file to the backup folder.

[Tool Request]
{""tool"":""copy_file"",""parameters"":{""source_path"":""config.json"",""destination_path"":""backup/config.json""}}",

            @"Now copying the data.csv file.

[Tool Request]
{""tool"":""copy_file"",""parameters"":{""source_path"":""data.csv"",""destination_path"":""backup/data.csv""}}",

            @"I'll remove the old backup file that's no longer needed.

[Tool Request]
{""tool"":""delete_file"",""parameters"":{""file_path"":""old_backup.json""}}",

            @"Removing the temporary file as well.

[Tool Request]
{""tool"":""delete_file"",""parameters"":{""file_path"":""temp.txt""}}",

            @"Backup complete! I've:
- Created a backup folder
- Copied config.json and data.csv to the backup
- Deleted old_backup.json and temp.txt
Your important files are now safely backed up."
        };

        public static readonly string[] FileOrganization = new[]
        {
            @"I'll help organize your files by type. Let me first check what files you have.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":"".""}}",

            @"I see various file types. Let me create organized directories for each type. Starting with documents.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""documents"",""create_parents"":true}}",

            @"Creating directory for images.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""images"",""create_parents"":true}}",

            @"Creating directory for scripts.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""scripts"",""create_parents"":true}}",

            @"Now I'll move the text document to the documents folder.

[Tool Request]
{""tool"":""move_file"",""parameters"":{""source_path"":""document.txt"",""destination_path"":""documents/document.txt""}}",

            @"Moving the image file.

[Tool Request]
{""tool"":""move_file"",""parameters"":{""source_path"":""image.png"",""destination_path"":""images/image.png""}}",

            @"Moving the JavaScript file to scripts.

[Tool Request]
{""tool"":""move_file"",""parameters"":{""source_path"":""script.js"",""destination_path"":""scripts/script.js""}}",

            @"Files have been organized successfully! I've moved:
- document.txt → documents/
- image.png → images/
- script.js → scripts/

Your files are now organized by type."
        };
    }

    /// <summary>
    /// Responses that include analysis or decision-making
    /// </summary>
    public static class AnalyticalResponses
    {
        public const string CheckBeforeAction = @"Let me first check if the destination directory exists before copying the file.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":""backup""}}";

        public const string AnalyzeAndSuggest = @"I've analyzed the file structure. Based on what I see, you have several unorganized files. Let me check the current directory first to provide specific recommendations.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":"".""}}";

        public const string ConditionalAction = @"I'll check the file size first to determine the best approach for handling this file.

[Tool Request]
{""tool"":""read_file"",""parameters"":{""file_path"":""large_file.txt""}}";
    }

    /// <summary>
    /// Error handling and recovery responses
    /// </summary>
    public static class ErrorResponses
    {
        public const string FileNotFound = @"I'll try to read the file you specified.

[Tool Request]
{""tool"":""read_file"",""parameters"":{""file_path"":""nonexistent.txt""}}";

        public const string InvalidPath = @"I'll attempt to access that directory.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":""/restricted/path""}}";

        public const string RecoveryAfterError = @"I see that the previous operation failed. Let me try a different approach. First, I'll create the necessary directory.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""backup"",""create_parents"":true}}";

        public const string PermissionDenied = @"I'll try to write to that location.

[Tool Request]
{""tool"":""write_file"",""parameters"":{""file_path"":""/system/protected.txt"",""content"":""test""}}";
    }

    /// <summary>
    /// Conversational responses without tool calls
    /// </summary>
    public static class NonToolResponses
    {
        public const string Greeting = "Hello! I'm Andy CLI Assistant. I can help you manage files, directories, and perform various system operations. What would you like to do today?";

        public const string AskingForClarification = "I need more information to help you. Could you please specify which file you'd like to work with and what operation you want to perform?";

        public const string ExplainingCapabilities = @"I can help you with various file and system operations:

• **File Operations**: Read, write, copy, move, and delete files
• **Directory Management**: Create directories, list contents, organize files
• **System Information**: Get system details and environment information

What would you like me to help you with?";

        public const string ConfirmingDangerousAction = "You're asking me to delete multiple files. This action cannot be undone. Are you sure you want to proceed? Please confirm by saying 'yes, delete all' if you want to continue.";

        public const string TaskComplete = "All done! I've completed the requested operations successfully. The files have been organized and the backup has been created. Is there anything else you'd like me to help with?";

        public const string Clarification = "I need more information. What would you like to do with which file?";
    }

    /// <summary>
    /// Complex scenarios with mixed responses
    /// </summary>
    public static class ComplexScenarios
    {
        public static readonly string[] AnalyzeAndReport = new[]
        {
            @"I'll analyze your project structure and create a report. Let me start by examining the directory structure.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":"".""}}",

            @"Now let me check the src directory for source files.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":""src""}}",

            @"Let me also check what's in the tests directory.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":""tests""}}",

            @"I'll gather system information to include in the report.

[Tool Request]
{""tool"":""system_info"",""parameters"":{}}",

            @"Now I'll create a comprehensive report based on my analysis.

[Tool Request]
{""tool"":""write_file"",""parameters"":{""file_path"":""project-report.md"",""content"":""# Project Analysis Report\n\n## Directory Structure\n\n### Root Directory\n- Source files: 5 files\n- Configuration: 2 files\n- Documentation: 3 files\n\n### Source Directory (src/)\n- Main application files: 12 files\n- Utilities: 5 files\n- Services: 8 files\n\n### Tests Directory (tests/)\n- Unit tests: 10 files\n- Integration tests: 5 files\n\n## System Information\n- OS: Windows 10\n- Memory: 16GB\n- Disk Space: 250GB available\n\n## Recommendations\n\n1. Consider organizing configuration files into a separate 'config' directory\n2. Add more unit test coverage for service classes\n3. Update documentation to reflect recent changes\n\n## Summary\n\nThe project structure is well-organized with clear separation of concerns. The codebase follows standard practices with separate directories for source, tests, and documentation.\n""}}",

            "I've completed the analysis and created a detailed report in 'project-report.md'. The report includes directory structure analysis, system information, and recommendations for improvements."
        };

        public static readonly string[] IntelligentFileProcessing = new[]
        {
            @"I'll help you process and organize these data files. Let me first check what we're working with.

[Tool Request]
{""tool"":""list_directory"",""parameters"":{""path"":""data""}}",

            @"I see CSV files that need processing. Let me read the first one to understand the structure.

[Tool Request]
{""tool"":""read_file"",""parameters"":{""file_path"":""data/sales_2024.csv""}}",

            @"Based on the data structure, I'll create a summary. Let me check another file to ensure consistency.

[Tool Request]
{""tool"":""read_file"",""parameters"":{""file_path"":""data/sales_2023.csv""}}",

            @"The data formats are consistent. I'll now create a processed directory for the output.

[Tool Request]
{""tool"":""create_directory"",""parameters"":{""path"":""data/processed"",""create_parents"":true}}",

            @"I'll create a summary file with the key insights from the data.

[Tool Request]
{""tool"":""write_file"",""parameters"":{""file_path"":""data/processed/summary.txt"",""content"":""Sales Data Summary\n==================\n\n2024 Sales:\n- Total Records: 1,250\n- Average Sale: $2,450\n- Top Month: March ($125,000)\n\n2023 Sales:\n- Total Records: 1,100\n- Average Sale: $2,200\n- Top Month: December ($118,000)\n\nYear-over-Year Growth: 11.4%\n\nKey Insights:\n- Q1 2024 shows strong growth compared to Q1 2023\n- Customer base increased by 13.6%\n- Average transaction value up by 11.4%\n""}}",

            "I've processed your sales data and created a summary in 'data/processed/summary.txt'. The analysis shows positive year-over-year growth of 11.4% with particularly strong performance in Q1 2024."
        };
    }
}