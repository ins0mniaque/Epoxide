using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Epoxide.Linq.Expressions;

// TODO: If multiple scheduler are required by multiple Schedule call,
//       schedule them all...
// NOTE: Schedule is also used for change tracking
public interface IExpressionStateMachine
{
    bool Get < T > ( int id, [ MaybeNullWhen ( true ) ] out T value );
    T    Set < T > ( int id, T value );

    bool Schedule < T > ( int id, T instance, MemberInfo member );
    bool Await    < T > ( int id, T value );

    ExpressionState SetException    ( ExceptionDispatchInfo exception );
    ExpressionState SetResult < T > ( T value );
}

// TODO: Reorder/rename values
public enum ExpressionState
{
    Inactive,
    Fallback,
    Active,
    Error,
    Scheduled,
    Awaiting
}

// TODO: Need TState?
// TODO: Need interface with IBindableEnumerable
public interface IBindingExpression < TSource > : IDisposable // NOTE: Represents Binding.Side
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

public interface IExpressionStateMachineStore
{
    IExpressionStateMachine StateMachine { get; init; }

    bool Get   ( int id, [ MaybeNullWhen ( true ) ] out object? value );
    void Set   ( int id, object? value );
    void Clear ( int id );
}

// TODO: Replace state with typed state visitor
// TODO: Automatically generate the stores 
public struct ExpressionStateMachineStore < T0 > : IExpressionStateMachine, IExpressionStateMachineStore
{
    public IExpressionStateMachine StateMachine { get; init; }

    // TODO: public?
    bool has0;
    T0   var0;

    public bool Get<T> ( int id, [MaybeNullWhen ( true )] out T value ) => throw new NotSupportedException ( );
    public T Set<T> ( int id, T value ) => throw new NotSupportedException ( );
    public bool Await<T> ( int id, T value ) => StateMachine.Await ( id, value );
    public bool Schedule<T> ( int id, T instance, MemberInfo member ) => StateMachine.Schedule ( id, instance, member );
    public ExpressionState SetException ( ExceptionDispatchInfo exception ) => StateMachine.SetException ( exception );
    public ExpressionState SetResult<T> ( T value ) => StateMachine.SetResult ( value );

    public bool Get ( int id, [ MaybeNullWhen ( true ) ] out object? value )
    {
        switch(id)
        {
            case 0: value = var0; return has0;
            default: value = default; return false;
        }
    }

    public void Set ( int id, object? value )
    {
        switch(id)
        {
            case 0: var0 = (T0) value; has0 = true; break;
        }
    }

    public void Clear ( int id )
    {
        switch ( id )
        {
            case 0: var0 = default; has0 = false; break;
        }
    }
}

public sealed class BindingExpression < TSource > : IBindingExpression < TSource >
{
    // Needs Read/Write like accessor
    // Schedule: ISchedulerSelector, then schedule read and add to container, also hook member, to container.
    // Await: IAsyncValue: ... container.
    // Invalidation from hook: unset value and read again
    // Manual invalidation: Only by instance + member?
    // IBindableEnumerable: Only needs CollectionSubscriber and Attach/Detach
    //                      IBindableEnumerableSubscriber? implemented by this? (Not IBinding)
    //                      - Has Bind/Unbind too?

    // IExpressionStateMachine Store => Wrapper over itself with store separated

    // TODO: Dispose container
    public void Dispose ( ) { }
}

