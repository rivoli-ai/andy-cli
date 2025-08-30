#!/bin/bash

echo "Testing Andy CLI Model Management"
echo "=================================="
echo ""
echo "Running: dotnet run --project src/Andy.Cli -- /model list"
echo ""

# Run the command and capture output
timeout 2 bash -c 'echo "/model list" | dotnet run --project src/Andy.Cli/Andy.Cli.csproj 2>/dev/null' | head -50

echo ""
echo "Note: The command runs in the CLI environment. You can also:"
echo "  1. Run the CLI: dotnet run --project src/Andy.Cli"
echo "  2. Press Ctrl+P to open the command palette"
echo "  3. Type '/model list' in the prompt"
echo "  4. Use '/model switch <provider>' to change providers"