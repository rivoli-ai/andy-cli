#!/bin/bash
# Proper ACP protocol test with Content-Length headers

cd /Users/samibengrine/Devel/rivoli-ai/andy-cli

# Create init message
INIT_MSG='{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"0.1.0","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'
INIT_LEN=${#INIT_MSG}

echo "Testing ACP server with proper Content-Length headers..."
echo ""

# Send properly formatted message
{
    echo "Content-Length: $INIT_LEN"
    echo ""
    echo -n "$INIT_MSG"
    sleep 5  # Wait for response
} | dotnet run --project src/Andy.Cli -- --acp 2>test-acp-debug.log | head -20

echo ""
echo "=== Debug Log (last 20 lines) ==="
tail -20 test-acp-debug.log
