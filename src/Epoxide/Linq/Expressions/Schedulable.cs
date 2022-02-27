﻿using System.Runtime.ExceptionServices;

namespace Epoxide.Linq.Expressions;

// TODO: Rename something without machine...
public interface IStateMachine
{
    bool Read  ( int id, out object? value );
    void Write ( int id, object? value );

    bool Schedule ( object? instance, MemberInfo member );
    bool WaitFor  ( int id, object? value );
    // bool WaitFor  ( int id, object? value, int id2, object? value2 );
    // Etc...
    bool WaitFor  ( int [ ] ids, object? [ ] values );

    BindingStatus Fallback  ( );
    BindingStatus Success   ( object? value ); // Rename?
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

public class BindingExpression : IBindingExpression, IStateMachine
{
    public IBinderServices Services => throw new NotImplementedException ( );

    public BindingStatus Exception ( ExceptionDispatchInfo exception ) => throw new NotImplementedException ( );
    public BindingStatus Fallback ( ) => throw new NotImplementedException ( );
    public bool Read ( int id, out object? value ) => throw new NotImplementedException ( );
    public bool Schedule ( object? instance, MemberInfo member ) => throw new NotImplementedException ( );
    public BindingStatus Success ( object? value ) => throw new NotImplementedException ( );
    public bool WaitFor ( int id, object? value ) => throw new NotImplementedException ( );
    public bool WaitFor ( int [ ] ids, object? [ ] values ) => throw new NotImplementedException ( );
    public void Write ( int id, object? value ) => throw new NotImplementedException ( );
}

public class SchedulableContext
{
    public static readonly MethodInfo success   = typeof ( IStateMachine ).GetMethod ( nameof ( IStateMachine.Success ) );
    public static readonly MethodInfo fallback  = typeof ( IStateMachine ).GetMethod ( nameof ( IStateMachine.Fallback ) );
    public static readonly MethodInfo exception2 = typeof ( IStateMachine ).GetMethod ( nameof ( IStateMachine.Exception ) );
    public static readonly MethodInfo schedule  = typeof ( IStateMachine ).GetMethod ( nameof ( IStateMachine.Schedule ) );
    public static readonly MethodInfo waitFor   = typeof ( IStateMachine ).GetMethod ( nameof ( IStateMachine.WaitFor ), new [ ] { typeof ( int ), typeof ( object ) } );

    private int id = 0;
    public ParameterExpression State { get; } = Expression.Parameter ( typeof ( IStateMachine ), "state" );
    public int GetNextId ( ) => id++;

    private HashSet < ParameterExpression > parameters = new ( );

    public void AddParameter ( ParameterExpression parameter )
    {
        parameters.Add ( parameter );
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
        return Expression.Call ( State, success, value.Type.IsValueType ? Expression.Convert ( value, typeof ( object ) ) : value );
    }

    public Expression Schedule ( Expression expression, MemberInfo member )
    {
        return Expression.Call ( State, schedule, expression, Expression.Constant ( member ) );
    }

    public Expression WaitFor ( int index, Expression expression )
    {
        return Expression.Call ( State, waitFor, Expression.Constant ( index ), expression );
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

    // TODO: Handle exceptions
    // TODO: Use binding state (TryGetValue/SetValue)
    private static Expression MakeSingleSchedulable ( Expression access, Expression instance, Expression propagatedInstance, SchedulableContext context )
    {
        var id         = context.GetNextId ( );
        var propagated = propagatedInstance.NodeType == ExpressionType.Block;
        var variable   = propagated ? ( (BlockExpression) propagatedInstance ).Variables.LastOrDefault ( ) :
                                      Expression.Variable ( instance.Type, GenerateVariableName ( instance, propagatedInstance, context ) );

        access = new ExpressionReplacer ( ReplaceInstance ).Visit ( access ).MakeNullable ( );

        Expression ReplaceInstance ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var accessed     = Expression.Variable ( access.Type, GenerateVariableName ( access, propagatedInstance, context ) );
        var assignAccess = Expression.Assign   ( accessed, access );

        var waitFor   = Expression.Condition ( test:    context.WaitFor  ( id, assignAccess ),
                                               ifTrue:  Expression.Constant ( BindingStatus.Wait ),
                                               ifFalse: context.Success  ( accessed ) );

        var member = access.NodeType == ExpressionType.MemberAccess ? ( (MemberExpression)     access ).Member :
                     access.NodeType == ExpressionType.Call         ? ( (MethodCallExpression) access ).Method :
                     throw new ArgumentException ( "Unknown access type", nameof ( access ) );

        var schedule  = Expression.Condition ( test:    context.Schedule  ( variable, member ),
                                               ifTrue:  Expression.Constant ( BindingStatus.Schedule ),
                                               ifFalse: waitFor );

        var nullTest  = Expression.Condition ( test:    Expression.Equal ( variable, Null ),
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

            return Expression.Block ( type:        nullTest.Type,
                                      variables:   block.Variables.Append ( accessed ),
                                      expressions: block.Expressions );
        }

        return Expression.Block ( type:        nullTest.Type,
                                  variables:   new [ ] { variable, accessed },
                                  expressions: new Expression [ ]
                                  {
                                      Expression.Assign ( variable, instance ),
                                      nullTest
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

    // TODO: Deal with ` and To[A-Z] methods, keywords, and fix numbering
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