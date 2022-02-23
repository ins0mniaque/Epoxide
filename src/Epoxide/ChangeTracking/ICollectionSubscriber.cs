using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;

using Epoxide.Collections;
using Epoxide.Linq.Expressions;

namespace Epoxide.ChangeTracking;

public interface ICollectionSubscriber
{
    IDisposable Subscribe < T > ( IEnumerable < T > collection, CollectionChangedCallback < T > k );
    void Invalidate < T > ( IEnumerable < T > collection );
}

public interface ICollectionSubscriptionFactory
{
    CollectionSubscription< T >? Create < T >( IEnumerable < T > collection, CollectionChangedCallback< T > callback );
}

public class CollectionSubscriptionFactory : ICollectionSubscriptionFactory
{
    public CollectionSubscription< T >? Create< T > ( IEnumerable < T > collection, CollectionChangedCallback< T > callback )
    {
        return collection is INotifyCollectionChanged ? new NotifyCollectionChangedCollectionSubscription< T > ( collection, callback ) :
                                                        null;
    }
}

public enum CollectionOperation
{
    Add,
    AddRange,
    Remove,
    RemoveRange,
    Move,
    Replace,
    Clear,
    Invalidate
}

public static class CollectionChange
{
    public static IEnumerable < CollectionChange < TResult > > ChangeType < T, TResult > ( this IEnumerable < CollectionChange < T > > changes, Func < T, TResult > selector )
    {
        return changes.Select ( change => CollectionChange < T >.ChangeType ( change, selector ) );
    }

    public static void ReplicateChanges < T > ( this ICollection < T > collection, IEnumerable < CollectionChange < T > > changes, IEnumerable < T > source )
    {
        // TODO: Process changes
        if ( changes.Any ( ) )
        {
            collection.Clear ( );
            foreach ( var item in source )
                collection.Add ( item );
        }
    }

    private static MethodInfo? bindCollectionsMethod;

    public static IDisposable? BindCollections ( this ICollectionSubscriber subscriber, object collection, object? enumerable )
    {
        bindCollectionsMethod ??= new Func < ICollectionSubscriber, ICollection < object >, ICollection < object >, IDisposable? > ( BindCollections ).Method.GetGenericMethodDefinition ( );

        var elementType     = collection.GetType ( ).GetGenericInterfaceArguments ( typeof ( ICollection < > ) ) [ 0 ];
        var bindCollections = bindCollectionsMethod.MakeGenericMethod ( elementType );

        return (IDisposable?) bindCollections.Invoke ( null, new [ ] { subscriber, collection, enumerable } );
    }

    public static IDisposable? BindCollections < T > ( this ICollectionSubscriber subscriber, ICollection < T > collection, IEnumerable < T >? enumerable )
    {
        if ( enumerable == null )
        {
            collection.Clear ( );

            return null;
        }

        collection.ReplicateChanges ( Enumerable.Repeat ( CollectionChange < T >.Invalidated ( ), 1 ), enumerable );

        return subscriber.Subscribe ( enumerable, (o, e) => collection.ReplicateChanges ( Enumerable.Repeat ( e, 1 ), enumerable ) );
    }
}

public sealed class CollectionChange < T >
{
    // TODO: Move methods to CollectionChange static class
    public static CollectionChange < T > Added ( T current, int index = -1 )
    {
        return new ( CollectionOperation.Add, item: current, index: index );
    }

    public static CollectionChange < T > Added ( IEnumerable < T > current, int index = -1 )
    {
        if ( current.Count ( ) == 1 )
            return new ( CollectionOperation.Add, item: current.First ( ), index: index );

        return new ( CollectionOperation.AddRange, items: current as IReadOnlyList < T > ?? current.ToList ( ), index: index );
    }

    public static CollectionChange < T > Removed ( T current, int index = -1 )
    {
        return new ( CollectionOperation.Remove, item: current, index: index );
    }

    public static CollectionChange < T > Removed ( IEnumerable < T > current, int index = -1 )
    {
        if ( current.Count ( ) == 1 )
            return new ( CollectionOperation.Remove, item: current.First ( ), index: index );

        return new ( CollectionOperation.RemoveRange, items: current as IReadOnlyList < T > ?? current.ToList ( ), index: index );
    }
    
    public static CollectionChange < T > Moved ( T current, int index, int movedFromIndex )
    {
        if ( index          < 0 ) throw new ArgumentOutOfRangeException ( nameof ( index ),          "Index must be greater than or equal to zero" );
        if ( movedFromIndex < 0 ) throw new ArgumentOutOfRangeException ( nameof ( movedFromIndex ), "Previous index must be greater than or equal to zero" );

        return new ( CollectionOperation.Move, item: current, index: index, movedFromIndex: movedFromIndex );
    }

