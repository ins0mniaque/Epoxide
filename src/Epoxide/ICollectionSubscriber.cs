using System.Collections.Specialized;
using System.ComponentModel;

namespace Epoxide;

public interface ICollectionSubscriber
{
    IDisposable Subscribe < T > ( IEnumerable < T > collection, Action<CollectionChange<T>, int> k );
    void Invalidate < T > ( IEnumerable < T > collection, int changeId = 0 );
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

public sealed class CollectionChange < T >
{
    // TODO: Move methods to non-generic static class
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
    public int Index { get; }

    public bool HasReplacedItem { get; }
    public T?   ReplacedItem    { get; }
    public int  MovedFromIndex  { get; }
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
        if ( e.Action == NotifyCollectionChangedAction.Reset && ((IEnumerable < T >) Collection).Any ( ) )
            Callback ( Collection, CollectionChange<T>.Added((IEnumerable < T >) Collection, 0));
    }

    // TODO: Handle null NewItems/OldItems
    private static CollectionChange < T > ToCollectionChange ( NotifyCollectionChangedEventArgs e ) => e.Action switch
    {
        NotifyCollectionChangedAction.Add     => CollectionChange < T >.Added    ( e.NewItems.Cast < T > ( ), e.NewStartingIndex ),
        NotifyCollectionChangedAction.Remove  => CollectionChange < T >.Removed  ( e.OldItems.Cast < T > ( ), e.NewStartingIndex ),
        NotifyCollectionChangedAction.Move    => CollectionChange < T >.Moved    ( (T) e.NewItems [ 0 ], e.NewStartingIndex, e.OldStartingIndex ),
        NotifyCollectionChangedAction.Replace => CollectionChange < T >.Replaced ( (T)e.NewItems [ 0 ], (T)e.OldItems [ 0 ], e.NewStartingIndex ),
        NotifyCollectionChangedAction.Reset   => CollectionChange < T >.Cleared  ( ),
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

    public IDisposable Subscribe < T > ( IEnumerable < T > collection, Action<CollectionChange<T>, int> k )
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

    public void Invalidate < T > ( IEnumerable < T > collection, int changeId = 0 )
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

        sub.Invalidate ( collection, changeId );
    }
}

public class CollectionSubscriber < T >
{
    private readonly ICollectionSubscriptionFactory factory;

    public CollectionSubscriber ( ICollectionSubscriptionFactory factory )
    {
        this.factory = factory;
    }

    private class Entry
    {
        public CollectionSubscription< T >? Subscription;
        public Action<CollectionChange<T>, int>? Action;
    }

    readonly Dictionary<IEnumerable < T >, Entry> collectionSubs =
        new Dictionary<IEnumerable < T >, Entry> ( );


    private sealed class Token : IDisposable
    {
        public CollectionSubscriber<T> me;
        public Entry MyClass;
        public Action<CollectionChange<T>, int> Callback;

        public void Dispose ( )
        {
            me.Remove ( this );
        }
    }

    private void Remove ( Token token )
    {
        token.MyClass.Action -= token.Callback;
        if ( token.MyClass.Action == null )
        {
            token.MyClass.Subscription?.Dispose ( );
            token.MyClass.Subscription = null;
        }
    }

    public IDisposable Subscribe ( IEnumerable < T > collection, Action<CollectionChange<T>, int> k )
    {
        var key = collection;
        Entry subs;
        if ( !collectionSubs.TryGetValue ( key, out subs ) )
        {
            subs = new Entry ( );
            collectionSubs.Add ( key, subs );
        }

        if ( subs.Action == null )
            subs.Subscription = factory.Create< T > ( collection, Callback );

        subs.Action += k;

        return new Token { me = this, MyClass = subs, Callback = k };
    }

    private void Callback ( IEnumerable < T > collection, CollectionChange<T> change )
    {
        Notify ( collection, change, 0 );
    }

    public void Invalidate ( IEnumerable < T > collection, int changeId = 0 )
    {
        Notify ( collection, CollectionChange < T >.Invalidated ( ), changeId );
    }

    private void Notify ( IEnumerable < T > collection, CollectionChange< T > change, int changeId = 0 )
    {
        var key = collection;
        if ( collectionSubs.TryGetValue ( key, out var subs ) )
        {
            if ( subs.Action is { } a )
                a ( change, changeId );
        }
    }
}