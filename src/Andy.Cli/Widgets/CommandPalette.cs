using System;
using System.Collections.Generic;
using System.Linq;
using DL = Andy.Tui.DisplayList;
using L = Andy.Tui.Layout;

namespace Andy.Cli.Widgets;

public class CommandPalette
{
    private List<CommandItem> _commands = new();
    private List<CommandItem> _filteredCommands = new();
    private string _query = "";
    private int _selectedIndex = 0;
    private bool _isOpen = false;
    private bool _waitingForParams = false;
    private CommandItem? _selectedCommand = null;
    private string _paramInput = "";
    private Func<string[]>? _availableOptionsProvider = null;
    private int _optionsScrollOffset = 0;
    private int _selectedOptionIndex = -1;

    public class CommandItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public Action<string[]>? Action { get; set; }
        public string[] Aliases { get; set; } = Array.Empty<string>();
        public string[] RequiredParams { get; set; } = Array.Empty<string>();
        public string ParameterHint { get; set; } = "";
        public Func<string[]>? GetAvailableOptions { get; set; }
    }

    public void SetCommands(IEnumerable<CommandItem> commands)
    {
        _commands = commands.ToList();
        UpdateFilteredCommands();
    }

    public void Open()
    {
        _isOpen = true;
        _query = "";
        _selectedIndex = 0;
        _waitingForParams = false;
        _selectedCommand = null;
        _paramInput = "";
        _optionsScrollOffset = 0;
        _selectedOptionIndex = -1;
        UpdateFilteredCommands();
    }

    public void Close()
    {
        _isOpen = false;
        _query = "";
    }

    public bool IsOpen => _isOpen;

    public void SetQuery(string query)
    {
        _query = query;
        _selectedIndex = 0;
        UpdateFilteredCommands();
    }

    public string GetQuery() => _waitingForParams ? _paramInput : _query;

    public void SetParamInput(string input)
    {
        _paramInput = input;
    }

    public bool IsWaitingForParams() => _waitingForParams;

    public void MoveSelection(int delta)
    {
        if (_waitingForParams && _availableOptionsProvider != null)
        {
            // Move through available options when in parameter mode
            var options = _availableOptionsProvider();
            if (options != null && options.Length > 0)
            {
                _selectedOptionIndex = Math.Clamp(_selectedOptionIndex + delta, -1, options.Length - 1);

                // Adjust scroll offset if needed
                int maxVisibleOptions = 10; // Show up to 10 options at once
                if (_selectedOptionIndex >= _optionsScrollOffset + maxVisibleOptions)
                {
                    _optionsScrollOffset = _selectedOptionIndex - maxVisibleOptions + 1;
                }
                else if (_selectedOptionIndex >= 0 && _selectedOptionIndex < _optionsScrollOffset)
                {
                    _optionsScrollOffset = _selectedOptionIndex;
                }
            }
        }
        else if (_filteredCommands.Count > 0)
        {
            _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _filteredCommands.Count - 1);
        }
    }

    public CommandItem? GetSelected()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _filteredCommands.Count)
            return _filteredCommands[_selectedIndex];
        return null;
    }

    public void ExecuteSelected(string args = "")
    {
        if (_waitingForParams && _selectedCommand != null)
        {
            // If an option is selected, use it as the parameter
            string paramToUse = _paramInput;
            if (_selectedOptionIndex >= 0 && _availableOptionsProvider != null)
            {
                var options = _availableOptionsProvider();
                if (options != null && _selectedOptionIndex < options.Length)
                {
                    // Extract just the tool ID from "tool_id - Tool Name" format
                    var selectedOption = options[_selectedOptionIndex];
                    var dashIndex = selectedOption.IndexOf(" - ");
                    paramToUse = dashIndex > 0 ? selectedOption.Substring(0, dashIndex) : selectedOption;
                }
            }

            // Execute with the parameters entered
            var argArray = string.IsNullOrWhiteSpace(paramToUse)
                ? Array.Empty<string>()
                : paramToUse.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _selectedCommand.Action?.Invoke(argArray);
            Close();
        }
        else
        {
            var selected = GetSelected();
            if (selected?.Action != null)
            {
                // Check if command needs parameters
                if (selected.RequiredParams.Length > 0)
                {
                    // Switch to parameter input mode
                    _waitingForParams = true;
                    _selectedCommand = selected;
                    _paramInput = "";
                    _availableOptionsProvider = selected.GetAvailableOptions;
                    _optionsScrollOffset = 0;
                    _selectedOptionIndex = -1;
                }
                else
                {
                    // Execute immediately if no params needed
                    var argArray = string.IsNullOrWhiteSpace(args)
                        ? Array.Empty<string>()
                        : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    selected.Action(argArray);
                    Close();
                }
            }
        }
    }

    private void UpdateFilteredCommands()
    {
        if (string.IsNullOrEmpty(_query))
        {
            _filteredCommands = _commands.ToList();
        }
        else
        {
            var lowerQuery = _query.ToLower();
            _filteredCommands = _commands
                .Where(c =>
                    c.Name.ToLower().Contains(lowerQuery) ||
                    c.Description.ToLower().Contains(lowerQuery) ||
                    c.Category.ToLower().Contains(lowerQuery) ||
                    c.Aliases.Any(a => a.ToLower().Contains(lowerQuery)))
                .OrderBy(c =>
                {
                    // Prioritize exact matches
                    if (c.Name.Equals(_query, StringComparison.OrdinalIgnoreCase))
                        return 0;
                    if (c.Aliases.Any(a => a.Equals(_query, StringComparison.OrdinalIgnoreCase)))
                        return 1;
                    // Then prefix matches
                    if (c.Name.StartsWith(_query, StringComparison.OrdinalIgnoreCase))
                        return 2;
                    if (c.Aliases.Any(a => a.StartsWith(_query, StringComparison.OrdinalIgnoreCase)))
                        return 3;
                    // Then contains
                    return 4;
                })
                .ToList();
        }
    }

    public void Render(L.Rect viewport, DL.DisplayList baseDl, DL.DisplayListBuilder wb)
    {
        if (!_isOpen) return;

        // Calculate palette dimensions
        int width = (int)Math.Min(80, viewport.Width - 10);
        int maxHeight = (int)Math.Min(20, viewport.Height - 6);
        // Ensure minimum height for at least one command + header
        int minHeight = 5; // title + search + separator + 1 command + hints

        // Calculate height based on mode
        int height;
        if (_waitingForParams)
        {
            // In parameter mode, we need more space for options
            // Get available options to calculate needed height
            var availableOptions = _availableOptionsProvider?.Invoke()?.Length ?? 0;
            int neededHeight = Math.Min(availableOptions + 7, maxHeight); // +7 for UI elements
            height = (int)Math.Max(15, neededHeight); // At least 15 lines for parameter mode
        }
        else
        {
            height = (int)Math.Max(minHeight, Math.Min(maxHeight, _filteredCommands.Count + 4)); // +4 for UI elements
        }

        int x = (int)((viewport.Width - width) / 2);
        int y = (int)Math.Max(2, (viewport.Height - height) / 3); // Position in upper third

        // Draw backdrop (semi-transparent overlay effect)
        wb.PushClip(new DL.ClipPush(0, 0, (int)viewport.Width, (int)viewport.Height));

        // Draw main palette window
        wb.PushClip(new DL.ClipPush(x, y, width, height));
        wb.DrawRect(new DL.Rect(x, y, width, height, new DL.Rgb24(20, 20, 30)));
        wb.DrawBorder(new DL.Border(x, y, width, height, "rounded", new DL.Rgb24(100, 100, 200)));

        // Draw title
        string title = " Command Palette ";
        int titleX = x + (width - title.Length) / 2;
        wb.DrawText(new DL.TextRun(titleX, y, title, new DL.Rgb24(200, 200, 255), new DL.Rgb24(20, 20, 30), DL.CellAttrFlags.Bold));

        // Draw search/parameter input box
        int searchY = y + 1;
        if (_waitingForParams && _selectedCommand != null)
        {
            // Show parameter input mode
            string prompt = $"Enter {_selectedCommand.Name} parameters: ";
            wb.DrawText(new DL.TextRun(x + 2, searchY, prompt, new DL.Rgb24(150, 200, 150), null, DL.CellAttrFlags.None));
            wb.DrawText(new DL.TextRun(x + 2 + prompt.Length, searchY, _paramInput, new DL.Rgb24(255, 255, 255), null, DL.CellAttrFlags.None));

            // Draw cursor
            int cursorX = x + 2 + prompt.Length + _paramInput.Length;
            wb.DrawText(new DL.TextRun(cursorX, searchY, "│", new DL.Rgb24(255, 255, 100), null, DL.CellAttrFlags.None));

            // Show parameter hint
            if (!string.IsNullOrEmpty(_selectedCommand.ParameterHint))
            {
                wb.DrawText(new DL.TextRun(x + 2, searchY + 1, _selectedCommand.ParameterHint, new DL.Rgb24(100, 150, 100), null, DL.CellAttrFlags.Italic));
            }
        }
        else
        {
            // Normal search mode
            wb.DrawText(new DL.TextRun(x + 2, searchY, "> ", new DL.Rgb24(150, 150, 200), null, DL.CellAttrFlags.None));
            wb.DrawText(new DL.TextRun(x + 4, searchY, _query, new DL.Rgb24(255, 255, 255), null, DL.CellAttrFlags.None));

            // Draw cursor in search box
            int cursorX = x + 4 + _query.Length;
            wb.DrawText(new DL.TextRun(cursorX, searchY, "│", new DL.Rgb24(255, 255, 100), null, DL.CellAttrFlags.None));
        }

        // Draw separator
        wb.DrawText(new DL.TextRun(x + 1, searchY + 1, new string('─', width - 2), new DL.Rgb24(60, 60, 80), null, DL.CellAttrFlags.None));

        // Draw filtered commands or available options
        if (_waitingForParams)
        {
            // Show available options if provider is available
            if (_availableOptionsProvider != null)
            {
                var options = _availableOptionsProvider();
                if (options != null && options.Length > 0)
                {
                    // Draw available options section
                    int optionsY = searchY + 3;
                    wb.DrawText(new DL.TextRun(x + 2, optionsY, "Available options:", new DL.Rgb24(150, 200, 150), null, DL.CellAttrFlags.Bold));

                    // Filter options based on current input
                    var filteredOptions = string.IsNullOrEmpty(_paramInput)
                        ? options
                        : options.Where(o => o.Contains(_paramInput, StringComparison.OrdinalIgnoreCase)).ToArray();

                    // Calculate visible range with scrolling
                    int maxVisibleOptions = Math.Min(height - 8, 10); // Show up to 10 options at once
                    int startIndex = _optionsScrollOffset;
                    int endIndex = Math.Min(startIndex + maxVisibleOptions, filteredOptions.Length);

                    // Show scroll indicators
                    if (startIndex > 0)
                    {
                        wb.DrawText(new DL.TextRun(x + 4, optionsY + 1, $"▲ {startIndex} more above", new DL.Rgb24(120, 120, 150), null, DL.CellAttrFlags.Italic));
                        optionsY++;
                    }

                    int optionLine = optionsY + 1;
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        if (optionLine >= y + height - 3) break; // Leave space for hints and scroll indicator

                        bool isSelected = i == _selectedOptionIndex;
                        var bgColor = isSelected ? new DL.Rgb24(40, 40, 80) : (DL.Rgb24?)null;
                        var fgColor = isSelected ? new DL.Rgb24(255, 255, 255) : new DL.Rgb24(180, 180, 200);

                        if (isSelected)
                        {
                            wb.DrawRect(new DL.Rect(x + 4, optionLine, width - 8, 1, bgColor!.Value));
                        }

                        string prefix = isSelected ? "▸ " : "• ";
                        wb.DrawText(new DL.TextRun(x + 4, optionLine, prefix + filteredOptions[i], fgColor, bgColor, isSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
                        optionLine++;
                    }

                    if (endIndex < filteredOptions.Length)
                    {
                        wb.DrawText(new DL.TextRun(x + 4, optionLine, $"▼ {filteredOptions.Length - endIndex} more below", new DL.Rgb24(120, 120, 150), null, DL.CellAttrFlags.Italic));
                    }

                    // Show total count
                    string countText = $"[{filteredOptions.Length} options]";
                    int countX = x + width - countText.Length - 2;
                    wb.DrawText(new DL.TextRun(countX, optionsY, countText, new DL.Rgb24(100, 150, 100), null, DL.CellAttrFlags.None));
                }
            }
            return;
        }

        int listY = searchY + 2;
        // Calculate visible items: height - (title line + search line + separator + hints line)
        int availableLines = Math.Max(1, height - 4);
        int visibleItems = Math.Min(_filteredCommands.Count, availableLines);

        if (_filteredCommands.Count == 0)
        {
            wb.DrawText(new DL.TextRun(x + 2, listY, "No matching commands", new DL.Rgb24(100, 100, 100), null, DL.CellAttrFlags.Italic));
        }
        else
        {
            for (int i = 0; i < visibleItems; i++)
            {
                var cmd = _filteredCommands[i];
                bool isSelected = i == _selectedIndex;

                var bgColor = isSelected ? new DL.Rgb24(40, 40, 80) : (DL.Rgb24?)null;
                var fgColor = isSelected ? new DL.Rgb24(255, 255, 255) : new DL.Rgb24(200, 200, 200);

                // Draw selection background
                if (isSelected)
                {
                    wb.DrawRect(new DL.Rect(x + 1, listY + i, width - 2, 1, bgColor!.Value));
                }

                // Draw command name with parameter hint
                string prefix = isSelected ? "▸ " : "  ";
                string cmdText = cmd.Name;
                if (cmd.RequiredParams.Length > 0)
                {
                    cmdText += " " + string.Join(" ", cmd.RequiredParams.Select(p => $"<{p}>"));
                }
                wb.DrawText(new DL.TextRun(x + 2, listY + i, prefix + cmdText, fgColor, bgColor, isSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));

                // Draw category/description on the right
                if (!string.IsNullOrEmpty(cmd.Category))
                {
                    string category = $"[{cmd.Category}]";
                    int categoryX = x + width - category.Length - 2;
                    wb.DrawText(new DL.TextRun(categoryX, listY + i, category, new DL.Rgb24(150, 150, 180), bgColor, DL.CellAttrFlags.None));
                }
            }
        }

        // Draw hints at bottom
        int hintsY = y + height - 1;
        string hints = _waitingForParams
            ? " ↑↓ Select Option  Enter Confirm  Esc Cancel "
            : " ↑↓ Navigate  Enter Select  Esc Close ";
        int hintsX = x + (width - hints.Length) / 2;
        wb.DrawText(new DL.TextRun(hintsX, hintsY, hints, new DL.Rgb24(120, 120, 150), new DL.Rgb24(20, 20, 30), DL.CellAttrFlags.None));

        wb.Pop(); // ClipPush for palette
        wb.Pop(); // ClipPush for backdrop
    }
}