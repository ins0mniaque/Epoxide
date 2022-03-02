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

// TODO: Multi-model binding expressions
public sealed class BindingExpression < TSource, TValue > : IBindingExpression < TSource, TValue >
{
    private readonly IExpressionStateMachine < TValue > stateMachine;
    private readonly IExpressionSetter < TValue >?      setter;

    public BindingExpression ( IExpressionStateMachine < TValue > stateMachine, IExpressionSetter < TValue >? setter = null )
    {
        this.stateMachine = stateMachine;
        this.setter       = setter;
    }

    public bool SetValue ( TValue value )
    {
        return setter != null && setter.SetValue ( stateMachine, value );
    }

    public void Bind ( TSource source )
    {
        stateMachine.Set ( 0, source );

        stateMachine.MoveNext ( );

        // TODO: Hook stateMachine.StateChanged
    }

    public void Unbind ( )
    {
        stateMachine.Cancel ( );
    }

    public void Dispose ( )
    {
        stateMachine.Dispose ( );
    }
}