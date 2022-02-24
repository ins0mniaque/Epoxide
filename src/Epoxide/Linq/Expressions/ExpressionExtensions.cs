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
}