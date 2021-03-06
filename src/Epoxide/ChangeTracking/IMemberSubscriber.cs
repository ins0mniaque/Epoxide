using System.Collections.Concurrent;
using System.ComponentModel;

namespace Epoxide.ChangeTracking;

public delegate void MemberChangedCallback ( object target, MemberInfo member );

public interface IMemberSubscription : IDisposable
{
    object     Target { get; }
    MemberInfo Member { get; }
}

/// <summary>
/// Specifies the behavior of a subscriber upon invalidation.
/// </summary>
public enum InvalidationMode
{
    /// <summary>
    /// The default setting for this enumeration, which is currently <see cref="Optimized" />.
    /// </summary>
    Default,

    /// <summary>
    /// Forces the invalidation to occur immediately.
    /// </summary>
    Forced,

    /// <summary>
    /// Allows the subscriber to determine whether and when invalidation is necessary.
    /// </summary>
    Optimized
}

public interface IMemberSubscriber : IDisposable
{
    IDisposable Subscribe  ( object target, MemberInfo member, MemberChangedCallback callback );
    void        Invalidate ( object target, MemberInfo member, InvalidationMode      mode     );
}

public interface IMemberSubscriptionFactory
{
    IMemberSubscription? Create ( object target, MemberInfo member, MemberChangedCallback callback );
}

public class DefaultMemberSubscriptionFactory : IMemberSubscriptionFactory
{
    public IMemberSubscription? Create ( object target, MemberInfo member, MemberChangedCallback callback )
    {
        if ( target is INotifyPropertyChanged npc )
            return new NotifyPropertyChangedMemberSubscription ( npc, member, callback );

        var type   = target.GetType ( );
        var @event = DynamicEvent.FindEvent ( type, member.Name + "Changed" ) ??
                     DynamicEvent.FindEvent ( type, "EditingDidEnd" )         ??
                     DynamicEvent.FindEvent ( type, "ValueChanged" )          ??
                     DynamicEvent.FindEvent ( type, "Changed" );

        if ( @event != null && DynamicEvent.Supports ( @event ) )
            return new DynamicEventMemberSubscription ( @event, target, member, callback );

        return null;
    }
}

public sealed class NotifyPropertyChangedMemberSubscription : IMemberSubscription
{
    public NotifyPropertyChangedMemberSubscription ( INotifyPropertyChanged target, MemberInfo member, MemberChangedCallback callback )
    {
        Target   = target   ?? throw new ArgumentNullException ( nameof ( target ) );
        Member   = member   ?? throw new ArgumentNullException ( nameof ( member ) );
        Callback = callback ?? throw new ArgumentNullException ( nameof ( callback ) );

        target.PropertyChanged += TargetOnPropertyChanged;
    }

    public object     Target { get; }
    public MemberInfo Member { get; }

    private MemberChangedCallback Callback { get; }

    public void Dispose ( )
    {
        ( (INotifyPropertyChanged) Target ).PropertyChanged -= TargetOnPropertyChanged;
    }

    private void TargetOnPropertyChanged ( object sender, PropertyChangedEventArgs e )
    {
        if ( string.IsNullOrEmpty ( e.PropertyName ) || e.PropertyName == Member.Name )
            Callback ( Target, Member );
    }
}

public sealed class DynamicEventMemberSubscription : IMemberSubscription
{
    public DynamicEventMemberSubscription ( EventInfo @event, object target, MemberInfo member, MemberChangedCallback callback )
    {
        Event        = @event   ?? throw new ArgumentNullException ( nameof ( @event ) );
        Target       = target   ?? throw new ArgumentNullException ( nameof ( target ) );
        Member       = member   ?? throw new ArgumentNullException ( nameof ( member ) );
        Callback     = callback ?? throw new ArgumentNullException ( nameof ( callback ) );
        EventHandler = DynamicEvent.Create ( @event, HandleEvent );

        Event.AddEventHandler ( Target, EventHandler );
    }

    public EventInfo  Event  { get; }
    public object     Target { get; }
    public MemberInfo Member { get; }

    private MemberChangedCallback Callback     { get; }
    private Delegate?             EventHandler { get; set; }

    public void Dispose ( )
    {
        if ( EventHandler is { } eventHandler )
            Event.RemoveEventHandler ( Target, eventHandler );

        EventHandler = null;
    }

    private void HandleEvent ( object? [ ] arguments )
    {
        Callback ( Target, Member );
    }
}

public sealed class MemberSubscriber : IMemberSubscriber
{
    private readonly ConcurrentDictionary < (object, MemberInfo), Entry > entries = new ( );
    private readonly IMemberSubscriptionFactory                           factory;

    public MemberSubscriber ( IMemberSubscriptionFactory factory )
    {
        this.factory = factory;
    }

    public IDisposable Subscribe ( object target, MemberInfo member, MemberChangedCallback callback )
    {
        var key   = (target, member);
        var entry = entries.GetOrAdd ( key, _ => new ( ) );
        var token = new Token { Subscriber = this, Key = key, Entry = entry, Callback = callback };

        entry.Subscription ??= factory.Create ( target, member, entry.SubscriptionCallback );
        entry.Callback      += callback;

        return token;
    }

    public void Invalidate ( object target, MemberInfo member, InvalidationMode mode )
    {
        if ( entries.TryGetValue ( (target, member), out var entry ) && entry.Callback is { } callback )
            if ( mode == InvalidationMode.Forced || entry.Subscription != null )
                callback ( target, member );
    }

    public void Dispose ( )
    {
        foreach ( var entry in entries )
        {
            entry.Value.Subscription?.Dispose ( );
            entry.Value.Subscription = null;
        }

        entries.Clear ( );
    }

    private class Entry
    {
        public IMemberSubscription?   Subscription;
        public MemberChangedCallback? Callback;

        public void SubscriptionCallback ( object target, MemberInfo member )
        {
            Callback?.Invoke ( target, member );
        }
    }

    private sealed class Token : IDisposable
    {
        public MemberSubscriber      Subscriber;
        public (object, MemberInfo)  Key;
        public Entry                 Entry;
        public MemberChangedCallback Callback;

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