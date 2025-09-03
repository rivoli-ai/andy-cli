#!/bin/bash
# Test script for andy-cli with optimized prompt

cd src/Andy.Cli

# Create a simple expect script to interact with the CLI
cat > test_interaction.exp << 'EOF'
#!/usr/bin/expect -f

set timeout 10
spawn dotnet run

# Wait for the prompt
expect "What would you like to explore today?"

# Send a simple hello message
send "hello\r"

# Wait for response or error
expect {
    "LLM Error" {
        puts "\n=== ERROR DETECTED ==="
        expect "Bad Request" { puts "Still getting 400 Bad Request error" }
        expect -re ".*" { puts $expect_out(buffer) }
    }
    "Hello" {
        puts "\n=== SUCCESS ==="
        puts "LLM responded successfully!"
    }
    timeout {
        puts "\n=== TIMEOUT ==="
        puts "No response within 10 seconds"
    }
}

# Send exit command
send "\033"
expect eof
EOF

chmod +x test_interaction.exp

# Check if expect is installed
if command -v expect &> /dev/null; then
    ./test_interaction.exp
else
    echo "expect not installed, trying direct run with timeout..."
    timeout 10 dotnet run <<< $'hello\n' 2>&1 || true
fi

rm -f test_interaction.exp