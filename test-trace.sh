#!/bin/bash

# Test the tracing system with a simple query
echo "Testing Andy CLI tracing system..."
echo ""

# Clean up old trace files (optional)
rm -f /tmp/andy-trace-*.json 2>/dev/null

# Run Andy with tracing enabled and send a test query
export ANDY_TRACE=1
export ANDY_TRACE_CONSOLE=1

echo "What time is it?" | dotnet run --project src/Andy.Cli

echo ""
echo "Test complete. Checking trace file..."

# Find the trace file that was created
TRACE_FILE=$(ls -t /tmp/andy-trace-*.json 2>/dev/null | head -1)

if [ -z "$TRACE_FILE" ]; then
    echo "ERROR: No trace file was created!"
    exit 1
fi

echo "Trace file created: $TRACE_FILE"
echo "File size: $(ls -lh "$TRACE_FILE" | awk '{print $5}')"
echo ""
echo "First few trace entries:"
head -5 "$TRACE_FILE" | jq -r '.type' 2>/dev/null || head -5 "$TRACE_FILE"