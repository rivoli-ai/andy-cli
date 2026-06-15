using System;
using System.Collections.Generic;
using System.Linq;

namespace Andy.Cli.Services
{
    /// <summary>
    /// Detects tight tool-call loops: the same (tool, arguments) signature recurring within a
    /// sliding window of recent calls. Lets <see cref="UiUpdatingToolExecutor"/> short-circuit a
    /// runaway repeat (e.g. the model calling the same query over and over making no progress)
    /// with guidance to the model instead of re-running the tool and burning tokens.
    ///
    /// Scope: catches exact-identical repeats only (low false-positive). Varied-argument
    /// "no-progress" spinning is left to the turn-limit guard. Thread-safe.
    /// </summary>
    internal sealed class ToolCallLoopDetector
    {
        private readonly int _window;
        private readonly int _threshold;
        private readonly Queue<string> _recent = new();
        private readonly object _lock = new();

        /// <param name="window">How many of the most recent calls to consider.</param>
        /// <param name="threshold">
        /// Occurrences of the same signature within the window at which a call is treated as a loop.
        /// With the default 3, the third identical call in the window is the first to be flagged.
        /// </param>
        public ToolCallLoopDetector(int window = 8, int threshold = 3)
        {
            if (window < 1) throw new ArgumentOutOfRangeException(nameof(window));
            if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
            _window = window;
            _threshold = threshold;
        }

        /// <summary>
        /// Record a call signature and report whether it has now occurred at least
        /// <c>threshold</c> times within the most recent <c>window</c> calls (i.e. this call
        /// looks like a loop and should be short-circuited).
        /// </summary>
        public bool RecordAndIsLooping(string signature)
        {
            lock (_lock)
            {
                _recent.Enqueue(signature);
                while (_recent.Count > _window) _recent.Dequeue();

                int count = 0;
                foreach (var s in _recent)
                {
                    if (s == signature) count++;
                }
                return count >= _threshold;
            }
        }

        /// <summary>Build a stable signature from a tool id and its parameters (order-independent).</summary>
        public static string Signature(string toolId, IReadOnlyDictionary<string, object?>? parameters)
        {
            if (parameters == null || parameters.Count == 0) return toolId;
            var parts = parameters
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}");
            return toolId + "|" + string.Join("&", parts);
        }
    }
}
