using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Andy.Cli.Services;

/// <summary>
/// Provides intelligent summarization of file contents for LLM consumption
/// </summary>
public static class FileContentSummarizer
{
    private const int MaxPreviewLines = 50;
    private const int MaxPreviewChars = 2000;
    
    /// <summary>
    /// Intelligently summarize file content for LLM processing
    /// </summary>
    public static string SummarizeFileContent(string filePath, string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;
        
        var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        var lines = content.Split('\n');
        var totalLines = lines.Length;
        var totalChars = content.Length;
        
        // For small files, return as-is
        if (totalChars <= MaxPreviewChars)
            return content;
        
        var summary = new StringBuilder();
        
        // Add file statistics
        summary.AppendLine($"[File: {filePath}]");
        summary.AppendLine($"[Size: {totalChars:N0} characters, {totalLines:N0} lines]");
        summary.AppendLine();
        
        // For code files, extract structure
        if (IsCodeFile(extension))
        {
            var structure = ExtractCodeStructure(content, extension);
            if (!string.IsNullOrEmpty(structure))
            {
                summary.AppendLine("=== File Structure ===");
                summary.AppendLine(structure);
                summary.AppendLine();
            }
        }
        
        // Add first N lines as preview
        summary.AppendLine("=== Content Preview (first 50 lines) ===");
        var previewLines = Math.Min(MaxPreviewLines, lines.Length);
        for (int i = 0; i < previewLines; i++)
        {
            summary.AppendLine(lines[i]);
        }
        
        if (totalLines > MaxPreviewLines)
        {
            summary.AppendLine();
            summary.AppendLine($"[... {totalLines - MaxPreviewLines} more lines omitted ...]");
            summary.AppendLine();
            
            // Add last few lines for context
            summary.AppendLine("=== Last 10 lines ===");
            var startLine = Math.Max(0, totalLines - 10);
            for (int i = startLine; i < totalLines; i++)
            {
                summary.AppendLine(lines[i]);
            }
        }
        
        return summary.ToString();
    }
    
    private static bool IsCodeFile(string extension)
    {
        var codeExtensions = new[] { ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".rb", ".php" };
        return codeExtensions.Contains(extension);
    }
    
    private static string ExtractCodeStructure(string content, string extension)
    {
        var structure = new StringBuilder();
        
        if (extension == ".cs")
        {
            // Extract C# structure
            var namespaces = ExtractMatches(content, @"namespace\s+([\w.]+)");
            var classes = ExtractMatches(content, @"(?:public|private|internal|protected)?\s*(?:static|abstract|sealed)?\s*(?:partial\s+)?class\s+(\w+)");
            var interfaces = ExtractMatches(content, @"(?:public|private|internal)?\s*interface\s+(\w+)");
            var methods = ExtractMatches(content, @"(?:public|private|protected|internal)?\s*(?:static|virtual|override|async)?\s*(?:void|Task|[\w<>]+)\s+(\w+)\s*\(");
            
            if (namespaces.Any())
            {
                structure.AppendLine($"Namespaces: {string.Join(", ", namespaces.Distinct())}");
            }
            if (classes.Any())
            {
                structure.AppendLine($"Classes ({classes.Count}): {string.Join(", ", classes.Take(10))}");
                if (classes.Count > 10)
                    structure.AppendLine($"  ... and {classes.Count - 10} more");
            }
            if (interfaces.Any())
            {
                structure.AppendLine($"Interfaces: {string.Join(", ", interfaces)}");
            }
            if (methods.Any())
            {
                var publicMethods = methods.Where(m => !m.StartsWith("get_") && !m.StartsWith("set_")).Take(10).ToList();
                structure.AppendLine($"Key Methods ({methods.Count}): {string.Join(", ", publicMethods)}");
                if (methods.Count > 10)
                    structure.AppendLine($"  ... and {methods.Count - 10} more");
            }
        }
        
        return structure.ToString();
    }
    
    private static List<string> ExtractMatches(string content, string pattern)
    {
        var matches = new List<string>();
        var regex = new Regex(pattern, RegexOptions.Multiline);
        foreach (Match match in regex.Matches(content))
        {
            if (match.Groups.Count > 1)
                matches.Add(match.Groups[1].Value);
        }
        return matches;
    }
}