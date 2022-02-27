﻿using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Epoxide.Linq.Expressions;

public static class StateMachine 
{
    public static BindingStatus MoveNext < TSource > ( this Func < TSource, IExpressionState, BindingStatus > compiled, TSource source, IExpressionState state )
    {
        try
        {
            return compiled ( source, state );
        }
        catch ( Exception exception )
        {
            state.Exception ( BindingException.Capture ( exception ) );

            return BindingStatus.Exception;
        }
    }
}

public interface IExpressionState
{
    bool Read  < T > ( int id, [ MaybeNullWhen ( true ) ] out T value );
    T    Write < T > ( int id, T value );

    bool Schedule ( object? instance, MemberInfo member );
    bool WaitFor  ( int id, object? value ); // Rename IsAsync? Remove object so it's not typed?
    // bool WaitFor  ( int id, object? value, int id2, object? value2 );
    // Etc...
    bool WaitFor  ( int [ ] ids, object? [ ] values );

    // TODO: Typed Success?
    BindingStatus Fallback  ( );
    BindingStatus Success   ( object? value ); // TODO: Rename... Result? SetResult?
    BindingStatus Exception ( ExceptionDispatchInfo exception );
}

// TODO: Rename not status...
public enum BindingStatus
{
    Fallback,
    Success,
    Exception,
    Schedule,
    Wait
}

// TODO: Need TState, and TSource for ParameterExpression
// TODO: Rename IScheduledExpression? SchedulableExpression?
public interface IBindingExpression // NOTE: Represents Binding.Side
{
    IBinderServices Services { get; }
}

// public class BindingExpression : IBindingExpression, IExpressionState { }

public class SchedulableContext
{
    public static readonly MethodInfo success   = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.Success ) );
    public static readonly MethodInfo fallback  = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.Fallback ) );
    public static readonly MethodInfo exception2 = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.Exception ) );
    public static readonly MethodInfo schedule  = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.Schedule ) );
    public static readonly MethodInfo waitFor   = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.WaitFor ), new [ ] { typeof ( int ), typeof ( object ) } );
    public static readonly MethodInfo read      = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.Read ) );
    public static readonly MethodInfo write     = typeof ( IExpressionState ).GetMethod ( nameof ( IExpressionState.Write ) );

    public ParameterExpression State { get; } = Expression.Parameter ( typeof ( IExpressionState ), "state" );
    public IReadOnlyDictionary < ParameterExpression, int > Variables => variables;

    private readonly Dictionary < ParameterExpression, int > variables = new Dictionary<ParameterExpression, int> ( );



    public int GetId ( ParameterExpression variable )
    {
        if ( ! variables.TryGetValue ( variable, out var id ) )
            variables [ variable ] = id = variables.Count;

        return id;
    }

    public Expression Assign ( int id, ParameterExpression variable, Expression value )
    {
        var typedRead  = read .MakeGenericMethod ( variable.Type );
        var typedWrite = write.MakeGenericMethod ( variable.Type );

        var readValue  = Expression.Call ( State, typedRead, Expression.Constant ( id ), variable );
        var writeValue = Expression.Call ( State, typedWrite, Expression.Constant ( id ), value );

        return Expression.Condition ( test:    readValue,
                                      ifTrue:  variable,
                                      ifFalse: writeValue );
    }

    public Expression Fallback ( )
    {
        return Expression.Call ( State, fallback );
    }

    public Expression Exception ( Expression exception )
    {
        return Expression.Call ( State, exception2, exception );
    }

    public Expression Success ( Expression value )
    {
        // TODO: Typed result
        return Expression.Call ( State, success, Box ( value ) );
    }

    public Expression Schedule ( Expression expression, MemberInfo member )
    {
        // TODO: Typed result
        return Expression.Call ( State, schedule, Box ( expression ), Expression.Constant ( member, typeof ( MemberInfo ) ) );
    }

    public Expression WaitFor ( int id, Expression expression )
    {
        // TODO: Typed result or remove object argument
        return Expression.Call ( State, waitFor, Expression.Constant ( id ), Box ( expression ) );
    }

    private static Expression Box ( Expression expression )
    {
        return expression.Type.IsValueType ? Expression.Convert ( expression, typeof ( object ) ) : expression;
    }
}
    