// TODO: Reuse variables when expression fingerprint matches
public class ExpressionStateMachineBuilderContext
{
    public static readonly MethodInfo result   = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.SetResult ) );
    public static readonly MethodInfo exception2 = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.SetException ) );
    public static readonly MethodInfo schedule  = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Schedule ) );
    public static readonly MethodInfo waitFor   = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Await ) );
    public static readonly MethodInfo read      = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Get ) );
    public static readonly MethodInfo write     = typeof ( IExpressionStateMachine ).GetMethod ( nameof ( IExpressionStateMachine.Set ) );

    public ParameterExpression StateMachine { get; } = Expression.Parameter ( typeof ( IExpressionStateMachine ), "λ" );

    public IReadOnlyDictionary < ParameterExpression, int > Variables => variables;

    public MemberExpression? WritableExpression    { get; set; }
    public int?              WritableTargetId      { get; private set; }
    public int?              WritableTargetValueId { get; private set; }

    private readonly Dictionary < ParameterExpression, int > variables = new Dictionary<ParameterExpression, int> ( );

    public int GetId ( ParameterExpression variable )
    {
        if ( ! variables.TryGetValue ( variable, out var id ) )
            variables [ variable ] = id = variables.Count;

        return id;
    }

    private MemberExpression? WritableExpressionValue { get; set; }

    public Expression Assign ( int id, ParameterExpression variable, Expression value )
    {
        if ( WritableExpressionValue != null && variable == WritableExpressionValue.Expression )
            WritableTargetId = id;

        var unconverted = value.Unconvert ( );

        if ( WritableExpression != null && unconverted.NodeType == ExpressionType.MemberAccess )
        {
            var valueMember = (MemberExpression) unconverted;
            if ( valueMember.Member == WritableExpression.Member )
            {
                WritableExpressionValue = valueMember;
                WritableTargetValueId   = id;
            }
        }

        var typedRead  = read .MakeGenericMethod ( variable.Type );
        var typedWrite = write.MakeGenericMethod ( variable.Type );

        var readValue  = Expression.Call ( StateMachine, typedRead, Expression.Constant ( id ), variable );
        var writeValue = Expression.Call ( StateMachine, typedWrite, Expression.Constant ( id ), value );

        return Expression.Condition ( test:    readValue,
                                      ifTrue:  variable,
                                      ifFalse: writeValue );
    }

    public Expression SetException ( Expression exception )
    {
        // TODO: Static
        var capture = typeof ( BindingException ).GetMethod ( nameof ( BindingException.Capture ) );

        return Expression.Call ( StateMachine, exception2, Expression.Call ( capture, exception ) );
    }

    public Expression SetResult ( Expression value )
    {
        return Expression.Call ( StateMachine, result.MakeGenericMethod ( value.Type ), value );
    }

    public Expression Schedule ( int id, Expression expression, MemberInfo member )
    {
        return Expression.Call ( StateMachine, schedule.MakeGenericMethod ( expression.Type ), Expression.Constant ( id ), expression, Expression.Constant ( member, typeof ( MemberInfo ) ) );
    }

    public Expression Await ( int id, Expression expression )
    {
        return Expression.Call ( StateMachine, waitFor.MakeGenericMethod ( expression.Type ), Expression.Constant ( id ), expression );
    }
}

public static class StateMachineBuilder
{
    public static Expression MakeStateMachine ( this Expression expression, ExpressionStateMachineBuilderContext context )
    {
        if ( expression.Type == typeof ( ExpressionState ) )
            return expression;

        return Expression.Condition ( test:    context.Await ( -1, expression ),
                                      ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                      ifFalse: context.SetResult ( expression ) );
    }

    public static Expression AddStateMachineExceptionHandling ( this Expression expression, ExpressionStateMachineBuilderContext context )
    {
        var exception  = Expression.Parameter ( typeof ( Exception ), "exception" );
        var catchBlock = Expression.Catch     ( exception, context.SetException ( exception ) );

        return Expression.TryCatch ( expression, catchBlock );
    }

    public static Expression ToStateMachine ( this MemberExpression member, Expression? expression, ExpressionStateMachineBuilderContext context )
    {
        return MakeSchedulable ( member, member.Expression, expression, context );
    }

    public static Expression ToStateMachine ( this MethodCallExpression method, Expression? @object, IEnumerable < Expression > arguments, ExpressionStateMachineBuilderContext context )
    {
        return MakeSchedulable ( method, method.Object, @object, method.Arguments, arguments, context );
    }

