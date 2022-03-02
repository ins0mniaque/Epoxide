using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

using Epoxide.Disposables;

namespace Epoxide.Linq.Expressions;

// NOTE: Schedule is also used for change tracking
public interface IExpressionStateMachine : IDisposable
{
    // TODO: void, and State property?
    ExpressionState MoveNext ( );

    bool Get < T > ( int id, [ MaybeNullWhen ( true ) ] out T value );
    T    Set < T > ( int id, T value );
    void Clear     ( int id );

    bool Schedule < T > ( int id, T instance, MemberInfo member );
    bool Await    < T > ( int id, T value );

    // TODO: GetException/GetResult? TryGet?

    ExpressionState SetException ( ExceptionDispatchInfo exception );
}

public interface IExpressionStateMachine < TResult > : IExpressionStateMachine
{
    ExpressionState SetResult ( TResult value );
}

// TODO: Reorder/rename values
public enum ExpressionState
{
    Exception = -1,
    Uninitialized,
    Fallback,
    Scheduled,
    Awaiting,
    Result
}

public interface IExpressionStateMachineStore < TResult > : IExpressionStateMachine < TResult >
{
    void SetStateMachine ( IExpressionStateMachine < TResult > stateMachine );
}

public sealed class ExpressionStateMachineStore < TResult > : IExpressionStateMachineStore < TResult >
{
    private IExpressionStateMachine < TResult > stateMachine;

    private readonly object? [ ] vars;
    private readonly bool    [ ] hass;

    public ExpressionStateMachineStore ( int capacity )
    {
        vars = new object? [ capacity ];
        hass = new bool    [ capacity ];
    }

    // TODO: Verify this was set
    public void SetStateMachine ( IExpressionStateMachine < TResult > stateMachine )
    {
        stateMachine = stateMachine;
    }

    public ExpressionState MoveNext ( ) => stateMachine.MoveNext ( );

    public bool Get<T> ( int id, [MaybeNullWhen ( true )] out T value )
    {
        if ( hass [ id ] )
        {
            value = (T) vars [ id ];
            return true;
        }

        value = default;
        return false;
    }

    public T Set<T> ( int id, T value )
    {
        vars [ id ] = value;
        hass [ id ] = true;

        return value;
    }

    public void Clear ( int id )
    {
        vars [ id ] = default;
        hass [ id ] = false;
    }

    public bool Schedule<T> ( int id, T instance, MemberInfo member ) => stateMachine.Schedule ( id, instance, member );
    public bool Await<T> ( int id, T value ) => stateMachine.Await ( id, value );
    public ExpressionState SetException ( ExceptionDispatchInfo exception ) => stateMachine.SetException ( exception );
    public ExpressionState SetResult ( TResult value ) => stateMachine.SetResult ( value );
    public void Dispose ( ) => stateMachine.Dispose ( );
}

// TODO: Replace state with typed state visitor
// TODO: Automatically generate the stores 
public struct ExpressionStateMachineStore < T0, TResult > : IExpressionStateMachineStore < TResult >
{
    private IExpressionStateMachine < TResult > stateMachine;

    // TODO: public?
    bool has0;
    T0   var0;

    // TODO: Verify this was set
    public void SetStateMachine ( IExpressionStateMachine < TResult > stateMachine )
    {
        stateMachine = stateMachine;
    }

    public ExpressionState MoveNext ( ) => stateMachine.MoveNext ( );

    public bool Get<T> ( int id, [ MaybeNullWhen ( true ) ] out T value )
    {
        switch(id)
        {
            case 0: value = (T) (object) var0; return has0;
            default: value = default; return false;
        }
    }

    public T Set<T> ( int id, T value )
    {
        switch(id)
        {
            case 0: var0 = (T0) (object) value; has0 = true; return value;
            default: return default;
        }
    }

    public void Clear ( int id )
    {
        switch ( id )
        {
            case 0: var0 = default; has0 = false; break;
        }
    }

    public bool Schedule<T> ( int id, T instance, MemberInfo member ) => stateMachine.Schedule ( id, instance, member );
    public bool Await<T> ( int id, T value ) => stateMachine.Await ( id, value );
    public ExpressionState SetException ( ExceptionDispatchInfo exception ) => stateMachine.SetException ( exception );
    public ExpressionState SetResult ( TResult value ) => stateMachine.SetResult ( value );
    public void Dispose ( ) => stateMachine.Dispose ( );
}

public sealed class ExpressionStateMachine < TStateMachineStore, TResult > : IExpressionStateMachine < TResult >
    where TStateMachineStore : IExpressionStateMachineStore < TResult >
{
    private readonly TStateMachineStore  store;
    private readonly Func < TStateMachineStore, ExpressionState > moveNext;
    private readonly CompositeDisposable disposables;

    public ExpressionStateMachine ( TStateMachineStore store, Func < TStateMachineStore, ExpressionState > moveNext )
    {
        this.store = store;
        this.disposables = new ( );
        this.moveNext = moveNext;

        store.SetStateMachine ( this );
    }

    public ExpressionState MoveNext ( ) => moveNext ( store );

    public bool Get<T> ( int id, [MaybeNullWhen ( true )] out T value ) => store.Get ( id, out value );
    public T Set<T> ( int id, T value ) => store.Set < T > ( id, value );
    public void Clear ( int id ) => store.Clear ( id );

    public bool Await<T> ( int id, T value )
    {
        if ( value is IAwaitable )
        {
            // TODO: Handle awaitable

            // TODO: Callback: Clear ( id ), MoveNext
        }

        return false;
    }

    public bool Schedule < T > ( int id, T instance, MemberInfo member )
    {
        var scheduler = (IScheduler?) null;
        var sub       = (ChangeTracking.IMemberSubscription?) null;

        // TODO: Callback: Clear ( id ), MoveNext

        return false;
    }

    public ExpressionState SetException ( ExceptionDispatchInfo exception )
    {
        // TODO: Error event? 
        return ExpressionState.Exception;
    }

    public ExpressionState SetResult ( TResult value )
    {
        // TODO: ValueChanged event
        return ExpressionState.Result;
    }

    public void Dispose ( )
    {
        disposables.Dispose ( );
    }
}