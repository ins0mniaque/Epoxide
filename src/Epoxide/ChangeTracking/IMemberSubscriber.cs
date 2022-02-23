using System.Collections.Concurrent;
using System.ComponentModel;

namespace Epoxide.ChangeTracking;

public interface IMemberSubscriber
{
    IDisposable Subscribe  ( object target, MemberInfo member, MemberChangedCallback callback );
    void        Invalidate ( object target, MemberInfo member );
}

public interface IMemberSubscriptionFactory
{
    MemberSubscription? Create ( object target, MemberInfo member, MemberChangedCallback callback );
}

public class MemberSubscriptionFactory : IMemberSubscriptionFactory
{
    public MemberSubscription? Create ( object target, MemberInfo member, MemberChangedCallback callback )
    {
        return target is INotifyPropertyChanged npc ? new NotifyPropertyChangedMemberSubscription ( npc,    member, callback ) :
                                                      new GenericEventMemberSubscription          ( target, member, callback );
    }
}

public delegate void MemberChangedCallback ( object target, MemberInfo member );

public abstract class MemberSubscription : IDisposable
{
    protected MemberSubscription ( object target, MemberInfo member, MemberChangedCallback callback )
    {
        Target   = target   ?? throw new ArgumentNullException ( nameof ( target ) );
        Member   = member   ?? throw new ArgumentNullException ( nameof ( member ) );
        Callback = callback ?? throw new ArgumentNullException ( nameof ( callback ) );
    }

    public object     Target { get; }
    public MemberInfo Member { get; }

    protected MemberChangedCallback Callback { get; }

    public abstract void Dispose ( );
}

public sealed class NotifyPropertyChangedMemberSubscription : MemberSubscription
{
    public NotifyPropertyChangedMemberSubscription ( INotifyPropertyChanged target, MemberInfo member, MemberChangedCallback callback ) : base ( target, member, callback )
    {
        target.PropertyChanged += TargetOnPropertyChanged;
    }

    public override void Dispose ( )
    {
        ( (INotifyPropertyChanged) Target ).PropertyChanged -= TargetOnPropertyChanged;
    }

    private void TargetOnPropertyChanged ( object sender, PropertyChangedEventArgs e )
    {
        if ( string.IsNullOrEmpty ( e.PropertyName ) || e.PropertyName == Member.Name )
            Callback ( Target, Member );
    }
}

public sealed class GenericEventMemberSubscription : MemberSubscription
{
    EventInfo? eventInfo;
    Delegate? eventHandler;

    public GenericEventMemberSubscription ( object target, MemberInfo member, MemberChangedCallback callback ) : base ( target, member, callback )
    {
        AddHandlerForFirstExistingEvent ( member.Name + "Changed", "EditingDidEnd", "ValueChanged", "Changed" );
    }

    public override void Dispose ( )
    {
        if ( eventInfo == null ) return;

        eventInfo.RemoveEventHandler ( Target, eventHandler );

        eventInfo = null;
        eventHandler = null;
    }

    void HandleAnyEvent ( object? sender, EventArgs e )
    {
        Callback ( Target, Member );
    }

    bool AddHandlerForFirstExistingEvent ( params string [ ] names )
    {
        var type = Target.GetType ( );
        foreach ( var name in names )
        {
            var ev = GetEvent ( type, name );

            if ( ev != null )
            {
                eventInfo = ev;
                var isClassicHandler = typeof(EventHandler).GetTypeInfo ( )
                    .IsAssignableFrom ( ev.EventHandlerType.GetTypeInfo ( ) );

                eventHandler = isClassicHandler
                    ? (EventHandler) HandleAnyEvent
                    : CreateGenericEventHandler ( ev, ( ) => HandleAnyEvent ( null, EventArgs.Empty ) );

                ev.AddEventHandler ( Target, eventHandler );

                return true;
            }
        }

        return false;
    }

    static EventInfo? GetEvent ( Type type, string eventName )
    {
        var t = type;
        while ( t != null && t != typeof(object) )
        {
            var ti = t.GetTypeInfo ( );
            var ev = ti.GetDeclaredEvent ( eventName );
            if ( ev != null ) return ev;
            t = ti.BaseType;
        }

        return null;
    }

    static Dictionary<EventInfo, Delegate> cache = new Dictionary<EventInfo, Delegate>();
    static Delegate CreateGenericEventHandler ( EventInfo evt, Action d )
    {
        if ( cache.TryGetValue ( evt, out var handler ) )
            return handler;

        var handlerType = evt.EventHandlerType;
        var handlerTypeInfo = handlerType.GetTypeInfo ( );
        var handlerInvokeInfo = handlerTypeInfo.GetDeclaredMethod ( nameof ( Action.Invoke ) );
        var eventParams = handlerInvokeInfo.GetParameters ( );

        var parameters = eventParams.Select ( p => Expression.Parameter ( p.ParameterType, p.Name ) ).ToArray ( );
        var body = Expression.Call ( Expression.Constant ( d ),
            d.GetType ( ).GetTypeInfo ( ).GetDeclaredMethod ( nameof ( Action.Invoke ) ) );
        var lambda = Expression.Lambda ( body, parameters );

        cache [ evt ] = handler = lambda.Compile ( );

        return handler;
    }
}

public class MemberSubscriber : IMemberSubscriber
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

    public void Invalidate ( object target, MemberInfo member )
    {
        if ( entries.TryGetValue ( (target, member), out var entry ) && entry.Callback is { } callback )
            callback ( target, member );
    }

    private class Entry
    {
        public MemberSubscription?    Subscription;
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