    public static Expression ToStateMachine ( this BinaryExpression binary, Expression? left, Expression? right, ExpressionStateMachineBuilderContext context )
    {
        var isCoalesceToNull = binary.NodeType == ExpressionType.Coalesce &&
                               right .NodeType == ExpressionType.Constant &&
                               ( (ConstantExpression) right ).Value == null;

        if ( isCoalesceToNull && left.NodeType == ExpressionType.Block )
        {
            var replaced = new List < ParameterExpression > ( );

            left = new ReversedExpressionReplacer ( RemoveFallbacks ).Visit ( left );

            Expression RemoveFallbacks ( Expression node )
            {
                if ( node.NodeType == ExpressionType.Conditional )
                {
                    var condition = (ConditionalExpression) node;

                    // TODO: Compare with stored ConstantExpression for Fallback
                    if ( condition.IfTrue.NodeType == ExpressionType.Constant && Equals ( ( (ConstantExpression) condition.IfTrue ).Value, ExpressionState.Fallback ) )
                    {
                        var assign           = ( (BinaryExpression) condition.Test ).Left;
                        var variable         = assign.NodeType == ExpressionType.Parameter ? (ParameterExpression) assign : (ParameterExpression) ( (BinaryExpression) assign ).Left;
                        var waitForCondition = (ConditionalExpression) condition.IfFalse;
                        var waitFor          = (MethodCallExpression)  waitForCondition.Test;

                        replaced.Add ( variable );

                        return Expression.Condition ( test:     Expression.Call ( waitFor.Object, waitFor.Method, waitFor.Arguments [ 0 ], assign ),
                                                      ifTrue:   waitForCondition.IfTrue,
                                                      ifFalse : waitForCondition.IfFalse );
                    }
                }

                return node;
            }

            // TODO: This needs force type change with MakeNullable, and RemoveNullable at usage
            left = new ExpressionReplacer ( CoalesceAccess ).Visit ( left );

            Expression CoalesceAccess ( Expression node )
            {
                if ( node.NodeType == ExpressionType.MemberAccess )
                {
                    var member = (MemberExpression) node;
                    if ( member.Expression != null && member.Expression.NodeType == ExpressionType.Parameter && replaced.Contains ( member.Expression ) )
                        return Expression.Condition ( test:    Expression.Equal ( member.Expression, Null ),
                                                      ifTrue:  node,
                                                      ifFalse: Expression.Constant ( null, node.Type ) );
                }

                if ( node.NodeType == ExpressionType.Call )
                {
                    var method = (MethodCallExpression) node;
                    if ( method.Object != null && method.Object.NodeType == ExpressionType.Parameter && replaced.Contains ( method.Object ) )
                        return Expression.Condition ( test:    Expression.Equal ( method.Object, Null ),
                                                      ifTrue:  node,
                                                      ifFalse: Expression.Constant ( null, node.Type ) );
                }

                return node;
            }
        
            return left;
        }

        return MakeSchedulable ( binary, binary.Left, left, new [ ] { binary.Right }, Enumerable.Repeat ( right, 1 ), context );
    }