    public static CollectionChange < T > Replaced ( T current, int index )
    {
        if ( index < 0 ) throw new ArgumentOutOfRangeException ( nameof ( index ), "Index must be greater than or equal to zero" );

        return new ( CollectionOperation.Replace, item: current, index: index );
    }

    public static CollectionChange < T > Replaced ( T current, T previous, int currentIndex = -1 )
    {
        return new ( CollectionOperation.Replace, item: current, index: currentIndex, hasReplacedItem: true, replacedItem: previous );
    }

    public static CollectionChange < T > Cleared ( )
    {
        return new ( CollectionOperation.Clear );
    }

    public static CollectionChange < T > Invalidated ( )
    {
        return new ( CollectionOperation.Invalidate );
    }

    public static CollectionChange < TResult > ChangeType < TResult > ( CollectionChange < T > change, Func < T, TResult > selector )
    {
        return new ( change.Operation,
                     change.Items?.Select ( selector ).ToList ( ),
                     selector ( change.Item ),
                     change.Index,
                     change.HasReplacedItem,
                     change.HasReplacedItem ? selector ( change.ReplacedItem ) : default,
                     change.MovedFromIndex );
    }

    private CollectionChange ( CollectionOperation operation, IReadOnlyList < T >? items = null, T? item = default, int index = -1, bool hasReplacedItem = false, T? replacedItem = default, int movedFromIndex = -1 )
    {
        Operation       = operation;
        Items           = items;
        Item            = item;
        Index           = index;
        HasReplacedItem = hasReplacedItem;
        ReplacedItem    = replacedItem;
        MovedFromIndex  = movedFromIndex;
    }

    public CollectionOperation Operation { get; }

    public IReadOnlyList < T >? Items { get; }

    public T?  Item  { get; }
    public int Index { get; private set; }

    public bool HasReplacedItem { get; }
    public T?   ReplacedItem    { get; }
    public int  MovedFromIndex  { get; private set; }

    public void ChangeIndex ( int index )
    {
        if ( index < 0 ) throw new ArgumentOutOfRangeException ( nameof ( index ) );
        if ( Index < 0 ) throw new InvalidOperationException   ( "CollectionChange has no Index to change" );

        Index = index;
    }

    public void ChangeMovedFromIndex ( int movedFromIndex )
    {
        if ( movedFromIndex < 0 ) throw new ArgumentOutOfRangeException ( nameof ( movedFromIndex ) );
        if ( MovedFromIndex < 0 ) throw new InvalidOperationException   ( "CollectionChange has no MovedFromIndex to change" );

        MovedFromIndex = movedFromIndex;
    }
}

public interface ICollectionChangeSet
{
    int Count { get; }
}

public interface ICollectionChangeSet < T > : IListWithRangeSupport < CollectionChange < T > >, ICollectionChangeSet
{

}

