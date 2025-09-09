#!/bin/bash

# Test script to check if the LLM is hallucinating file contents

echo "Testing file hallucination issue..."
echo ""
echo "Asking about a file that doesn't exist: src/Andy.Cli/Chat/Chat.cs"
echo ""
echo "ls src/Andy.Cli/Chat/Chat.cs" | ANDY_DEBUG_RAW=true dotnet run --project src/Andy.Cli -- --provider cerebras --model qwen-3-coder-480b 2>&1 | tee test_output.log

echo ""
echo "Output saved to test_output.log"