    // TODO: Replace IsNullable with IsSchedulable
    // TODO: Rename
    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, ExpressionStateMachineBuilderContext context )
    {
        if ( instance != null && propagatedInstance != null && ( instance.CanBeNull ( ) || propagatedInstance.Type == typeof ( ExpressionState ) ) )
            return MakeSingleSchedulable ( access, instance, propagatedInstance, context );

        return access;
    }

    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, IReadOnlyCollection < Expression > arguments, IEnumerable < Expression > propagatedArguments, ExpressionStateMachineBuilderContext context )
    {
        var instances           = new List < Expression > ( );
        var propagatedInstances = new List < Expression > ( );

        if ( instance != null && propagatedInstance != null && ( instance.CanBeNull ( ) || propagatedInstance.Type == typeof ( ExpressionState ) ) )
        {
            instances          .Add ( instance );
            propagatedInstances.Add ( propagatedInstance );
        }

        using var argumentsEnumerator           = arguments          .GetEnumerator ( );
        using var propagatedArgumentsEnumerator = propagatedArguments.GetEnumerator ( );

        while ( argumentsEnumerator.MoveNext ( ) )
        {
            if ( ! propagatedArgumentsEnumerator.MoveNext ( ) )
                throw new ArgumentException ( "Less propagated arguments than arguments were provided", nameof ( propagatedArguments ) );

            if ( argumentsEnumerator.Current.CanBeNull ( ) || propagatedArgumentsEnumerator.Current.Type == typeof ( ExpressionState ) )
            {
                instances          .Add ( argumentsEnumerator          .Current );
                propagatedInstances.Add ( propagatedArgumentsEnumerator.Current );
            }
        }

        return instances.Count == 1 ? MakeSingleSchedulable    ( access, instances [ 0 ], propagatedInstances [ 0 ], context ) :
               instances.Count >  1 ? MakeMultipleSchedulables ( access, instances,       propagatedInstances,       context ) :
               access;
    }

    private readonly static ConstantExpression Null = Expression.Constant ( null );

    // TODO: Clean up propagated instance detection (must return BindingStatus)
    private static Expression MakeSingleSchedulable ( Expression access, Expression instance, Expression propagatedInstance, ExpressionStateMachineBuilderContext context )
    {
        var propagated = propagatedInstance.NodeType == ExpressionType.Block;
        var variable   = propagated ? ( (BlockExpression) propagatedInstance ).Variables.LastOrDefault ( ) :
                                      instance.GenerateVariable ( context.Variables.Keys );
        var id         = context.GetId ( variable );

        access = new ExpressionReplacer ( ReplaceInstance ).Visit ( access ).MakeNullable ( );

        Expression ReplaceInstance ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var accessed     = access.GenerateVariable ( context.Variables.Keys );
        var accessedId   = context.GetId ( accessed );
        var assignAccess = Expression.Assign   ( accessed, context.Assign ( accessedId, accessed, access ) );

        var waitForAccess = Expression.Condition ( test:    context.Await  ( accessedId, assignAccess ),
                                                   ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                                   ifFalse: context.SetResult  ( accessed ) );

        var member    = GetAccessedMember ( access );
        var schedule  = Expression.Condition ( test:    context.Schedule  ( accessedId, variable, member ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Scheduled ),
                                               ifFalse: waitForAccess );

        var waitFor   = Expression.Condition ( test:    context.Await  ( id, variable ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                               ifFalse: schedule );

        var assigned  = propagated ? (Expression) variable : Expression.Assign ( variable, context.Assign ( id, variable, instance ) );
        var nullTest  = Expression.Condition ( test:    Expression.Equal ( assigned, Null ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Fallback ),
                                               ifFalse: waitFor );

        if ( propagated )
        {
            var block = (BlockExpression) new ExpressionReplacer ( ReplaceResult ).Visit ( propagatedInstance );

            Expression ReplaceResult ( Expression node )
            {
                if ( node.NodeType == ExpressionType.Conditional )
                {
                    var ifFalse = ( (ConditionalExpression) node ).IfFalse;
                    if ( ifFalse.NodeType == ExpressionType.Call && ( (MethodCallExpression) ifFalse ).Method.IsGenericMethod && ( (MethodCallExpression) ifFalse ).Method.GetGenericMethodDefinition ( ) == ExpressionStateMachineBuilderContext.result )
                    {
                        var waitForAccess = (MethodCallExpression) ( (ConditionalExpression) node ).Test;
                        var replacement   = (ConditionalExpression) nullTest;

                        return Expression.Condition ( test:    Expression.Equal ( waitForAccess.Arguments [ 1 ], Null ),
                                                      ifTrue:  replacement.IfTrue,
                                                      ifFalse: replacement.IfFalse );
                    }
                }

                return node;
            }

            if ( block.Expressions.Count > 1 )
                throw new InvalidOperationException ( "Invalid propagated block" );

            return Expression.Block ( type:        nullTest.Type,
                                      variables:   block.Variables.Append ( accessed ),
                                      expressions: block.Expressions );
        }

        return Expression.Block ( type:        nullTest.Type,
                                  variables:   new [ ] { variable, accessed },
                                  expressions: new Expression [ ]
                                  {
                                      nullTest
                                  } );
    }

    private static Expression MakeMultipleSchedulables ( Expression access, List < Expression > instance, List < Expression > propagatedInstance, ExpressionStateMachineBuilderContext context )
    {
        var variables = new ParameterExpression [ instance.Count ];

        for ( var index = 0; index < variables.Length; index++ )
        {
            // TODO: Remove multiple array accesses
            var propagated = propagatedInstance [ index ].NodeType == ExpressionType.Block;
            var variable   = propagated ? ( (BlockExpression) propagatedInstance [ index ] ).Variables.LastOrDefault ( ) :
                                          instance [ index ].GenerateVariable ( context.Variables.Keys );

            variables [ index ] = variable;
        }

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            var index = instance.IndexOf ( node );
            if ( index >= 0 )
                return instance [ index ].IsNullableStruct ( ) ? variables [ index ] : variables [ index ].RemoveNullable ( );

            return node;
        }

        var accessed     = access.GenerateVariable ( context.Variables.Keys );
        var accessedId   = context.GetId ( accessed );
        var assignAccess = Expression.Assign   ( accessed, context.Assign ( accessedId, accessed, access ) );

        var waitForAccess = Expression.Condition ( test:    context.Await  ( accessedId, assignAccess ),
                                                   ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                                   ifFalse: context.SetResult  ( accessed ) );

        var member    = GetAccessedMember ( access );

        var schedule  = Expression.Condition ( test:    variables.Select    ( variable => context.Schedule ( accessedId, variable, member ) )
                                                                 .Aggregate ( Expression.Or ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Scheduled ),
                                               ifFalse: waitForAccess );

        var waitFor   = Expression.Condition ( test:    variables.Select    ( variable => context.Await ( context.GetId ( variable ), variable ) )
                                                                 .Aggregate ( Expression.Or ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Awaiting ),
                                               ifFalse: schedule );

        var assigned  = variables.Select ( (variable, index) => propagatedInstance [ index ].NodeType == ExpressionType.Block ? (Expression) variable : Expression.Assign ( variable, context.Assign ( context.GetId ( variable ), variable, instance [ index ] ) ) );
        var nullTest  = Expression.Condition ( test:    assigned.Select    ( variable => Expression.Equal ( variable, Null ) )
                                                                .Aggregate ( Expression.OrElse ),
                                               ifTrue:  Expression.Constant ( ExpressionState.Fallback ),
                                               ifFalse: waitFor );

        if ( propagatedInstance.Any ( p => p.NodeType == ExpressionType.Block ) )
        {
            propagatedInstance = propagatedInstance.Where ( p => p.NodeType == ExpressionType.Block ).ToList ( );

            var vars = new List < ParameterExpression > ( );

            for ( var index = propagatedInstance.Count - 1; index >= 0; index-- )
            {
                if ( propagatedInstance [ index ].NodeType == ExpressionType.Block )
                    propagatedInstance [ index ] = new ExpressionReplacer ( ReplaceResult ).Visit ( propagatedInstance [ index ] );

                Expression ReplaceResult ( Expression node )
                {
                    if ( node.NodeType == ExpressionType.Conditional )
                    {
                        var ifFalse = ( (ConditionalExpression) node ).IfFalse;
                        if ( ifFalse.NodeType == ExpressionType.Call && ( (MethodCallExpression) ifFalse ).Method.IsGenericMethod && ( (MethodCallExpression) ifFalse ).Method.GetGenericMethodDefinition ( ) == ExpressionStateMachineBuilderContext.result )
                        {
                            var waitForAccess = (MethodCallExpression) ( (ConditionalExpression) node ).Test;
                            var replacement   = index + 1 < propagatedInstance.Count ? (ConditionalExpression) ( (BlockExpression) propagatedInstance [ index + 1 ] ).Expressions [ 0 ] : nullTest;

                            if ( index + 1 < propagatedInstance.Count )
                                vars.AddRange ( ( (BlockExpression) propagatedInstance [ index + 1 ] ).Variables );

                            return Expression.Condition ( test:    Expression.Equal ( waitForAccess.Arguments [ 1 ], Null ),
                                                          ifTrue:  replacement.IfTrue,
                                                          ifFalse: replacement.IfFalse );
                        }
                    }

                    return node;
                }
            }

            var block = (BlockExpression) propagatedInstance [ 0 ];

            return Expression.Block ( type:        nullTest.Type,
                                      variables:   block.Variables.Concat ( vars ).Append ( accessed ),
                                      expressions: block.Expressions );
        }

        return Expression.Block ( type:        nullTest.Type,
                                  variables:   variables.Append ( accessed ),
                                  expressions: nullTest );
    }

    private static MemberInfo GetAccessedMember ( Expression access )
    {
        access = access.Unconvert ( );

        return access.NodeType == ExpressionType.MemberAccess ? ( (MemberExpression)     access ).Member :
               access.NodeType == ExpressionType.Call         ? ( (MethodCallExpression) access ).Method :
               access is BinaryExpression binary              ? binary.Method :
               throw new ArgumentException ( "Unknown access type", nameof ( access ) );
    }
}