public class CollectionChangeSet < T > : List < CollectionChange < T > >, ICollectionChangeSet < T >
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionChangeSet{T}" /> class
    /// that is empty and has the default initial capacity.
    /// </summary>
    public CollectionChangeSet ( ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionChangeSet{T}" /> class
    /// that is empty and has the specified initial capacity.
    /// </summary>
    /// <param name="capacity">The number of changes that the new set can initially store.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is less than 0.</exception>
    public CollectionChangeSet ( int capacity ) : base ( capacity ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionChangeSet{T}" /> class
    /// that contains changes copied from the specified collection and has sufficient
    /// capacity to accommodate the number of changes copied.
    /// </summary>
    /// <param name="changes">The collection whose changes are copied to the new set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changes" /> is <see langword="null" />.</exception>
    public CollectionChangeSet ( IEnumerable < CollectionChange < T > > changes ) : base ( changes ) { }
}

public delegate void CollectionChangedCallback < T > ( IEnumerable < T > collection, CollectionChange < T > change );

public abstract class CollectionSubscription < T > : IDisposable
{
    protected CollectionSubscription ( IEnumerable < T > collection, CollectionChangedCallback < T > callback )
    {
        Collection = collection ?? throw new ArgumentNullException ( nameof ( collection ) );
        Callback   = callback   ?? throw new ArgumentNullException ( nameof ( callback ) );
    }

    public IEnumerable < T > Collection { get; }

    protected CollectionChangedCallback < T > Callback { get; }

    public abstract void Dispose ( );
}

public sealed class NotifyCollectionChangedCollectionSubscription < T > : CollectionSubscription < T >
{
    public NotifyCollectionChangedCollectionSubscription ( IEnumerable < T > collection, CollectionChangedCallback < T > callback ) : base ( collection, callback )
    {
        if ( collection is not INotifyCollectionChanged ncc )
            throw new ArgumentException ( "Collection must implement INotifyCollectionChanged", nameof ( collection ) );

        ncc.CollectionChanged += Collection_CollectionChanged;
    }

    public override void Dispose ( )
    {
        ( (INotifyCollectionChanged) Collection ).CollectionChanged -= Collection_CollectionChanged;
    }

    private void Collection_CollectionChanged ( object sender, NotifyCollectionChangedEventArgs e )
    {
        Callback ( Collection, ToCollectionChange ( e ) );
    }

    // TODO: Handle null NewItems/OldItems
    private static CollectionChange < T > ToCollectionChange ( NotifyCollectionChangedEventArgs e ) => e.Action switch
    {
        NotifyCollectionChangedAction.Add     => CollectionChange < T >.Added       ( e.NewItems.Cast < T > ( ), e.NewStartingIndex ),
        NotifyCollectionChangedAction.Remove  => CollectionChange < T >.Removed     ( e.OldItems.Cast < T > ( ), e.NewStartingIndex ),
        NotifyCollectionChangedAction.Move    => CollectionChange < T >.Moved       ( (T) e.NewItems [ 0 ], e.NewStartingIndex, e.OldStartingIndex ),
        NotifyCollectionChangedAction.Replace => CollectionChange < T >.Replaced    ( (T)e.NewItems [ 0 ], (T)e.OldItems [ 0 ], e.NewStartingIndex ),
        NotifyCollectionChangedAction.Reset   => CollectionChange < T >.Invalidated ( ),
        _ => throw new InvalidEnumArgumentException ( nameof ( e.Action ), (int) e.Action, typeof ( NotifyCollectionChangedAction ) )
    };
}

public class CollectionSubscriber : ICollectionSubscriber
{
    private readonly ICollectionSubscriptionFactory factory;

    public CollectionSubscriber ( ICollectionSubscriptionFactory factory )
    {
        this.factory = factory;
    }

    readonly Dictionary<Type, object> typedSubscribers =
        new Dictionary<Type, object> ( );

    public IDisposable Subscribe < T > ( IEnumerable < T > collection, CollectionChangedCallback < T > k )
    {
        var key = typeof(T);
        var sub = (CollectionSubscriber<T>?) null;
        if ( !typedSubscribers.TryGetValue ( key, out var subs ) )
        {
            sub = new CollectionSubscriber<T> ( factory );
            typedSubscribers.Add ( key, sub );
        }
        else
            sub = (CollectionSubscriber<T>?) subs;

        return sub.Subscribe ( collection, k );
    }

    public void Invalidate < T > ( IEnumerable < T > collection )
    {
        var key = typeof(T);
        var sub = (CollectionSubscriber<T>?) null;
        if ( !typedSubscribers.TryGetValue ( key, out var subs ) )
        {
            sub = new CollectionSubscriber<T> ( factory );
            typedSubscribers.Add ( key, sub );
        }
        else
            sub = (CollectionSubscriber<T>?) subs;

        sub.Invalidate ( collection );
    }
}

public class CollectionSubscriber < T >
{
    private readonly ConcurrentDictionary < IEnumerable < T >, Entry > entries = new ( );
    private readonly ICollectionSubscriptionFactory                    factory;

    public CollectionSubscriber ( ICollectionSubscriptionFactory factory )
    {
        this.factory = factory;
    }

    public IDisposable Subscribe ( IEnumerable < T > collection, CollectionChangedCallback < T > callback )
    {
        var key   = collection;
        var entry = entries.GetOrAdd ( key, _ => new ( ) );
        var token = new Token { Subscriber = this, Key = key, Entry = entry, Callback = callback };

        entry.Subscription ??= factory.Create < T > ( collection, entry.SubscriptionCallback );
        entry.Callback      += callback;

        return token;
    }

    public void Invalidate ( IEnumerable < T > collection )
    {
        if ( entries.TryGetValue ( collection, out var entry ) && entry.Callback is { } callback )
            callback ( collection, CollectionChange < T >.Invalidated ( ) );
    }

    private class Entry
    {
        public CollectionSubscription    < T >? Subscription;
        public CollectionChangedCallback < T >? Callback;

        public void SubscriptionCallback ( IEnumerable < T > collection, CollectionChange < T > change )
        {
            Callback?.Invoke ( collection, change );
        }
    }

    private sealed class Token : IDisposable
    {
        public CollectionSubscriber < T >      Subscriber;
        public IEnumerable < T >               Key;
        public Entry                           Entry;
        public CollectionChangedCallback < T > Callback;

        public void Dispose ( )
        {
            Subscriber.Remove ( this );
        }
    }

    private void Remove ( Token token )
    {
        token.Entry.Callback -= token.Callback;

        if ( token.Entry.Callback == null )
        {
            token.Entry.Subscription?.Dispose ( );
            token.Entry.Subscription = null;

            entries.TryRemove ( token.Key, out var _ );
        }
    }
}