using System;

namespace Andy.Cli.Services;

/// <summary>
/// Centralized error policy for controlling exception propagation.
/// When strict mode is enabled, caught exceptions are rethrown after logging.
/// Enable by setting ANDY_STRICT_ERRORS=1 (default: enabled).
/// Disable with ANDY_STRICT_ERRORS=0 if needed for tests.
/// </summary>
public static class ErrorPolicy
{
    private static bool? _strict;

    public static bool IsStrict
    {
        get
        {
            if (_strict.HasValue) return _strict.Value;
            var env = Environment.GetEnvironmentVariable("ANDY_STRICT_ERRORS");
            // Default to strict if not specified
            _strict = string.IsNullOrEmpty(env) ? true : env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase);
            return _strict.Value;
        }
        set { _strict = value; }
    }

    public static void RethrowIfStrict(Exception ex)
    {
        if (IsStrict) throw ex;
    }
} 