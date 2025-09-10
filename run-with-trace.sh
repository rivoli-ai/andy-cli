#!/bin/bash

# Run Andy CLI with tracing enabled
# Usage: ./run-with-trace.sh [console]
#
# Options:
#   console - Also output trace to console
#
# Environment variables:
#   ANDY_TRACE=1 - Enable tracing to file
#   ANDY_TRACE_CONSOLE=1 - Also output to console
#   ANDY_TRACE_PATH=/path/to/trace.json - Custom trace file path

echo "Starting Andy CLI with tracing enabled..."

# Set trace environment variable
export ANDY_TRACE=1

# Check if console output is requested
if [ "$1" == "console" ]; then
    export ANDY_TRACE_CONSOLE=1
    echo "Console tracing enabled"
fi

# Optional: Set custom trace path
# export ANDY_TRACE_PATH="/tmp/andy-trace-custom.json"

# Build first to ensure latest changes
echo "Building project..."
dotnet build --quiet

# Run Andy CLI
echo "Starting Andy CLI with tracing..."
echo "Trace files will be saved to: /tmp/andy-trace-*.json"
echo ""

cd src/Andy.Cli
dotnet run

# After exit, show trace file location
echo ""
echo "Session ended. Trace files:"
ls -la /tmp/andy-trace-*.json 2>/dev/null || echo "No trace files found in /tmp/"