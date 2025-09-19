#!/bin/bash

# Test script for provider detection

echo "Testing Provider Detection..."
echo "=============================="
echo ""

# Function to test with different environment setups
test_provider() {
    local desc="$1"
    shift

    echo "Test: $desc"
    echo "Environment variables:"

    # Show which env vars are set
    for var in "$@"; do
        if [[ "$var" == *"="* ]]; then
            echo "  $var"
        fi
    done

    # Run the detect command
    echo "Result:"
    env -i HOME="$HOME" PATH="$PATH" "$@" dotnet run --project src/Andy.Cli --no-build -- model detect 2>/dev/null | grep -A20 "Provider Detection"
    echo ""
    echo "---"
    echo ""
}

# Build first
echo "Building project..."
dotnet build src/Andy.Cli/Andy.Cli.csproj > /dev/null 2>&1

# Test 1: No environment variables
test_provider "No environment variables"

# Test 2: Only CEREBRAS_API_KEY
test_provider "Only CEREBRAS_API_KEY" \
    CEREBRAS_API_KEY="test-key"

# Test 3: Only OPENAI_API_KEY
test_provider "Only OPENAI_API_KEY" \
    OPENAI_API_KEY="test-key"

# Test 4: Both OPENAI and CEREBRAS (should prefer OPENAI)
test_provider "OPENAI and CEREBRAS keys" \
    OPENAI_API_KEY="test-openai" \
    CEREBRAS_API_KEY="test-cerebras"

# Test 5: Azure OpenAI (highest priority after Ollama)
test_provider "Azure OpenAI configuration" \
    AZURE_OPENAI_API_KEY="test-azure-key" \
    AZURE_OPENAI_ENDPOINT="https://test.openai.azure.com"

# Test 6: All providers (should prefer Azure)
test_provider "All providers configured" \
    AZURE_OPENAI_API_KEY="test-azure" \
    AZURE_OPENAI_ENDPOINT="https://test.openai.azure.com" \
    OPENAI_API_KEY="test-openai" \
    CEREBRAS_API_KEY="test-cerebras" \
    ANTHROPIC_API_KEY="test-anthropic"

# Test 7: Ollama configured (highest priority)
test_provider "Ollama configured" \
    OLLAMA_API_BASE="http://localhost:11434" \
    OPENAI_API_KEY="test-openai" \
    CEREBRAS_API_KEY="test-cerebras"

echo "Testing complete!"