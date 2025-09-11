using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Andy.Cli.Parsing;
using Andy.Cli.Parsing.Compiler;
using Andy.Cli.Services.ContentPipeline;
using Andy.Cli.Widgets;
using Andy.Llm.Models;
using Microsoft.Extensions.Logging;

namespace Andy.Cli.Services.Conversation;

// Removed unused ResponseCompiler; parsing is handled by andy-llm structured outputs.