// TODO: Rename Binding something? or Schedulable.ToScheduled
public static class Schedulable
{
    public static Expression AsSchedulable ( this MemberExpression member, Expression? expression, SchedulableContext context )
    {
        return MakeSchedulable ( member, member.Expression, expression, context );
    }

    public static Expression AsSchedulable ( this MethodCallExpression method, Expression? @object, IEnumerable < Expression > arguments, SchedulableContext context )
    {
        return MakeSchedulable ( method, method.Object, @object, method.Arguments, arguments, context );
    }

    // TODO: This needs to replace Fallback ( ) with Success ( coalesce.Value )
    public static Expression AsSchedulable ( this BinaryExpression binary, Expression? left, Expression? right, SchedulableContext context )
    {
        return MakeSchedulable ( binary, binary.Left, left, new [ ] { binary.Right }, Enumerable.Repeat ( right, 1 ), context );
    }

    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, SchedulableContext context )
    {
        if ( instance != null && propagatedInstance != null && ( instance.IsNullable ( ) || propagatedInstance.Type == typeof ( BindingStatus ) ) )
            return MakeSingleSchedulable ( access, instance, propagatedInstance, context );

        return access;
    }

    private static Expression MakeSchedulable ( Expression access, Expression? instance, Expression? propagatedInstance, IReadOnlyCollection < Expression > arguments, IEnumerable < Expression > propagatedArguments, SchedulableContext context )
    {
        var instances           = new List < Expression > ( );
        var propagatedInstances = new List < Expression > ( );

        if ( instance != null && propagatedInstance != null && ( instance.IsNullable ( ) || propagatedInstance.Type == typeof ( BindingStatus ) ) )
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

            if ( argumentsEnumerator.Current.IsNullable ( ) || propagatedArgumentsEnumerator.Current.Type == typeof ( BindingStatus ) )
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
    private static Expression MakeSingleSchedulable ( Expression access, Expression instance, Expression propagatedInstance, SchedulableContext context )
    {
        var propagated = propagatedInstance.NodeType == ExpressionType.Block;
        var variable   = propagated ? ( (BlockExpression) propagatedInstance ).Variables.LastOrDefault ( ) :
                                      Expression.Variable ( instance.Type, GenerateVariableName ( instance, context ) );
        var id         = context.GetId ( variable );

        access = new ExpressionReplacer ( ReplaceInstance ).Visit ( access ).MakeNullable ( );

        Expression ReplaceInstance ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var accessed     = Expression.Variable ( access.Type, GenerateVariableName ( access, context ) );
        var accessedId   = context.GetId ( accessed );
        var assignAccess = Expression.Assign   ( accessed, context.Assign ( accessedId, accessed, access ) );

        var waitFor   = Expression.Condition ( test:    context.WaitFor  ( accessedId, assignAccess ),
                                               ifTrue:  Expression.Constant ( BindingStatus.Wait ),
                                               ifFalse: context.Success  ( accessed ) );

        var member    = GetAccessedMember ( access );
        var schedule  = Expression.Condition ( test:    context.Schedule  ( variable, member ),
                                               ifTrue:  Expression.Constant ( BindingStatus.Schedule ),
                                               ifFalse: waitFor );

        var assigned  = propagated ? (Expression) variable : Expression.Assign ( variable, context.Assign ( id, variable, instance ) );
        var nullTest  = Expression.Condition ( test:    Expression.Equal ( assigned, Null ),
                                               ifTrue:  context.Fallback ( ),
                                               ifFalse: schedule );

        if ( propagated )
        {
            var block = (BlockExpression) new ExpressionReplacer ( ReplaceSuccess ).Visit ( propagatedInstance );

            Expression ReplaceSuccess ( Expression node )
            {
                if ( node.NodeType == ExpressionType.Call && ((MethodCallExpression) node).Method == SchedulableContext.success )
                    return nullTest;

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

    private static Expression MakeMultipleSchedulables ( Expression access, List < Expression > instance, List < Expression > propagatedInstance, SchedulableContext context )
    {
        var variables = new ParameterExpression [ instance.Count ];

        for ( var index = 0; index < variables.Length; index++ )
        {
            // TODO: Remove multiple array accesses
            var propagated = propagatedInstance [ index ].NodeType == ExpressionType.Block;
            var variable   = propagated ? ( (BlockExpression) propagatedInstance [ index ] ).Variables.LastOrDefault ( ) :
                                          Expression.Variable ( instance [ index ].Type, GenerateVariableName ( instance [ index ], context ) );

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

        var accessed     = Expression.Variable ( access.Type, GenerateVariableName ( access, context ) );
        var accessedId   = context.GetId ( accessed );
        var assignAccess = Expression.Assign   ( accessed, context.Assign ( accessedId, accessed, access ) );

        var waitFor   = Expression.Condition ( test:    context.WaitFor  ( accessedId, assignAccess ),
                                               ifTrue:  Expression.Constant ( BindingStatus.Wait ),
                                               ifFalse: context.Success  ( accessed ) );

        var member    = GetAccessedMember ( access );
        var schedule  = Expression.Condition ( test:    variables.Select    ( variable => context.Schedule  ( variable, member ) )
                                                                 .Aggregate ( Expression.Or ),
                                               ifTrue:  Expression.Constant ( BindingStatus.Schedule ),
                                               ifFalse: waitFor );

        var assigned  = variables.Select ( (variable, index) => propagatedInstance [ index ].NodeType == ExpressionType.Block ? (Expression) variable : Expression.Assign ( variable, context.Assign ( context.GetId ( variable ), variable, instance [ index ] ) ) );
        var nullTest  = Expression.Condition ( test:    assigned.Select    ( variable => Expression.Equal ( variable, Null ) )
                                                                .Aggregate ( Expression.OrElse ),
                                               ifTrue:  context.Fallback ( ),
                                               ifFalse: schedule );

        if ( propagatedInstance.Any ( p => p.NodeType == ExpressionType.Block ) )
        {
            bool replaced;
            var vars = new List < ParameterExpression > ( );

            for ( var index = propagatedInstance.Count - 1; index >= 0; index-- )
            {
                replaced = false;

                propagatedInstance [ index ] = new ExpressionReplacer ( ReplaceSuccess ).Visit ( propagatedInstance [ index ] );

                Expression ReplaceSuccess ( Expression node )
                {
                    if ( node.NodeType == ExpressionType.Call && ((MethodCallExpression) node).Method == SchedulableContext.success )
                    {
                        replaced = true;

                        var result = index + 1 < propagatedInstance.Count ? propagatedInstance [ index + 1 ] : nullTest;

                        if ( result.NodeType == ExpressionType.Block )
                        {
                            var block = (BlockExpression) result;
                            if ( block.Expressions.Count > 1 )
                                throw new InvalidOperationException ( "Invalid propagated block" );

                            result = block.Expressions [ 0 ];
                            vars.AddRange ( block.Variables );
                        }

                        return result;
                    }
            
                    return node;
                }

                // TODO: Verify this is correct
                if ( ! replaced )
                    propagatedInstance [ index ] = index + 1 < propagatedInstance.Count ? propagatedInstance [ index + 1 ] : nullTest;
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
        while ( access.NodeType == ExpressionType.Convert )
            access = ( (UnaryExpression) access ).Operand;

        return access.NodeType == ExpressionType.MemberAccess ? ( (MemberExpression)     access ).Member :
               access.NodeType == ExpressionType.Call         ? ( (MethodCallExpression) access ).Method :
               access is BinaryExpression binary              ? binary.Method :
               throw new ArgumentException ( "Unknown access type", nameof ( access ) );
    }

    // TODO: Deal with ` and To[A-Z] methods, keywords
    private static string GenerateVariableName ( Expression instance, SchedulableContext context )
    {
        var name = instance.Type.Name;
        if ( instance.NodeType == ExpressionType.MemberAccess )
            name = ( (MemberExpression) instance ).Member.Name;

        if ( char.IsUpper ( name [ 0 ] ) )
            name = char.ToLowerInvariant ( name [ 0 ] ) + name.Substring ( 1 );

        if ( context.Variables.Keys.LastOrDefault ( variable => variable.Name.StartsWith ( name, StringComparison.Ordinal ) ) is { } match )
            name += int.TryParse ( match.Name.AsSpan ( name.Length ), out var index ) ? index + 1 : 2;

        return name;
    }
}