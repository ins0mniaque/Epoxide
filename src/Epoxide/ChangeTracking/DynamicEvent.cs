using System.Collections.Concurrent;

namespace Epoxide.ChangeTracking;

public static class DynamicEvent
{
    private static readonly ConcurrentDictionary < EventInfo, Func < Action < object? [ ] >, Delegate > > cache = new ( );

    public static Delegate Create ( EventInfo @event, Action < object? [ ] > callback )
    {
        return cache.GetOrAdd ( @event, CreateDelegateFactory ) ( callback );
    }

    public static bool Supports ( EventInfo @event )
    {
        var method = @event.EventHandlerType.GetMethod ( nameof ( Action.Invoke ) );

        return method.ReturnType == typeof ( void ) &&
               method.GetParameters ( ).Length <= 8;
    }

    public static EventInfo EnsureSupported ( EventInfo @event )
    {
        var method = @event.EventHandlerType.GetMethod ( nameof ( Action.Invoke ) );

        if ( method.ReturnType != typeof ( void ) )
            throw new NotSupportedException ( $"Event { DebugView.Display ( @event ) } has a return type" );

        if ( method.GetParameters ( ).Length > 8 )
            throw new NotSupportedException ( $"Event { DebugView.Display ( @event ) } has too many arguments" );

        return @event;
    }

    public static EventInfo? FindEvent ( Type type, string eventName )
    {
        while ( type != null && type != typeof ( object ) )
        {
            if ( type.GetEvent ( eventName ) is { } @event )
                return @event;

            type = type.BaseType;
        }

        return null;
    }

    private static Func < Action < object? [ ] >, Delegate >? identity;
    private static Func < Action < object? [ ] >, Delegate >? action;
    private static Func < Action < object? [ ] >, Delegate >? classic;
    private static Func < Action < object? [ ] >, Delegate >? generic;

    private static PropertyInfo? handlerDelegate;

    private static Func < Action < object? [ ] >, Delegate > CreateDelegateFactory ( EventInfo @event )
    {
        if ( @event.EventHandlerType == typeof ( Action < object? [ ] > )     ) return identity ??= callback => callback;
        if ( @event.EventHandlerType == typeof ( Action )                     ) return action   ??= callback => (Action) ( ( ) => callback ( Array.Empty < object > ( ) ) );
        if ( @event.EventHandlerType == typeof ( EventHandler )               ) return classic  ??= callback => (EventHandler)               ( (o, e) => callback ( new [ ] { o, e } ) );
        if ( @event.EventHandlerType == typeof ( EventHandler < EventArgs > ) ) return generic  ??= callback => (EventHandler < EventArgs >) ( (o, e) => callback ( new [ ] { o, e } ) );

        handlerDelegate ??= typeof ( Handler ).GetProperty ( nameof ( Handler.Delegate ) );

        var type      = GetHandlerType ( @event );
        var ctor      = type.GetConstructor ( new [ ] { typeof ( Action < object? [ ] > ) } );
        var callback  = Expression.Parameter ( typeof ( Action < object? [ ] > ), "callback" );
        var @delegate = Expression.MakeMemberAccess ( Expression.New ( ctor, callback ), handlerDelegate );
        var handler   = Expression.Convert ( @delegate, @event.EventHandlerType );
        var factory   = Expression.Lambda < Func < Action < object? [ ] >, Delegate > > ( handler, callback );

        return factory.Compile ( );
    }

    private static Type GetHandlerType ( EventInfo @event )
    {
        var method = @event.EventHandlerType.GetMethod ( nameof ( Action.Invoke ) );
        if ( method.ReturnType != typeof ( void ) )
            throw new NotSupportedException ( $"Event { DebugView.Display ( @event ) } has a return type" );

        var parameters = method.GetParameters ( );
        var types      = new Type [ parameters.Length ];
        for ( var index = 0; index < types.Length; index++ )
            types [ index ] = parameters [ index ].ParameterType;

        return types.Length switch
        {
            0 => typeof ( Handler                   ),
            1 => typeof ( Handler < >               ).MakeGenericType ( types ),
            2 => typeof ( Handler < , >             ).MakeGenericType ( types ),
            3 => typeof ( Handler < , , >           ).MakeGenericType ( types ),
            4 => typeof ( Handler < , , , >         ).MakeGenericType ( types ),
            5 => typeof ( Handler < , , , , >       ).MakeGenericType ( types ),
            6 => typeof ( Handler < , , , , , >     ).MakeGenericType ( types ),
            7 => typeof ( Handler < , , , , , , >   ).MakeGenericType ( types ),
            8 => typeof ( Handler < , , , , , , , > ).MakeGenericType ( types ),
            _ => throw new NotSupportedException ( $"Event { DebugView.Display ( @event ) } has too many arguments" )
        };
    }

    private abstract class Handler
    {
        protected Handler ( Action < object? [ ] > callback )
        {
            Callback = callback;
        }

        public          Action < object? [ ] > Callback { get; }
        public abstract Delegate               Delegate { get; }
    }

    private class Handler < T > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T arg )
        {
            Callback ( new object? [ ] { arg } );
        }
    }

    private class Handler < T1, T2 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2 )
        {
            Callback ( new object? [ ] { arg1, arg2 } );
        }
    }

    private class Handler < T1, T2, T3 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2, T3 arg3 )
        {
            Callback ( new object? [ ] { arg1, arg2, arg3 } );
        }
    }

    private class Handler < T1, T2, T3, T4 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2, T3 arg3, T4 arg4 )
        {
            Callback ( new object? [ ] { arg1, arg2, arg3, arg4 } );
        }
    }

    private class Handler < T1, T2, T3, T4, T5 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5 )
        {
            Callback ( new object? [ ] { arg1, arg2, arg3, arg4, arg5 } );
        }
    }

    private class Handler < T1, T2, T3, T4, T5, T6 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6 )
        {
            Callback ( new object? [ ] { arg1, arg2, arg3, arg4, arg5, arg6 } );
        }
    }

    private class Handler < T1, T2, T3, T4, T5, T6, T7 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7 )
        {
            Callback ( new object? [ ] { arg1, arg2, arg3, arg4, arg5, arg6, arg7 } );
        }
    }

    private class Handler < T1, T2, T3, T4, T5, T6, T7, T8 > : Handler
    {
        public Handler ( Action < object? [ ] > action ) : base ( action ) { }

        public override Delegate Delegate => Handle;

        public void Handle ( T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8 )
        {
            Callback ( new object? [ ] { arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 } );
        }
    }
}