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
    
    public class CommandItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public Action<string[]>? Action { get; set; }
        public string[] Aliases { get; set; } = Array.Empty<string>();
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
    
    public string GetQuery() => _query;
    
    public void MoveSelection(int delta)
    {
        if (_filteredCommands.Count == 0) return;
        
        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _filteredCommands.Count - 1);
    }
    
    public CommandItem? GetSelected()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _filteredCommands.Count)
            return _filteredCommands[_selectedIndex];
        return null;
    }
    
    public void ExecuteSelected(string args = "")
    {
        var selected = GetSelected();
        if (selected?.Action != null)
        {
            var argArray = string.IsNullOrWhiteSpace(args) 
                ? Array.Empty<string>() 
                : args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            selected.Action(argArray);
            Close();
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
        int height = (int)Math.Min(maxHeight, _filteredCommands.Count + 4); // +4 for borders and query
        
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
        
        // Draw search box
        int searchY = y + 2;
        wb.DrawText(new DL.TextRun(x + 2, searchY, "🔍 ", new DL.Rgb24(150, 150, 200), null, DL.CellAttrFlags.None));
        wb.DrawText(new DL.TextRun(x + 5, searchY, _query, new DL.Rgb24(255, 255, 255), null, DL.CellAttrFlags.None));
        
        // Draw cursor in search box
        if (!string.IsNullOrEmpty(_query) || true) // Always show cursor
        {
            int cursorX = x + 5 + _query.Length;
            wb.DrawText(new DL.TextRun(cursorX, searchY, "│", new DL.Rgb24(255, 255, 100), null, DL.CellAttrFlags.None));
        }
        
        // Draw separator
        wb.DrawText(new DL.TextRun(x + 1, searchY + 1, new string('─', width - 2), new DL.Rgb24(60, 60, 80), null, DL.CellAttrFlags.None));
        
        // Draw filtered commands
        int listY = searchY + 2;
        int visibleItems = Math.Min(_filteredCommands.Count, height - 5);
        
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
                
                // Draw command name
                string prefix = isSelected ? "▸ " : "  ";
                wb.DrawText(new DL.TextRun(x + 2, listY + i, prefix + cmd.Name, fgColor, bgColor, isSelected ? DL.CellAttrFlags.Bold : DL.CellAttrFlags.None));
                
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
        string hints = " ↑↓ Navigate  Enter Select  Esc Close ";
        int hintsX = x + (width - hints.Length) / 2;
        wb.DrawText(new DL.TextRun(hintsX, hintsY, hints, new DL.Rgb24(120, 120, 150), new DL.Rgb24(20, 20, 30), DL.CellAttrFlags.None));
        
        wb.Pop(); // ClipPush for palette
        wb.Pop(); // ClipPush for backdrop
    }
}