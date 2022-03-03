using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Epoxide.Linq.Expressions;

public interface IAwaiter < T >
{
    IDisposable Await < TState > ( TState state, Action < TState, T? > onCompleted, Action < TState, ExceptionDispatchInfo? > onException );
}

// NOTE: Task.Result is supported by awaiting task and MoveNext,
//       which will read .Result
public interface IAwaiterSelector // Name? New binding service?
{
    // TryAwait?
    IAwaiter < T >? SelectAwaiter < T > ( T awaitable );
}

public interface INewScheduler
{
    IDisposable Schedule < TState > ( TState state, Action < TState > action );
}

public interface INewSchedulerSelector
{
    INewScheduler? SelectScheduler < T > ( T instance, MemberInfo member );
}

// TODO: Split into 2 interfaces? IExpressionStateMachineScheduler/Awaiter
public interface IExpressionStateMachineHandler
{
    bool Schedule < T > ( int id, T instance, MemberInfo member );
    bool Await    < T > ( int id, T value );
}

// TODO: Create once and reuse
//       Not sure if needs to be exposed on state machine
public interface IExpressionStateMachineMetadata
{
    Type [ ] Parameters { get; }
    Type [ ] Variables  { get; }
}

public sealed class ExpressionStateMachineHandler : IExpressionStateMachineHandler
{
    public static ExpressionStateMachineHandler Default { get; } = new ( );

    public bool Schedule < T > ( int id, T instance, MemberInfo member )
    {
        return false;
    }

    public bool Await < T > ( int id, T value )
    {
        return false;
    }
}

// NOTE: Schedule is also used for change tracking
// NOTE: Await is also used for validation
public interface IExpressionStateMachine : IExpressionStateMachineHandler
{
    event EventHandler? StateChanged;

    ExpressionState State { get; }

    void MoveNext ( );
    void Reset    ( );

    bool Get < T > ( int id, [ MaybeNullWhen ( true ) ] out T? value );
    T?   Set < T > ( int id, T? value );
    void Clear     ( int id );

    bool            TryGetException ( [ NotNullWhen ( true ) ] out ExceptionDispatchInfo? exception );
    ExpressionState SetException    ( ExceptionDispatchInfo exception );

    void SetHandler ( IExpressionStateMachineHandler handler );
}

public interface IExpressionStateMachine < TResult > : IExpressionStateMachine
{
    bool            TryGetResult ( [ MaybeNullWhen ( true ) ] out TResult? value );
    ExpressionState SetResult    ( TResult? value );
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

    public event EventHandler? StateChanged
    {
        add    { stateMachine.StateChanged += value; }
        remove { stateMachine.StateChanged -= value; }
    }

    public ExpressionState State => stateMachine.State;

    // TODO: Verify this was set
    public void SetStateMachine ( IExpressionStateMachine < TResult > stateMachine )
    {
        stateMachine = stateMachine;
    }

    public void MoveNext ( ) => stateMachine.MoveNext ( );
    public void Reset   ( )
    {
        stateMachine.Reset ( );

        Clear ( );
    }

    public bool Get < T > ( int id, [ MaybeNullWhen ( true ) ] out T? value )
    {
        if ( hass [ id ] )
        {
            value = (T?) vars [ id ];
            return true;
        }

        value = default;
        return false;
    }

    public T? Set < T > ( int id, T? value )
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

    private void Clear ( )
    {
        Array.Fill ( vars, default );
        Array.Fill ( hass, false   );
    }

    public bool Schedule<T> ( int id, T instance, MemberInfo member ) => stateMachine.Schedule ( id, instance, member );
    public bool Await<T> ( int id, T value ) => stateMachine.Await ( id, value );
    public bool TryGetException ( [ NotNullWhen ( true ) ] out ExceptionDispatchInfo? exception ) => stateMachine.TryGetException ( out exception );
    public ExpressionState SetException ( ExceptionDispatchInfo exception ) => stateMachine.SetException ( exception );
    public bool TryGetResult ( [ MaybeNullWhen ( true ) ] out TResult? value ) => stateMachine.TryGetResult ( out value );
    public ExpressionState SetResult ( TResult? value ) => stateMachine.SetResult ( value );
    public void SetHandler ( IExpressionStateMachineHandler handler ) => stateMachine.SetHandler(handler);
}

// TODO: Replace state with typed state visitor
// TODO: Automatically generate the stores 
public struct ExpressionStateMachineStore < T0, TResult > : IExpressionStateMachineStore < TResult >
{
    private IExpressionStateMachine < TResult > stateMachine;

    // TODO: public?
    bool has0;
    T0?  var0;

    public event EventHandler? StateChanged
    {
        add    { stateMachine.StateChanged += value; }
        remove { stateMachine.StateChanged -= value; }
    }

