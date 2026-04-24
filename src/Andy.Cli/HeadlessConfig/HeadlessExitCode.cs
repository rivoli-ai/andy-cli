namespace Andy.Cli.HeadlessConfig;

// Structured process exit codes for `andy-cli run --headless`, per the AQ2
// contract (rivoli-ai/andy-cli#47). Consumers in andy-containers (Epic AP
// configurator + captor) key off these values to decide retry/cancel/
// report semantics — changing a mapping is a breaking cross-repo change.
public enum HeadlessExitCode
{
    Success = 0,
    AgentFailure = 1,
    ConfigError = 2,
    Cancelled = 3,
    Timeout = 4,
    InternalError = 5,
}
