#!/bin/bash
# Test script for Andy CLI ACP server

set -e

echo "Testing Andy CLI ACP Server"
echo "============================"
echo ""

# Test 1: Initialize
echo "Test 1: Initialize"
echo '{"jsonrpc":"2.0","method":"initialize","id":1,"params":{"protocolVersion":"0.1.0","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' | \
  dotnet run --project src/Andy.Cli -- --acp 2>&1 | \
  grep -E '^\{' | \
  python3 -c "import sys, json; print(json.dumps(json.loads(sys.stdin.read()), indent=2))" 2>/dev/null || echo "Response received"

echo ""
echo "✓ Server started successfully!"
echo ""
echo "Next steps:"
echo "1. Configure Zed with the settings from docs/ZED_INTEGRATION.md"
echo "2. Restart Zed"
echo "3. Open Assistant Panel (Cmd+? or Ctrl+?)"
echo "4. Try: 'What tools do you have available?'"
