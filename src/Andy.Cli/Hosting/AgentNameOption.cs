namespace Andy.Cli.Hosting;

/// <summary>Shared naming option for interactive, headless, one-shot and ACP entry points.</summary>
public static class AgentNameOption
{
    public static (string[] Args, string? Name) Extract(string[] args)
    {
        var remaining = new List<string>();
        string? name = null;
        bool seen = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { remaining.AddRange(args.Skip(i)); break; }
            if (args[i] != "--agent-name" && !args[i].StartsWith("--agent-name=", StringComparison.Ordinal))
            { remaining.Add(args[i]); continue; }
            if (seen) throw new ArgumentException("--agent-name may only be supplied once.");
            seen = true;
            if (args[i] == "--agent-name")
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--agent-name requires a name string (use an empty string to clear it).");
                name = args[i];
            }
            else name = args[i]["--agent-name=".Length..];
            if (name.Length > 256 || name.Any(char.IsControl))
                throw new ArgumentException("Agent name must be at most 256 characters without control characters.");
            name = name.Trim();
        }
        return (remaining.ToArray(), name);
    }
}
