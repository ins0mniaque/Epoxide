using System.ComponentModel;

using Epoxide.Disposables;

namespace Epoxide.Linq.Expressions;

// TODO: Need TState?
// TODO: Need interface with IBindableEnumerable
public interface IBindingExpression < TSource, TValue > : IDisposable // NOTE: Represents Binding.Side
{
    // LambdaExpression Expression    { get; }
    // bool             IsCollection  { get; }
    // bool             IsWritable    { get; }
    // bool             IsWriteActive { get; }

    // ExpressionState State { get; }
    // void Bind   ( ); // Starts state machine
    // void Unbind ( ); // Clear container and store

    // void Read  < TState > ( TSource source, TState state,                ExpressionAccessCallback < TSource, TState, ExpressionReadResult  > callback );
    // void Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback );
}

public static class BindingExpression
{
    public static IBindingExpression < TSource, TValue > Create < TSource, TValue > ( LambdaExpression expression )
    {
        if ( expression.Body.Type != typeof ( TValue ) )
            expression = Expression.Lambda < Func < TSource, TValue > > ( Expression.Convert ( expression.Body, typeof ( TValue ) ), expression.Parameters );

        return Create ( (Expression < Func < TSource, TValue > >) expression );
    }

    public static IBindingExpression < TSource, TValue > Create < TSource, TValue > ( Expression < Func < TSource, TValue > > expression )
    {
        var visitor       = new ExpressionStateMachineBuilderVisitor ( );
        var stateMachined = (Expression < Func < IExpressionStateMachine < TValue >, ExpressionState > >) visitor.Visit ( expression );

        // TODO: Create appropriate ExpressionStateMachineStore < > if variables.Count is low enough
        //       and convert expression
        var parameters = visitor.Context.Parameters;
        var variables  = visitor.Context.Variables;
        var store      = new ExpressionStateMachineStore < TValue > ( parameters.Count + variables.Count );

        var moveNext = CachedExpressionCompiler.Compile ( stateMachined );
        var setter   = visitor.Context.WritableTarget   is { } target ? new ConstantExpressionSetter < TValue > ( target, visitor.Context.WritableExpression.Member ) :
                       visitor.Context.WritableTargetId is { } id     ? new ExpressionSetter         < TValue > ( id,     visitor.Context.WritableExpression.Member ) :
                       (IExpressionSetter < TValue >?) null;

        return new BindingExpression < TSource, TValue > ( new ExpressionStateMachine < ExpressionStateMachineStore < TValue >, TValue > ( store, moveNext ), setter );
    }
}

// TODO: Rename IExpressionStateMachineSetter?
public interface IExpressionSetter < TValue >
{
    bool SetValue ( IExpressionStateMachine < TValue > stateMachine, TValue? value );
}

public sealed class ConstantExpressionSetter < TValue > : IExpressionSetter < TValue >
{
    private readonly Action < object, TValue > setter;
    private readonly object                    target;

    public ConstantExpressionSetter ( object constant, MemberInfo member )
    {
        setter = DynamicTypeAccessor.CompileSetter < TValue > ( member );
        target = constant;
    }

    public bool SetValue ( IExpressionStateMachine < TValue > stateMachine, TValue value )
    {
        setter ( target, value );

        return true;
    }
}

public sealed class ExpressionSetter < TValue > : IExpressionSetter < TValue >
{
    private readonly Action < object, TValue > setter;
    private readonly int                       targetId;

    public ExpressionSetter ( int id, MemberInfo member )
    {
        setter   = DynamicTypeAccessor.CompileSetter < TValue > ( member );
        targetId = id;
    }

    public bool SetValue ( IExpressionStateMachine < TValue > stateMachine, TValue value )
    {
        if ( stateMachine.Get < object > ( targetId, out var target ) && target != null )
        {
            setter ( target, value );

            return true;
        }

        return false;
    }
}

public enum BindingState
{
    Exception = -1,
    Uninitialized,
    Fallback,
    Scheduled,
    Awaiting,
    Result
}