    public ExpressionState State => stateMachine.State;

    // TODO: Verify this was set
    public void SetStateMachine ( IExpressionStateMachine < TResult > stateMachine )
    {
        stateMachine = stateMachine;
    }

    public void MoveNext ( ) => stateMachine.MoveNext ( );
    public void Reset    ( )
    {
        stateMachine.Reset ( );

        Clear ( );
    }

    public bool Get < T > ( int id, [ MaybeNullWhen ( true ) ] out T? value )
    {
        switch(id)
        {
            case 0: value = (T?) (object?) var0; return has0;
            default: value = default; return false;
        }
    }

    public T? Set < T > ( int id, T? value )
    {
        switch(id)
        {
            case 0: var0 = (T0?) (object?) value; has0 = true; return value;
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

    private void Clear ( )
    {
        var0 = default; has0 = false;
    }

    public bool Schedule<T> ( int id, T instance, MemberInfo member ) => stateMachine.Schedule ( id, instance, member );
    public bool Await<T> ( int id, T value ) => stateMachine.Await ( id, value );
    public bool TryGetException ( [ NotNullWhen ( true ) ] out ExceptionDispatchInfo? exception ) => stateMachine.TryGetException ( out exception );
    public ExpressionState SetException ( ExceptionDispatchInfo exception ) => stateMachine.SetException ( exception );
    public bool TryGetResult ( [ MaybeNullWhen ( true ) ] out TResult? value ) => stateMachine.TryGetResult ( out value );
    public ExpressionState SetResult ( TResult? value ) => stateMachine.SetResult ( value );
    public void SetHandler ( IExpressionStateMachineHandler handler ) => stateMachine.SetHandler(handler);
}

public sealed class ExpressionStateMachine < TStateMachineStore, TResult > : IExpressionStateMachine < TResult >
    where TStateMachineStore : IExpressionStateMachineStore < TResult >
{
    private readonly TStateMachineStore  store;
    private readonly Func < TStateMachineStore, ExpressionState > moveNext;
    private          IExpressionStateMachineHandler handler;

    public ExpressionStateMachine ( TStateMachineStore store, Func < TStateMachineStore, ExpressionState > moveNext )
    {
        this.store = store;
        this.moveNext = moveNext;
        this.handler = ExpressionStateMachineHandler.Default;

        store.SetStateMachine ( this );
    }

    public event EventHandler? StateChanged;

    public  ExpressionState        State     { get; private set; }
    private ExceptionDispatchInfo? Exception { get; set; }
    private TResult?               Result    { get; set; }

    public void MoveNext ( )
    {
        State = moveNext ( store );

        // TODO: If State == Await, check for marked exception or result immediately
        //       It could have ran synchronously

        if ( State != ExpressionState.Exception )
            Exception = null;

        if ( State != ExpressionState.Result )
            Result = default;

        StateChanged?.Invoke ( this, EventArgs.Empty );
    }

    public void Reset ( )
    {
        State     = ExpressionState.Uninitialized;
        Exception = null;
        Result    = default;

        StateChanged?.Invoke ( this, EventArgs.Empty );
    }

    public bool Get<T> ( int id, [MaybeNullWhen ( true )] out T? value ) => store.Get ( id, out value );
    public T? Set<T> ( int id, T? value ) => store.Set ( id, value );
    public void Clear ( int id ) => store.Clear ( id );

    bool isRunningAwait;

    public bool Await < T > ( int id, T value )
    {
        try
        {
            isRunningAwait = true;

            return handler.Await ( id, value );
        }
        finally
        {
            isRunningAwait = false;
        }
    }

    public bool Schedule < T > ( int id, T instance, MemberInfo member )
    {
        return handler.Schedule ( id, instance, member );
    }

    public bool TryGetException ( [ NotNullWhen ( true ) ] out ExceptionDispatchInfo? exception )
    {
        if ( State == ExpressionState.Exception && Exception != null )
        {
            exception = Exception;
            return true;
        }

        exception = default;
        return false;
    }

    public ExpressionState SetException ( ExceptionDispatchInfo exception )
    {
        // TODO: Mark exceptions while isRunningAwait is true
        Exception = exception;

        return ExpressionState.Exception;
    }

    public bool TryGetResult ( [ MaybeNullWhen ( true ) ] out TResult? value )
    {
        if ( State == ExpressionState.Result )
        {
            value = Result;
            return true;
        }

        value = default;
        return false;
    }

    public ExpressionState SetResult ( TResult? value )
    {
        // TODO: Mark results while isRunningAwait is true
        Result = value;

        return ExpressionState.Result;
    }

    public void SetHandler ( IExpressionStateMachineHandler handler )
    {
        this.handler = handler;
    }
}