#!/bin/bash

echo "=== Testing Tool Result Display ==="
echo

# Clean up old debug files
rm -f /tmp/tool*.txt

# Test 1: Simple datetime request
echo "Test 1: Getting current date/time"
echo "what time is it?" | timeout 10 dotnet run --project src/Andy.Cli --no-build 2>/dev/null || true
echo
sleep 2

# Check if debug files were created
if [ -f /tmp/tool_executor_debug.txt ]; then
    echo "Debug output from tool executor:"
    tail -20 /tmp/tool_executor_debug.txt | grep -E "Extracted|datetime|result" || true
fi

echo
echo "Test complete!"