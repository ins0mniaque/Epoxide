namespace Epoxide.Disposables;

/// <summary>
/// Represents a disposable resource whose underlying disposable resource can be replaced by another disposable resource, causing automatic disposal of the previous underlying disposable resource.
/// </summary>
public sealed class SerialDisposable : IDisposable
{
    private Value _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="SerialDisposable"/> class.
    /// </summary>
    public SerialDisposable()
    {
    }

    /// <summary>
    /// Gets a value that indicates whether the object is disposed.
    /// </summary>
    public bool IsDisposed => _current.IsDisposed;

    /// <summary>
    /// Gets or sets the underlying disposable.
    /// </summary>
    /// <remarks>If the SerialDisposable has already been disposed, assignment to this property causes immediate disposal of the given disposable object. Assigning this property disposes the previous disposable object.</remarks>
    public IDisposable? Disposable
    {
        get => _current.Disposable;
        set => _current.Disposable = value;
    }

    /// <summary>
    /// Disposes the underlying disposable as well as all future replacements.
    /// </summary>
    public void Dispose()
    {
        _current.Dispose();
    }

    private struct Value : IDisposable
    {
        private static readonly Disposed disposed = new Disposed();

        private IDisposable? _current;

        public bool IsDisposed => Volatile.Read(ref _current) == disposed;

        public IDisposable? Disposable
        {
            get
            {
                var current = Volatile.Read(ref _current);

                return current == disposed ? null : current;
            }
            set
            {
                var copy = Volatile.Read(ref _current);
                for (; ; )
                {
                    if (copy == disposed)
                    {
                        value?.Dispose();
                        return;
                    }

                    var current = Interlocked.CompareExchange(ref _current, value, copy);
                    if (current == copy)
                    {
                        copy?.Dispose();
                        return;
                    }

                    copy = current;
                }
            }
        }

        public void Dispose()
        {
            var old = Interlocked.Exchange(ref _current, disposed);

            if (old != disposed)
            {
                old?.Dispose();
            }
        }

        private sealed class Disposed : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}