using System.Runtime.ExceptionServices;

namespace Epoxide.Linq.Expressions;

public interface IBindingState
{
    // TODO: Attach/Detach + Services here?

    public object? TryGetValue ( int id ) { return null; }
    public void    Assign      ( int id, object? value ) { }
}

// TODO: Need TState, and TSource for ParameterExpression
// TODO: Rename IScheduledExpression? SchedulableExpression?
public interface IBindingExpression
{
    IBindingState   State    { get; }
    IBinderServices Services { get; }

    // TODO: Attach/Detach here?
    void Await ( IAwaitable awaitable )
    {

    }

    void Callback ( bool success, object? value, ExceptionDispatchInfo? exception )
    {

    }
}

public class SchedulableContext
{
    private static readonly MethodInfo   callback  = typeof ( IBindingExpression ).GetMethod   ( nameof ( IBindingExpression.Callback ) );
    private static readonly MethodInfo   await     = typeof ( IBindingExpression ).GetMethod ( nameof ( IBindingExpression.Await ) );

    private int id = 0;
    public ParameterExpression State { get; } = Expression.Parameter ( typeof ( IBindingExpression ), "state" );
    public int GetNextId ( ) => id++;

    private HashSet < ParameterExpression > parameters = new ( );

    public void AddParameter ( ParameterExpression parameter )
    {
        parameters.Add ( parameter );
    }

    public Expression Failure ( Expression expression )
    {
        return Expression.Call ( State, callback,
                                 Expression.Constant ( false ),
                                 Expression.Constant ( null ),
                                 Expression.Constant ( null, typeof ( ExceptionDispatchInfo ) ) );
    }

    public Expression Fault ( Expression expression, Expression exception )
    {
        return Expression.Call ( State, callback,
                                 Expression.Constant ( false ),
                                 Expression.Constant ( null ),
                                 exception );
    }

    public Expression Success ( Expression expression, Expression value )
    {
        return Expression.Call ( State, callback,
                                 Expression.Constant ( true ),
                                 value,
                                 Expression.Constant ( null, typeof ( ExceptionDispatchInfo ) ) );
    }

    public Expression Await ( Expression expression, Expression awaitable )
    {
        return Expression.Call ( State, await, awaitable );
    }
}
    
// TODO: Rename Binding something? or Schedulable.ToScheduled
public static class Schedulable
{
    public static Expression AsSchedulable ( this ParameterExpression parameter, SchedulableContext context )
    {
        context.AddParameter ( parameter );

        return parameter;
    }

    public static Expression AsSchedulable ( this MemberExpression member, Expression? expression, SchedulableContext context )
    {
        return MakeSchedulable ( member, member.Expression, expression, context );
    }

