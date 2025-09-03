using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets;

/// <summary>
/// Widget for displaying tool execution status and output
/// </summary>
public class ToolExecutionDisplay : IFeedItem
{
    private readonly string _toolId;
    private readonly string _toolName;
    private readonly Dictionary<string, object?> _parameters;
    private readonly List<string> _outputLines;
    private readonly int _maxDisplayLines;
    private bool _isComplete;
    private bool _isSuccessful;
    private string? _error;

    public ToolExecutionDisplay(
        string toolId,
        string toolName,
        Dictionary<string, object?> parameters,
        int maxDisplayLines = 10)
    {
        _toolId = toolId;
        _toolName = toolName;
        _parameters = parameters;
        _outputLines = new List<string>();
        _maxDisplayLines = maxDisplayLines;
        _isComplete = false;
        _isSuccessful = false;
    }

    /// <summary>
    /// Add output line from tool execution
    /// </summary>
    public void AddOutput(string output)
    {
        if (!string.IsNullOrEmpty(output))
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _outputLines.AddRange(lines);
        }
    }

    /// <summary>
    /// Mark execution as complete
    /// </summary>
    public void SetComplete(bool successful, string? error = null)
    {
        _isComplete = true;
        _isSuccessful = successful;
        _error = error;
    }

    public int MeasureLineCount(int width)
    {
        // Calculate lines needed
        int lines = 3; // Header + separator + status
        
        // Parameters
        if (_parameters.Any())
        {
            lines += 1 + _parameters.Count; // "Parameters:" + each param
        }
        
        // Output
        if (_outputLines.Any())
        {
            lines += 2; // "Output:" header + separator
            lines += Math.Min(_outputLines.Count, _maxDisplayLines);
            
            if (_outputLines.Count > _maxDisplayLines)
            {
                lines++; // Truncation message
            }
        }
        
        // Error
        if (!string.IsNullOrEmpty(_error))
        {
            lines += 2; // Error header + message
        }
        
        return lines;
    }

    public void RenderSlice(int x, int y, int width, int startLine, int maxLines, DL.DisplayList baseDl, DL.DisplayListBuilder b)
    {
        if (width <= 0 || maxLines <= 0) return;
        
        var currentY = y;
        var linesDrawn = 0;
        
        // Draw border
        var borderColor = _isComplete 
            ? (_isSuccessful ? new DL.Rgb24(60, 120, 60) : new DL.Rgb24(120, 60, 60))
            : new DL.Rgb24(100, 100, 40);
        
        // Header
        if (linesDrawn < maxLines && startLine <= 0)
        {
            var statusIcon = _isComplete ? (_isSuccessful ? "[OK]" : "[FAIL]") : "[RUN]";
            var headerText = $"{statusIcon} Tool: {_toolName}";
            b.DrawText(new DL.TextRun(x, currentY, headerText, new DL.Rgb24(255, 255, 255), null, DL.CellAttrFlags.Bold));
            currentY++;
            linesDrawn++;
        }
        
        // Tool ID
        if (linesDrawn < maxLines && startLine <= 1)
        {
            b.DrawText(new DL.TextRun(x + 2, currentY, $"ID: {_toolId}", new DL.Rgb24(150, 150, 200), null, DL.CellAttrFlags.None));
            currentY++;
            linesDrawn++;
        }
        
        // Parameters
        if (_parameters.Any() && linesDrawn < maxLines)
        {
            var paramLine = 2;
            if (startLine <= paramLine)
            {
                b.DrawText(new DL.TextRun(x + 2, currentY, "Parameters:", new DL.Rgb24(200, 200, 100), null, DL.CellAttrFlags.None));
                currentY++;
                linesDrawn++;
                paramLine++;
            }
            
            foreach (var param in _parameters)
            {
                if (linesDrawn >= maxLines) break;
                if (startLine <= paramLine)
                {
                    var value = param.Value?.ToString() ?? "null";
                    if (value.Length > 50)
                    {
                        value = value.Substring(0, 47) + "...";
                    }
                    b.DrawText(new DL.TextRun(x + 4, currentY, $"{param.Key}: {value}", new DL.Rgb24(180, 180, 180), null, DL.CellAttrFlags.None));
                    currentY++;
                    linesDrawn++;
                }
                paramLine++;
            }
        }
        
        // Output
        if (_outputLines.Any() && linesDrawn < maxLines)
        {
            var outputStartLine = 3 + _parameters.Count;
            
            if (startLine <= outputStartLine)
            {
                b.DrawText(new DL.TextRun(x + 2, currentY, "Output:", new DL.Rgb24(100, 200, 100), null, DL.CellAttrFlags.None));
                currentY++;
                linesDrawn++;
            }
            
            // Draw output lines
            var linesToShow = Math.Min(_outputLines.Count, _maxDisplayLines);
            for (int i = 0; i < linesToShow && linesDrawn < maxLines; i++)
            {
                var lineNum = outputStartLine + 1 + i;
                if (startLine <= lineNum)
                {
                    var outputLine = _outputLines[i];
                    if (outputLine.Length > width - 6)
                    {
                        outputLine = outputLine.Substring(0, width - 9) + "...";
                    }
                    b.DrawText(new DL.TextRun(x + 4, currentY, outputLine, new DL.Rgb24(150, 150, 150), null, DL.CellAttrFlags.None));
                    currentY++;
                    linesDrawn++;
                }
            }
            
            // Truncation message
            if (_outputLines.Count > _maxDisplayLines && linesDrawn < maxLines)
            {
                var truncLine = outputStartLine + 1 + linesToShow;
                if (startLine <= truncLine)
                {
                    var truncMsg = $"... {_outputLines.Count - _maxDisplayLines} more lines ...";
                    b.DrawText(new DL.TextRun(x + 4, currentY, truncMsg, new DL.Rgb24(100, 100, 120), null, DL.CellAttrFlags.Italic));
                    currentY++;
                    linesDrawn++;
                }
            }
        }
        
        // Error
        if (!string.IsNullOrEmpty(_error) && linesDrawn < maxLines)
        {
            b.DrawText(new DL.TextRun(x + 2, currentY, "Error:", new DL.Rgb24(255, 100, 100), null, DL.CellAttrFlags.Bold));
            currentY++;
            linesDrawn++;
            
            if (linesDrawn < maxLines)
            {
                var errorMsg = _error;
                if (errorMsg.Length > width - 6)
                {
                    errorMsg = errorMsg.Substring(0, width - 9) + "...";
                }
                b.DrawText(new DL.TextRun(x + 4, currentY, errorMsg, new DL.Rgb24(255, 150, 150), null, DL.CellAttrFlags.None));
            }
        }
    }
}