#!/bin/bash
echo "Testing Cerebras provider with limited tools..."
cd /Users/sami/devel/rivoli-ai/andy-cli

# Build the project
echo "Building the project..."
dotnet build src/Andy.Cli/Andy.Cli.csproj --nologo -v q

# Run the CLI and test model switching
echo "Testing model switch to Cerebras..."
echo -e "/model cerebras\nWhat's in the current directory?\n/exit" | dotnet run --project src/Andy.Cli/Andy.Cli.csproj --no-build 2>&1 | head -50

echo "Test complete."
