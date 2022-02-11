using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics;
using System.ComponentModel;

namespace Epoxide;

public interface IMemberObserver
{
    IDisposable? Observe ( object target, MemberInfo member, Action<int> k );
    void Invalidate ( object target, MemberInfo member, int changeId = 0 );
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
    EventInfo eventInfo;
    Delegate eventHandler;

    public GenericEventMemberSubscription ( object target, MemberInfo member, MemberChangedCallback callback ) : base ( target, member, callback )
    {
        AddHandlerForFirstExistingEvent ( member.Name + "Changed", "EditingDidEnd", "ValueChanged", "Changed" );
    }

    public override void Dispose ( )
    {
        if ( eventInfo == null ) return;

        eventInfo.RemoveEventHandler ( Target, eventHandler );

        Debug.WriteLine ( "BIND: Removed handler for {0} on {1}", eventInfo.Name, Target );

        eventInfo = null;
        eventHandler = null;
    }

    void HandleAnyEvent ( object sender, EventArgs e )
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
                Debug.WriteLine ( "BIND: Added handler for {0} on {1}", eventInfo.Name, Target );
                return true;
            }
        }

        return false;
    }

    static EventInfo GetEvent ( Type type, string eventName )
    {
        var t = type;
        while ( t != null && t != typeof(object) )
        {
            var ti = t.GetTypeInfo ( );
            var ev = t.GetTypeInfo ( ).GetDeclaredEvent ( eventName );
            if ( ev != null ) return ev;
            t = ti.BaseType;
        }

        return null;
    }

    static Delegate CreateGenericEventHandler ( EventInfo evt, Action d )
    {
        var handlerType = evt.EventHandlerType;
        var handlerTypeInfo = handlerType.GetTypeInfo ( );
        var handlerInvokeInfo = handlerTypeInfo.GetDeclaredMethod ( "Invoke" );
        var eventParams = handlerInvokeInfo.GetParameters ( );

        //lambda: (object x0, EventArgs x1) => d()
        var parameters = eventParams.Select ( p => Expression.Parameter ( p.ParameterType, p.Name ) ).ToArray ( );
        var body = Expression.Call ( Expression.Constant ( d ),
            d.GetType ( ).GetTypeInfo ( ).GetDeclaredMethod ( "Invoke" ) );
        var lambda = Expression.Lambda ( body, parameters );

        // TODO: Caching
        return lambda.Compile ( );

        //var delegateInvokeInfo = lambda.Compile ( ).GetMethodInfo ( );
        //return delegateInvokeInfo.CreateDelegate ( handlerType, null );
    }
}

public class MemberObserver : IMemberObserver
{
    private class MyClass
    {
        public MemberSubscription? Subscription;
        public Action<int>? Action;
    }

    readonly Dictionary<Tuple<Object, MemberInfo>, MyClass> objectSubs =
        new Dictionary<Tuple<Object, MemberInfo>, MyClass> ( );


    private sealed class Token : IDisposable
    {
        public MemberObserver me;
        public MyClass MyClass;
        public Action<int> Callback;

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

    public IDisposable? Observe ( object target, MemberInfo member, Action<int> k )
    {
        var key = Tuple.Create ( target, member );
        MyClass subs;
        if ( !objectSubs.TryGetValue ( key, out subs ) )
        {
            subs = new MyClass ( );
            objectSubs.Add ( key, subs );
        }

        if ( subs.Action == null )
            subs.Subscription = target is INotifyPropertyChanged npc ? new NotifyPropertyChangedMemberSubscription ( npc, member, Callback ) :
                new GenericEventMemberSubscription ( target, member, Callback );

        subs.Action += k;

        return new Token { me = this, MyClass = subs, Callback = k };
    }

    private void Callback ( object target, MemberInfo member )
    {
        Invalidate ( target, member, 0 );
    }

    public void Invalidate ( object target, MemberInfo member, int changeId = 0 )
    {
        var key = Tuple.Create ( target, member );
        if ( objectSubs.TryGetValue ( key, out var subs ) )
        {
            if ( subs.Action is { } a )
                a ( changeId );
        }
    }
}