namespace Epoxide;

/// <summary>
/// Represents a group of disposable resources that are disposed together.
/// </summary>
public sealed class CompositeDisposable : IDisposable
{
    private readonly object _gate = new object();
    private bool _disposed;
    private List<IDisposable?> _disposables;
    private int _count;
    private const int ShrinkThreshold = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeDisposable"/> class with no disposables contained by it initially.
    /// </summary>
    public CompositeDisposable()
    {
        _disposables = new List<IDisposable?>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeDisposable"/> class with the specified number of disposables.
    /// </summary>
    /// <param name="capacity">The number of disposables that the new CompositeDisposable can initially store.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is less than zero.</exception>
    public CompositeDisposable(int capacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _disposables = new List<IDisposable?>(capacity);
    }

    /// <summary>
    /// Gets the number of disposables contained in the <see cref="CompositeDisposable"/>.
    /// </summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Adds a disposable to the <see cref="CompositeDisposable"/> or disposes the disposable if the <see cref="CompositeDisposable"/> is disposed.
    /// </summary>
    /// <param name="item">Disposable to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
    public void Add(IDisposable item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                _disposables.Add(item);

                // If read atomically outside the lock, it should be written atomically inside
                // the plain read on _count is fine here because manipulation always happens
                // from inside a lock.
                Volatile.Write(ref _count, _count + 1);
                return;
            }
        }

        item.Dispose();
    }

    /// <summary>
    /// Removes and disposes the first occurrence of a disposable from the <see cref="CompositeDisposable"/>.
    /// </summary>
    /// <param name="item">Disposable to remove.</param>
    /// <returns>true if found; false otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
    public bool Remove(IDisposable item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            // this composite was already disposed and if the item was in there
            // it has been already removed/disposed
            if (_disposed)
            {
                return false;
            }

            //
            // List<T> doesn't shrink the size of the underlying array but does collapse the array
            // by copying the tail one position to the left of the removal index. We don't need
            // index-based lookup but only ordering for sequential disposal. So, instead of spending
            // cycles on the Array.Copy imposed by Remove, we use a null sentinel value. We also
            // do manual Swiss cheese detection to shrink the list if there's a lot of holes in it.
            //

            // read fields as infrequently as possible
            var current = _disposables;

            var i = current.IndexOf(item);
            if (i < 0)
            {
                // not found, just return
                return false;
            }

            current[i] = null;

            if (current.Capacity > ShrinkThreshold && _count < current.Capacity / 2)
            {
                var fresh = new List<IDisposable?>(current.Capacity / 2);

                foreach (var d in current)
                {
                    if (d != null)
                    {
                        fresh.Add(d);
                    }
                }

                _disposables = fresh;
            }

            // make sure the Count property sees an atomic update
            Volatile.Write(ref _count, _count - 1);
        }

        // if we get here, the item was found and removed from the list
        // just dispose it and report success

        item.Dispose();

        return true;
    }

    /// <summary>
    /// Disposes all disposables in the group and removes them from the group.
    /// </summary>
    public void Dispose()
    {
        List<IDisposable?>? currentDisposables = null;

        lock (_gate)
        {
            if (!_disposed)
            {
                currentDisposables = _disposables;

                // nulling out the reference is faster no risk to
                // future Add/Remove because _disposed will be true
                // and thus _disposables won't be touched again.
                _disposables = null!; // NB: All accesses are guarded by _disposed checks.

                Volatile.Write(ref _count, 0);
                Volatile.Write(ref _disposed, true);
            }
        }

        if (currentDisposables != null)
        {
            foreach (var d in currentDisposables)
            {
                d?.Dispose();
            }
        }
    }

    /// <summary>
    /// Removes and disposes all disposables from the <see cref="CompositeDisposable"/>, but does not dispose the <see cref="CompositeDisposable"/>.
    /// </summary>
    public void Clear()
    {
        IDisposable?[] previousDisposables;

        lock (_gate)
        {
            // disposed composites are always clear
            if (_disposed)
            {
                return;
            }

            var current = _disposables;

            previousDisposables = current.ToArray();
            current.Clear();

            Volatile.Write(ref _count, 0);
        }

        foreach (var d in previousDisposables)
        {
            d?.Dispose();
        }
    }

    /// <summary>
    /// Determines whether the <see cref="CompositeDisposable"/> contains a specific disposable.
    /// </summary>
    /// <param name="item">Disposable to search for.</param>
    /// <returns>true if the disposable was found; otherwise, false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
    public bool Contains(IDisposable item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            return _disposables.Contains(item);
        }
    }

    /// <summary>
    /// Returns the disposables contained in the <see cref="CompositeDisposable"/> in an array.
    /// </summary>
    public IDisposable[] ToArray()
    {
        lock (_gate)
        {
            // disposed composites are always clear
            if (_disposed || _count == 0)
            {
                return Array.Empty<IDisposable>();
            }

            var current = _disposables;

            var array = new IDisposable[_count];
            var index = 0;

            foreach (var d in current)
            {
                if (d != null)
                {
                    array[index++] = d;
                }
            }

            return array;
        }
    }

    /// <summary>
    /// Gets a value that indicates whether the object is disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposed);
}