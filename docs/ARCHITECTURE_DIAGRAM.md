# Andy CLI Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                                ANDY CLI SOLUTION                                │
└─────────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────────┐
│                              PRESENTATION LAYER                                │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │   Terminal UI   │  │  Command Palette│  │   Feed View     │                │
│  │   (Andy.Tui)    │  │                 │  │                 │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │  Prompt Line    │  │  Status Message │  │  Token Counter  │                │
│  │                 │  │                 │  │                 │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
└─────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              APPLICATION LAYER                                 │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │   Program.cs    │  │ ModelCommand    │  │ ToolsCommand    │                │
│  │   (Main Entry)  │  │                 │  │                 │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │SimpleAssistant  │  │ProviderDetection│  │Instrumentation  │                │
│  │    Service      │  │    Service      │  │    Server       │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
└─────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                               SERVICE LAYER                                    │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │  Tool Registry  │  │ Tool Executor   │  │Content Pipeline │                │
│  │                 │  │                 │  │                 │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │ Context Manager │  │Output Tracker   │  │  Error Policy   │                │
│  │                 │  │                 │  │                 │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
└─────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              FRAMEWORK LAYER                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │   Andy.Engine   │  │    Andy.Llm     │  │   Andy.Tools    │                │
│  │  (AI Agent)     │  │ (LLM Providers) │  │ (Tool Framework)│                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │    Andy.Tui     │  │   Andy.Model    │  │  Andy.Cli       │                │
│  │ (Terminal UI)   │  │ (Shared Models) │  │ (CLI App)       │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
└─────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                               TOOL ECOSYSTEM                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │ File System     │  │ Text Processing │  │ System Tools    │                │
│  │ • read_file     │  │ • search_text   │  │ • bash_command  │                │
│  │ • write_file    │  │ • replace_text  │  │ • system_info   │                │
│  │ • list_directory│  │ • format_text   │  │ • process_info  │                │
│  │ • copy_file     │  │                 │  │                 │                │
│  │ • delete_file   │  │                 │  │                 │                │
│  │ • move_file     │  │                 │  │                 │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │ Development     │  │ Web & JSON       │  │ Utilities       │                │
│  │ • code_index    │  │ • http_request   │  │ • datetime_tool │                │
│  │ • git_diff      │  │ • json_processor │  │ • encoding_tool │                │
│  │ • create_dir    │  │                 │  │ • todo_mgmt     │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
└─────────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                            LLM PROVIDER LAYER                                  │
├─────────────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │     OpenAI      │  │    Cerebras     │  │   Anthropic     │                │
│  │ • GPT-4         │  │ • Llama-3.3-70b │  │ • Claude-3      │                │
│  │ • GPT-4o-mini   │  │ • Fast inference│  │ • Sonnet        │                │
│  │ • Full tools    │  │ • Limited tools │  │ • Full tools    │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐                │
│  │ Google Gemini   │  │     Ollama      │  │ Azure OpenAI    │                │
│  │ • Gemini-2.0    │  │ • Local models  │  │ • Enterprise    │                │
│  │ • Flash         │  │ • No API key    │  │ • Custom endpoint│                │
│  │ • Full tools    │  │ • Full tools    │  │ • Full tools    │                │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘                │
└─────────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────────┐
│                              DATA FLOW DIAGRAM                                 │
└─────────────────────────────────────────────────────────────────────────────────┘

User Input → Terminal UI → SimpleAssistantService → Andy.Engine.SimpleAgent
     │                                                      │
     ▼                                                      ▼
Command Palette ← Feed View ← Content Pipeline ← LLM Provider (OpenAI/Cerebras/etc)
     │                                                      │
     ▼                                                      ▼
Tool Registry → Tool Executor → Tool Implementation → File System/System/Web APIs
     │                                                      │
     ▼                                                      ▼
Context Manager ← Tool Results ← Output Tracker ← Security Manager

┌─────────────────────────────────────────────────────────────────────────────────┐
│                            CONVERSATION LOOP                                   │
└─────────────────────────────────────────────────────────────────────────────────┘

1. User Input → 2. Context Building → 3. LLM Request → 4. Response Processing
     ↑                                                                    │
     │                                                                    ▼
8. Display Result ← 7. Content Pipeline ← 6. Tool Execution ← 5. Tool Call Detection
     │                                                                    │
     └─────────────────── Context Update ←───────────────────────────────┘

Max 12 Iterations with Safety Mechanisms:
• Consecutive tool-only detection (max 3)
• Force text response after multiple tool iterations
• Fallback to no-tools mode on errors
• Cumulative output limiting (6000 chars)

┌─────────────────────────────────────────────────────────────────────────────────┐
│                              CONFIGURATION                                     │
└─────────────────────────────────────────────────────────────────────────────────┘

Environment Variables:
• OPENAI_API_KEY, CEREBRAS_API_KEY, ANTHROPIC_API_KEY
• GOOGLE_API_KEY, AZURE_OPENAI_API_KEY, AZURE_OPENAI_ENDPOINT
• OLLAMA_API_BASE, OPENAI_API_BASE
• ANDY_DEBUG, ANDY_STRICT_ERRORS, ANDY_SKIP_OLLAMA

appsettings.json:
• Provider configurations with API keys
• Default models and priorities
• Custom endpoints and settings

┌─────────────────────────────────────────────────────────────────────────────────┐
│                              MONITORING & DEBUG                                │
└─────────────────────────────────────────────────────────────────────────────────┘

Instrumentation Server (Port 5555):
• Real-time conversation flow
• Tool execution tracking
• Performance metrics
• Error monitoring
• System prompt visibility

Debug Features:
• Raw response logging
• Conversation tracing
• Message part debugging
• Tool execution traces
• Context state changes
```