// TODO: Not extension
public static class BindingStateExtensions
{
    public static BindingState ToBindingState ( this ExpressionState state ) => state switch
    {
        ExpressionState.Exception     => BindingState.Exception,
        ExpressionState.Uninitialized => BindingState.Uninitialized,
        ExpressionState.Fallback      => BindingState.Fallback,
        ExpressionState.Scheduled     => BindingState.Scheduled,
        ExpressionState.Awaiting      => BindingState.Awaiting,
        ExpressionState.Result        => BindingState.Result,
        _                             => throw new InvalidEnumArgumentException ( nameof ( state ),
                                                                                  (int) state,
                                                                                  typeof ( ExpressionState ) )
    };
}

public interface IValidatorFactory
{
    IValidator < T > GetValidator < T > ( );
}

// TODO: Add metadata for field read from constants so IValidationContext.Instance/Member is always filled.
public interface IValidator < T >
{
    Task < ValidationResult < T > > Validate < TValue > ( IValidationContext < T > context, TValue value, CancellationToken cancellationToken );
}

public interface IValidationContext < T >
{
    T          Instance { get; }
    MemberInfo Member   { get; }
}

public interface IStateMachineValidationContext < T > : IValidationContext < T >
{
    int Id { get; }
}

// TODO: Rename error?
public class ValidationResult
{
    public object     Error  { get; }
    public MemberInfo Member { get; }
}

public class ValidationResult < T > : ValidationResult
{
    public T Instance { get; }
}

public interface IExpressionValidationFactory < TValue >
{
    IExpressionValidation < TValue >? Create ( IExpressionStateMachineMetadata metadata );
}

public interface IExpressionValidation < TValue >
{
    void AddError    ( IExpressionStateMachine < TValue > stateMachine, ValidationResult exception );
    void RemoveError ( IExpressionStateMachine < TValue > stateMachine, ValidationResult exception );
}

// TODO: Multi-model binding expressions
// TODO: Provide a way to throw an exception
public sealed class BindingExpression < TSource, TValue > : IBindingExpression < TSource, TValue >, IExpressionStateMachineHandler
{
    private readonly IExpressionStateMachine < TValue > stateMachine;
    private readonly IExpressionSetter < TValue >?      setter;
    private readonly IExpressionValidation < TValue >?  validation;
    private readonly CompositeDisposable                disposables;

    public BindingExpression ( IExpressionStateMachine < TValue > stateMachine, IExpressionSetter < TValue >? setter = null )
    {
        this.stateMachine = stateMachine;
        this.setter       = setter;
        this.disposables  = new ( );

        stateMachine.StateChanged += StateChanged;
    }

    public BindingState State => stateMachine.State.ToBindingState ( );

    private void StateChanged ( object sender, EventArgs e )
    {
        // TODO: Expose State, Exception and Value
        //       Do not use state machine types (i.e. BindingStatus)

        if ( stateMachine.State == ExpressionState.Exception &&
             stateMachine.TryGetException ( out var exception ) )
        {
            // TODO: Handle exception
            //       Handler should have a way to ignore exception?
            //       And if "ignored", it becomes an error here?
            //       State machine should have a way to report exceptions without capture

            // TODO: For Binding usage: If write target on the other side is "errorable",
            //       add error to target with disposable to clear error.
        }

        if ( stateMachine.State == ExpressionState.Result &&
             stateMachine.TryGetResult ( out var result ) )
        {
            // TODO: Handle result
        }
    }

    public void Bind ( TSource source )
    {
        stateMachine.Set      ( 0, source );
        stateMachine.MoveNext ( );
    }

    public void Unbind ( )
    {
        stateMachine.Reset ( );
    }

    public bool Schedule < T > ( int id, T instance, MemberInfo member )
    {
        // TODO: Schedule, MoveNext
        // TODO: Subscribe, Clear ( id ) and MoveNext on callback
        return false;
    }

    public bool Await < T > ( int id, T value )
    {
        // TODO: Await, MoveNext on callback
        // TODO: Validate async,
        //       then, add error for id/member
        //       Get information for context from metadata
        return false;
    }

    public bool SetValue ( TValue value )
    {
        return setter != null && setter.SetValue ( stateMachine, value );
    }

    // TODO: Expose Validation Add/Remove error for assigning from other side

    public void Dispose ( )
    {
        stateMachine.StateChanged -= StateChanged;

        disposables.Dispose ( );
    }
}