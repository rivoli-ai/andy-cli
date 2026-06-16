using System;
using System.Threading;

namespace Andy.Cli.Widgets
{
    /// <summary>
    /// Thread-safe holder for the live metrics of the in-flight conversation turn.
    /// The agent turn runs on a background task and the tool-call callback fires on the
    /// engine thread, while the ~60fps render loop reads these values every frame; all
    /// access therefore goes through <see cref="Interlocked"/> / volatile reads so the
    /// thinking row can update smoothly without tearing or locks.
    /// </summary>
    public sealed class TurnStats
    {
        private long _startTicks;
        private int _operations;
        private int _inputTokens;
        private int _outputTokens;
        private volatile bool _active;

        /// <summary>True while a turn is being processed.</summary>
        public bool IsActive => _active;

        /// <summary>Tool calls issued so far in the current turn.</summary>
        public int Operations => Volatile.Read(ref _operations);

        /// <summary>Estimated input/context tokens sent for the current turn.</summary>
        public int InputTokens => Volatile.Read(ref _inputTokens);

        /// <summary>Estimated output tokens produced for the current turn.</summary>
        public int OutputTokens => Volatile.Read(ref _outputTokens);

        /// <summary>Elapsed time since the turn began, or <see cref="TimeSpan.Zero"/> when idle.</summary>
        public TimeSpan Elapsed
        {
            get
            {
                long start = Interlocked.Read(ref _startTicks);
                if (start == 0) return TimeSpan.Zero;
                long now = DateTime.UtcNow.Ticks;
                return now > start ? TimeSpan.FromTicks(now - start) : TimeSpan.Zero;
            }
        }

        /// <summary>Reset all counters and start the turn clock.</summary>
        public void Begin(DateTime startUtc)
        {
            Interlocked.Exchange(ref _startTicks, startUtc.Ticks);
            Interlocked.Exchange(ref _operations, 0);
            Interlocked.Exchange(ref _inputTokens, 0);
            Interlocked.Exchange(ref _outputTokens, 0);
            _active = true;
        }

        /// <summary>Mark the turn as finished; counters retain their final values for display.</summary>
        public void End() => _active = false;

        /// <summary>Increment the operations (tool-call) counter; returns the new value.</summary>
        public int IncrementOperations() => Interlocked.Increment(ref _operations);

        /// <summary>Set the current input/context token estimate.</summary>
        public void SetInputTokens(int tokens) => Interlocked.Exchange(ref _inputTokens, tokens);

        /// <summary>Set the current output token count (replacing any prior value).</summary>
        public void SetOutputTokens(int tokens) => Interlocked.Exchange(ref _outputTokens, tokens);

        /// <summary>Accumulate output tokens produced by an additional round-trip in this turn.</summary>
        public void AddOutputTokens(int tokens) => Interlocked.Add(ref _outputTokens, tokens);
    }
}
