#!/bin/bash

# View Andy CLI trace files in a readable format
# Usage: ./view-trace.sh [trace-file]
#
# If no file is specified, shows the most recent trace file

if [ -z "$1" ]; then
    # Find the most recent trace file
    TRACE_FILE=$(ls -t /tmp/andy-trace-*.json 2>/dev/null | head -1)
    
    if [ -z "$TRACE_FILE" ]; then
        echo "No trace files found in /tmp/"
        echo "Run Andy CLI with ANDY_TRACE=1 to generate trace files"
        exit 1
    fi
    
    echo "Viewing most recent trace: $TRACE_FILE"
else
    TRACE_FILE="$1"
    
    if [ ! -f "$TRACE_FILE" ]; then
        echo "File not found: $TRACE_FILE"
        exit 1
    fi
fi

echo ""
echo "=== ANDY CLI TRACE VIEWER ==="
echo "File: $TRACE_FILE"
echo "Size: $(ls -lh "$TRACE_FILE" | awk '{print $5}')"
echo "============================="
echo ""

# Use jq if available for pretty printing
if command -v jq &> /dev/null; then
    # Parse and display trace entries with color
    cat "$TRACE_FILE" | while IFS= read -r line; do
        TYPE=$(echo "$line" | jq -r '.type // "unknown"')
        SEQ=$(echo "$line" | jq -r '.seq // ""')
        TIMESTAMP=$(echo "$line" | jq -r '.timestamp // ""' | cut -d'T' -f2 | cut -d'.' -f1)
        
        case "$TYPE" in
            "trace_start")
                echo -e "\033[1;32m[START]\033[0m Trace started at $TIMESTAMP"
                ;;
            "user_message")
                MSG=$(echo "$line" | jq -r '.message // ""' | head -c 100)
                echo -e "\033[1;36m[$SEQ USER]\033[0m $MSG..."
                ;;
            "llm_request")
                MODEL=$(echo "$line" | jq -r '.model // "unknown"')
                MSG_COUNT=$(echo "$line" | jq -r '.message_count // 0')
                echo -e "\033[1;33m[$SEQ LLM_REQ]\033[0m Model: $MODEL, Messages: $MSG_COUNT"
                ;;
            "llm_response")
                ELAPSED=$(echo "$line" | jq -r '.elapsed_ms // 0')
                LENGTH=$(echo "$line" | jq -r '.content_length // 0')
                echo -e "\033[1;32m[$SEQ LLM_RESP]\033[0m ${LENGTH} chars in ${ELAPSED}ms"
                ;;
            "tool_calls_extracted")
                COUNT=$(echo "$line" | jq -r '.count // 0')
                TOOLS=$(echo "$line" | jq -r '.tools[]?.tool_id // ""' | tr '\n' ' ')
                echo -e "\033[1;35m[$SEQ TOOLS]\033[0m Found $COUNT tools: $TOOLS"
                ;;
            "tool_execution")
                TOOL=$(echo "$line" | jq -r '.tool_id // "unknown"')
                SUCCESS=$(echo "$line" | jq -r '.success // false')
                ELAPSED=$(echo "$line" | jq -r '.elapsed_ms // 0')
                if [ "$SUCCESS" = "true" ]; then
                    echo -e "\033[1;34m[$SEQ EXEC]\033[0m $TOOL ✓ (${ELAPSED}ms)"
                else
                    echo -e "\033[1;31m[$SEQ EXEC]\033[0m $TOOL ✗ (${ELAPSED}ms)"
                fi
                ;;
            "iteration")
                ITER=$(echo "$line" | jq -r '.iteration // 0')
                MAX=$(echo "$line" | jq -r '.max_iterations // 0')
                echo -e "\033[0;90m[$SEQ ITER]\033[0m Iteration $ITER/$MAX"
                ;;
            "error")
                CONTEXT=$(echo "$line" | jq -r '.context // ""')
                MSG=$(echo "$line" | jq -r '.message // ""' | head -c 100)
                echo -e "\033[1;31m[$SEQ ERROR]\033[0m $CONTEXT: $MSG..."
                ;;
            "context_stats")
                MSGS=$(echo "$line" | jq -r '.message_count // 0')
                TOKENS=$(echo "$line" | jq -r '.token_estimate // 0')
                TOOLS=$(echo "$line" | jq -r '.tool_call_count // 0')
                echo -e "\033[0;90m[$SEQ STATS]\033[0m Msgs: $MSGS, Tokens: ~$TOKENS, Tools: $TOOLS"
                ;;
            "trace_end")
                TOTAL=$(echo "$line" | jq -r '.total_entries // 0')
                echo -e "\033[1;32m[END]\033[0m Trace complete. Total entries: $TOTAL"
                ;;
        esac
    done
else
    # Fallback to basic display without jq
    echo "Install 'jq' for better trace viewing: brew install jq"
    echo ""
    cat "$TRACE_FILE" | while IFS= read -r line; do
        echo "$line" | head -c 200
        echo "..."
    done
fi

echo ""
echo "============================="
echo "To view raw JSON: cat $TRACE_FILE | jq"