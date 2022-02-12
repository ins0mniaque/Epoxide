using System.Linq.Expressions;
using System.Reflection;

namespace Epoxide;

public class SentinelPropogationVisitor : MemberNullPropogationVisitor
{
    public static object Sentinel { get; } = new ( );

    public Expression VisitAndAddSentinelSupport ( Expression node )
    {
        var x = Visit ( node );

        // NOTE: Remove right part check?
        if ( node.NodeType == ExpressionType.Coalesce && ((BinaryExpression)node).Right is ConstantExpression e && e.Value == null )
            return x;

        if ( x.Type != typeof(object))
            x = Expression.Convert ( x, typeof(object) );
        return Expression.Coalesce ( x, Expression.Constant (Sentinel ) );
    }

    protected override Expression VisitBinary ( BinaryExpression node )
    {
        if ( node.NodeType == ExpressionType.Coalesce && node.Right is ConstantExpression e && e.Value == null )
            return new MemberNullPropogationVisitor ( ).Visit ( node.Left );

        return base.VisitBinary ( node );
    }
}

public class EnumerableToQueryableVisitor : ExpressionVisitor
{
    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        if ( node.Method.DeclaringType == typeof ( Enumerable ) )
        {
            var test = FindQueryableMethod(node.Method.Name, node.Method.GetParameters (  ).Select ( p => p.ParameterType ).ToArray ( ));
            if ( test == null )
                return base.VisitMethodCall ( node );

            var arg0 = node.Arguments[0];

            arg0 = Visit ( arg0 );

            var asBindable = typeof(BindableQueryable).GetMethod ( nameof(BindableQueryable.AsBindable) ).MakeGenericMethod ( arg0.Type.GetGenericArguments (  )[0]);

            arg0 = typeof ( IQueryable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0 );

            var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : typeof ( Expression ).IsAssignableFrom ( a.Type ) ? Expression.Lambda ( a ) : a );

            return Expression.Call ( test, arguments );
        }

        return base.VisitMethodCall ( node );
    }

    private static ILookup<string, MethodInfo> s_seqMethods;
    private static HashSet<string> s_nonSeqMethods;
    private static MethodInfo FindQueryableMethod(string name, params Type[] typeArgs)
    {
        var generic = typeArgs.Skip ( 1 ).Select ( p => p.IsGenericType ? p.GetGenericTypeDefinition ( ) : p ).ToArray ( );
        var asExpr =  typeArgs.Length > 1 && typeArgs [ 1 ].IsGenericType ? typeArgs [ 1 ].GetGenericArguments ( ) :
                      typeArgs [ 0 ].GetGenericArguments ( );
        if (s_seqMethods == null)
        {
            s_seqMethods = typeof(Queryable).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Concat ( typeof(BindableQueryable).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) )
                .ToLookup(m => m.Name);
            s_nonSeqMethods = typeof(Enumerable).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => m.Name).Except ( s_seqMethods.Select ( a => a.Key ) ).ToHashSet ( );
        }

        if ( s_nonSeqMethods.Contains ( name ) )
            return null;

        var mi = s_seqMethods[name].FirstOrDefault(m => m.GetParameters (  ).Skip ( 1 ).Select ( p => p.ParameterType.IsGenericType ? p.ParameterType.GetGenericArguments (  )[0].GetGenericTypeDefinition ( ) : p.ParameterType) .SequenceEqual ( generic));

        if (asExpr != null)
            return mi.MakeGenericMethod(asExpr);
        return mi;
    }
}

public class MemberNullPropogationVisitor : ExpressionVisitor
{
    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == null || !IsNullable(node.Expression.Type))
            return base.VisitMember(node);

        var expression = base.Visit(node.Expression);
        var nullBaseExpression = Expression.Constant(null, expression.Type);
        var test = Expression.ReferenceEqual(expression, nullBaseExpression);
        var outputType = node.Type;
        if (!IsNullable ( outputType ))
        {
            outputType = typeof(Nullable<>).MakeGenericType(outputType);
            return Expression.Condition(test, Expression.Constant(null, outputType), Expression.Convert(node,outputType));
        }
        return Expression.Condition(test, Expression.Constant(null, outputType), node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Object == null || !IsNullable(node.Object.Type))
            return base.VisitMethodCall(node);

        var expression = base.Visit(node.Object);
        var nullBaseExpression = Expression.Constant(null, expression.Type);
        var test = Expression.Equal(expression, nullBaseExpression);
        var outputType = node.Type;
        if (!IsNullable ( outputType ))
        {
            outputType = typeof(Nullable<>).MakeGenericType(outputType);
            return Expression.Condition(test, Expression.Constant(null, outputType), Expression.Convert(node,outputType));
        }
        return Expression.Condition(test, Expression.Constant(null, outputType), node);
    }

    private static bool IsNullable(Type type)
    {
        if (type.IsClass)
            return true;
        return type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(Nullable<>);
    }
}