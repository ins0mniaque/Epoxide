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

        return new BindingExpression < TSource, TValue > ( new ExpressionStateMachine < ExpressionStateMachineStore < TValue >, TValue > ( store, moveNext ) );
    }
}

public sealed class BindingExpression < TSource, TValue > : IBindingExpression < TSource, TValue >
{
    private readonly IExpressionStateMachine < TValue > stateMachine;

    public BindingExpression ( IExpressionStateMachine < TValue > stateMachine )
    {
        this.stateMachine = stateMachine;
    }

    public bool SetValue ( object? value )
    {
        // TODO: If active and writable
        return false;
    }

    public void Bind ( TSource source )
    {
        stateMachine.Set ( 0, source );

        stateMachine.MoveNext ( );
    }

    public void Unbind ( )
    {
        // TODO: Fix this
        // disposables.Clear ( );

        stateMachine.Clear ( 0 );
    }


    public void Dispose ( )
    {
        stateMachine.Dispose ( );
    }
}