    public static Expression AsSchedulable ( this MethodCallExpression method, Expression? @object, IEnumerable < Expression > arguments, SchedulableContext context )
    {
        return MakeSchedulable ( method, method.Object, @object, method.Arguments, arguments, context );
    }

    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, SchedulableContext context )
    {
        if ( instance != null && propagatedInstance != null && propagatedInstance.IsNullable ( ) )
            return MakeSingleSchedulable ( access, instance, propagatedInstance, context );

        return access;
    }

    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, IReadOnlyCollection < Expression > arguments, IEnumerable < Expression > propagatedArguments, SchedulableContext context )
    {
        var instances           = new List < Expression > ( );
        var propagatedInstances = new List < Expression > ( );

        if ( instance != null && propagatedInstance != null && propagatedInstance.IsNullable ( ) )
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

            if ( propagatedArgumentsEnumerator.Current.IsNullable ( ) )
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

    // TODO: Handle exceptions
    // TODO: Use binding state (TryGetValue/SetValue)
    // TODO: Needs id in Await
    // TODO: Add scheduler support
    private static Expression MakeSingleSchedulable ( Expression access, Expression instance, Expression propagatedInstance, SchedulableContext context )
    {
        if ( instance == propagatedInstance )
        {
            access = access.MakeNullable ( );

            var accessed = Expression.Variable ( access.Type, GenerateVariableName ( access, access, context ) );
            var assignAccess = Expression.Assign   ( accessed, access );

            var awaitTest = Expression.Condition ( test:    Expression.TypeIs    ( accessed, typeof ( IAwaitable ) ),
                                           ifTrue:  context.Await   ( propagatedInstance, Expression.Convert ( accessed, typeof ( IAwaitable ) ) ),
                                           ifFalse: context.Success ( propagatedInstance, accessed ) );
            var nullTest  = Expression.Condition ( test:    Expression.Equal ( propagatedInstance, Null ),
                                               ifTrue:  context.Failure  ( propagatedInstance ),
                                               ifFalse: awaitTest );

            return Expression.Block ( type:        nullTest.Type,
                                      variables:   new [ ] { accessed },
                                      expressions: new Expression [ ]
                                      {
                                          assignAccess,
                                          nullTest
                                      } );
        }

        var variable = Expression.Variable ( propagatedInstance.Type, GenerateVariableName ( instance, propagatedInstance, context ) );
        var assign   = Expression.Assign   ( variable, propagatedInstance );

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var accessed2 = Expression.Variable ( access.Type, GenerateVariableName ( access, access, context ) );
        var assignAccess2 = Expression.Assign   ( accessed2, access );

        var awaitTest2 = Expression.Condition ( test:    Expression.TypeIs    ( accessed2, typeof ( IAwaitable ) ),
                                           ifTrue:  context.Await   ( propagatedInstance, Expression.Convert ( accessed2, typeof ( IAwaitable ) ) ),
                                           ifFalse: context.Success ( propagatedInstance, accessed2 ) );
        var nullTest2  = Expression.Condition ( test:    Expression.Equal ( propagatedInstance, Null ),
                                           ifTrue:  context.Failure  ( propagatedInstance ),
                                           ifFalse: awaitTest2 );

        return Expression.Block ( type:        nullTest2.Type,
                                  variables:   new [ ] { variable, accessed2 },
                                  expressions: new Expression [ ]
                                  {
                                      assign,
                                      assignAccess2,
                                      nullTest2
                                  } );
    }

    // TODO: Handle exceptions
    // TODO: Use binding state (TryGetValue/SetValue)
    // TODO: Needs id in Await
    // TODO: Add scheduler support
    // TODO: Implement IAwaitable support
    private static Expression MakeMultipleSchedulables ( Expression access, List < Expression > instance, List < Expression > propagatedInstance, SchedulableContext context )
    {
        var variables   = new ParameterExpression [ instance.Count ];
        var expressions = new Expression          [ propagatedInstance.Count + 1 ];

        for ( var index = 0; index < variables.Length; index++ )
        {
            // TODO: Remove multiple array accesses
            variables   [ index ] = Expression.Variable ( propagatedInstance [ index ].Type, GenerateVariableName ( instance [ index ], propagatedInstance [ index ], context ) );
            expressions [ index ] = Expression.Assign   ( variables [ index ], propagatedInstance [ index ] );
        }

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            var index = instance.IndexOf ( node );
            if ( index >= 0 )
                return instance [ index ].IsNullableStruct ( ) ? variables [ index ] : variables [ index ].RemoveNullable ( );

            return node;
        }

        var test      = variables.Select    ( variable => Expression.Equal ( variable, Null ) )
                                 .Aggregate ( Expression.OrElse );
        var condition = Expression.Condition ( test:    test,
                                               ifTrue:  Expression.Constant ( null, access.Type ),
                                               ifFalse: access );

        expressions [ ^1 ] = condition;

        return Expression.Block ( type:        access.Type,
                                  variables:   variables,
                                  expressions: expressions );
    }

    // TODO: Deal with ` and To[A-Z] methods
    private static string GenerateVariableName ( Expression instance, Expression propagatedInstance, SchedulableContext context )
    {
        var name = instance.Type.Name;
        if ( instance.NodeType == ExpressionType.MemberAccess )
            name = ( (MemberExpression) instance ).Member.Name;

        if ( char.IsUpper ( name [ 0 ] ) )
            name = char.ToLowerInvariant ( name [ 0 ] ) + name.Substring ( 1 );

        if ( propagatedInstance.NodeType == ExpressionType.Block )
            if ( ( (BlockExpression) propagatedInstance ).Variables.LastOrDefault ( variable => variable.Name.StartsWith ( name, StringComparison.Ordinal ) ) is { } match )
                name += int.TryParse ( match.Name.AsSpan ( name.Length ), out var index ) ? index + 1 : 2;

        return name;
    }
}