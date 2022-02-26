﻿using System.Runtime.CompilerServices;

namespace Epoxide.Linq.Expressions;

public static class ExpressionExtensions
{
    public static bool IsClosure ( this Expression node )
    {
        return Attribute.IsDefined ( node.Type, typeof ( CompilerGeneratedAttribute ) );
    }

    public static Expression MakeNullable ( this Expression node )
    {
        if ( node.IsNullable ( ) )
            return node;

        return Expression.Convert ( node, typeof ( Nullable < > ).MakeGenericType ( node.Type ) );
    }

    public static bool IsNullable ( this Expression node )
    {
        return node.Type.IsValueType ? Nullable.GetUnderlyingType ( node.Type ) != null : ! IsClosure ( node );
    }

    public static bool IsNullableStruct ( this Expression node )
    {
        return node.Type.IsValueType && Nullable.GetUnderlyingType ( node.Type ) != null;
    }

    public static Expression RemoveNullable ( this Expression node )
    {
        if ( node.IsNullableStruct ( ) )
            return Expression.Convert ( node, node.Type.GenericTypeArguments [ 0 ] );

        return node;
    }

    public static bool IsCollection ( this Expression node )
    {
        return node.Type.GetGenericInterfaceArguments ( typeof ( ICollection         < > ) ) != null ||
               node.Type.GetGenericInterfaceArguments ( typeof ( IReadOnlyCollection < > ) ) != null;
    }

    public static MemberExpression? ToWritable ( this Expression node )
    {
        while ( node.NodeType == ExpressionType.Convert )
            node = ( (UnaryExpression) node ).Operand;

        if ( node.NodeType != ExpressionType.MemberAccess )
            return null;

        var memberAccess = (MemberExpression) node;
        if ( memberAccess.Member is PropertyInfo { CanWrite: true } or FieldInfo )
            return memberAccess;

        return null;
    }

    public static Expression PropagateNull ( this BinaryExpression binary, Expression left, Expression right )
    {
        if ( binary.NodeType == ExpressionType.Coalesce )
        {
            if ( binary.Left == left && binary.Right == right && left.IsNullable ( ) && right.IsNullable ( ) )
                return binary;

            return Expression.Coalesce ( left .MakeNullable ( ),
                                         right.MakeNullable ( ) );
        }

        if ( binary.Left == left && binary.Right == right )
            return binary;

        return Expression.MakeBinary ( binary.NodeType, left, right, binary.IsLiftedToNull, binary.Method, binary.Conversion );
    }

    public static Expression PropagateNull ( this ConditionalExpression condition, Expression ifTrue, Expression ifFalse )
    {
        if ( condition.IfTrue == ifTrue && condition.IfFalse == ifFalse && ifTrue.IsNullable ( ) && ifFalse.IsNullable ( ) )
            return condition;

        return Expression.Condition ( condition.Test, ifTrue.MakeNullable ( ), ifFalse.MakeNullable ( ) );
    }

    public static Expression PropagateNull ( this MemberExpression member, Expression? expression )
    {
        return PropagateNullIfNullable ( member, member.Expression, expression );
    }

    public static Expression PropagateNull ( this MethodCallExpression method, Expression? @object, IEnumerable < Expression > arguments )
    {
        return PropagateNullIfNullable ( method, method.Object, @object, method.Arguments, arguments );
    }

    private static Expression PropagateNullIfNullable ( Expression access, Expression? instance, Expression? propagatedInstance )
    {
        if ( instance != null && propagatedInstance != null && propagatedInstance.IsNullable ( ) )
            return PropagateSingleNull ( access, instance, propagatedInstance );

        return access;
    }

    private static Expression PropagateNullIfNullable ( Expression access, Expression? instance, Expression? propagatedInstance, IReadOnlyCollection < Expression > arguments, IEnumerable < Expression > propagatedArguments )
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

        return instances.Count == 1 ? PropagateSingleNull   ( access, instances [ 0 ], propagatedInstances [ 0 ] ) :
               instances.Count >  1 ? PropagateMultipleNull ( access, instances,       propagatedInstances ) :
               access;
    }

    private readonly static ConstantExpression Null = Expression.Constant ( null );

    private static Expression PropagateSingleNull ( Expression access, Expression instance, Expression propagatedInstance )
    {
        if ( instance == propagatedInstance )
        {
            access = access.MakeNullable ( );

            return Expression.Condition ( test:    Expression.Equal    ( instance, Null ),
                                          ifTrue:  Expression.Constant ( null, access.Type ),
                                          ifFalse: access );
        }

        var variable = Expression.Variable ( propagatedInstance.Type, GenerateVariableName ( instance, propagatedInstance ) );
        var assign   = Expression.Assign   ( variable, propagatedInstance );

        access = new ExpressionReplacer ( Replace ).Visit ( access ).MakeNullable ( );

        Expression Replace ( Expression node )
        {
            if ( node == instance )
                return instance.IsNullableStruct ( ) ? variable : variable.RemoveNullable ( );

            return node;
        }

        var test      = Expression.Equal ( variable, Null );
        var condition = Expression.Condition ( test:    test,
                                               ifTrue:  Expression.Constant ( null, access.Type ),
                                               ifFalse: access );

        return Expression.Block ( type:        access.Type,
                                  variables:   new [ ] { variable },
                                  expressions: new Expression [ ]
                                  {
                                      assign,
                                      condition
                                  } );
    }

    private static Expression PropagateMultipleNull ( Expression access, List < Expression > instance, List < Expression > propagatedInstance )
    {
        var variables   = new ParameterExpression [ instance.Count ];
        var expressions = new Expression          [ propagatedInstance.Count + 1 ];

        for ( var index = 0; index < variables.Length; index++ )
        {
            variables   [ index ] = Expression.Variable ( propagatedInstance [ index ].Type, GenerateVariableName ( instance [ index ], propagatedInstance [ index ] ) );
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
    private static string GenerateVariableName ( Expression instance, Expression propagatedInstance )
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