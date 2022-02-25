using System.Runtime.CompilerServices;

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
        return ! node.Type.IsValueType || Nullable.GetUnderlyingType ( node.Type ) != null;
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

    public static Expression PropagateNull ( this MemberExpression member, Expression? instance )
    {
        return instance != null ? PropagateNull ( instance, member ) : member;
    }

    // TODO: Handle arguments
    public static Expression PropagateNull ( this MethodCallExpression method, Expression? instance, IEnumerable < Expression > arguments )
    {
        return instance != null ? PropagateNull ( instance, method ) : method;
    }

    // TODO: Fix simple field accesses being transformed in variables
    private static Expression PropagateNull ( Expression instance, Expression member )
    {
        var safe    = instance;
        var caller  = Expression.Variable ( safe.Type, GenerateVariableName ( safe ) );
        var assign  = Expression.Assign   ( caller, safe );
        var cast    = instance.IsNullableStruct ( ) ? caller : caller.RemoveNullable ( );
        var access  = new ExpressionReplacer ( node => node == instance ? cast : node ).Visit ( member ).MakeNullable ( );
        var ternary = Expression.Condition ( test:    Expression.Equal ( caller, Expression.Constant ( null ) ),
                                             ifTrue:  Expression.Constant ( null, access.Type ),
                                             ifFalse: access );

        return Expression.Block ( type:        access.Type,
                                  variables:   new [ ] { caller },
                                  expressions: new Expression [ ]
                                  {
                                      assign,
                                      ternary
                                  } );
    }

    // TODO: Fix by finding member access in safe
    private static string GenerateVariableName ( Expression instance )
    {
        var name = instance.Type.Name;
        if ( instance.NodeType == ExpressionType.MemberAccess )
            name = ( (MemberExpression) instance ).Member.Name;

        if ( char.IsUpper ( name [ 0 ] ) )
            name = char.ToLowerInvariant ( name [ 0 ] ) + name.Substring ( 1 );

        if ( instance.NodeType == ExpressionType.Block )
            if ( ( (BlockExpression) instance ).Variables.LastOrDefault ( variable => variable.Name.StartsWith ( name, StringComparison.Ordinal ) ) is { } match )
                name += int.TryParse ( match.Name.AsSpan ( name.Length ), out var index ) ? index + 1 : 2;

        return name;
    }
}