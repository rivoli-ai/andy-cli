namespace Andy.Cli.Services;

/// <summary>Owns the current turn token and serializes cancellation with scope disposal.</summary>
public sealed class ActiveTurnCancellation : IDisposable
{
    private readonly object _sync = new();
    private Scope? _current;
    private bool _disposed;

    public Scope Begin()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current != null) throw new InvalidOperationException("A turn is already active.");
            return _current = new Scope(this);
        }
    }

    public bool TryCancel()
    {
        lock (_sync)
        {
            if (_current == null) return false;
            _current.Source.Cancel();
            return true;
        }
    }

    private void End(Scope scope)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, scope)) return;
            _current = null;
            scope.Source.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_current is { } scope)
            {
                scope.Source.Cancel();
                End(scope);
            }
        }
    }

    public sealed class Scope : IDisposable
    {
        private readonly ActiveTurnCancellation _owner;
        internal CancellationTokenSource Source { get; } = new();
        public CancellationToken Token { get; }
        internal Scope(ActiveTurnCancellation owner) { _owner = owner; Token = Source.Token; }
        public void Dispose() => _owner.End(this);
    }
}
