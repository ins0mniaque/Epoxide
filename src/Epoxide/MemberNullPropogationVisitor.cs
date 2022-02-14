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

public class MemberNullPropogationVisitor : ExpressionVisitor
{
    protected override Expression VisitLambda < T > ( Expression < T > node ) => node;

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
        {
            Visit ( node.Object );
            return node;
        }

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

public class EnumerableToCollectionVisitor : ExpressionVisitor
{
    private static MethodInfo? toListMethod;

    public EnumerableToCollectionVisitor ( Type returnType )
    {
        ReturnType = returnType;
    }

    public Type ReturnType { get; }

    public override Expression Visit ( Expression node )
    {
        if ( ! ReturnType.IsGenericType || ! node.Type.IsGenericType || node.Type == ReturnType )
            return node;

        if ( node.Type.GetGenericTypeDefinition ( ) == typeof ( IEnumerable < > ) )
        {
            var elementType = ReturnType.GetGenericArguments ( ) [ 0 ];
            if ( typeof ( ICollection         < > ).MakeGenericType ( elementType ).IsAssignableFrom ( ReturnType ) ||
                 typeof ( IReadOnlyCollection < > ).MakeGenericType ( elementType ).IsAssignableFrom ( ReturnType ) )
            {
                toListMethod ??= typeof ( BindableEnumerable ).GetMethod ( nameof ( BindableEnumerable.ToList ) );

                // TODO: Replace ObservableCollection with own collection
                var collectionType = ReturnType;
                if ( collectionType.IsInterface )
                    collectionType = typeof ( System.Collections.ObjectModel.ObservableCollection < > ).MakeGenericType ( elementType );

                var toList = toListMethod.MakeGenericMethod ( collectionType, node.Type.GetGenericArguments ( ) [ 0 ] );

                return Expression.Call ( toList, node );
            }
        }

        return node;
    }
}

public class EnumerableToQueryableVisitor : ExpressionVisitor
{
    private static MethodInfo? asBindableMethod;

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        if ( node.Method.DeclaringType == typeof ( Enumerable ) || node.Method.DeclaringType == typeof ( BindableEnumerable ) )
        {
            var queryableMethod = FindQueryableMethod ( node.Method );
            if ( queryableMethod == null )
                return base.VisitMethodCall ( node );

            asBindableMethod ??= typeof ( BindableEnumerable ).GetMethod ( nameof ( BindableEnumerable.AsBindable ) );

            var arg0 = Visit ( node.Arguments [ 0 ] );

            var asBindable = asBindableMethod.MakeGenericMethod ( arg0.Type.GetGenericArguments ( ) [ 0 ] );

            arg0 = typeof ( IQueryable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0 );

            var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : typeof ( Expression ).IsAssignableFrom ( a.Type ) ? Expression.Lambda ( a ) : a );

            return Expression.Call ( queryableMethod, arguments );
        }

        return base.VisitMethodCall ( node );
    }

    private class QueryableMethod
    {
        public QueryableMethod ( MethodInfo method )
        {
            Method           = method;
            GenericTypeCount = method.GetGenericArguments ( ).Length;
            ArgumentTypes    = GetQueryableMethodArgumentTypes ( method );
        }

        public MethodInfo Method           { get; }
        public int        GenericTypeCount { get; }
        public Type [ ]   ArgumentTypes    { get; }
    }

    private static ILookup < string, QueryableMethod >? queryableMethods;
    private static HashSet < string >?                  enumerableMethodsWithoutEquivalents;

    private static MethodInfo? FindQueryableMethod(MethodInfo method)
    {
        const BindingFlags Extensions = BindingFlags.Static | BindingFlags.Public;

        queryableMethods ??= typeof ( BindableQueryable ).GetMethods ( Extensions )
            .Concat ( typeof ( Queryable ).GetMethods ( Extensions ) )
            .Select ( m => new QueryableMethod ( m ) )
            .ToLookup ( q => q.Method.Name );

        enumerableMethodsWithoutEquivalents ??= typeof ( BindableEnumerable ).GetMethods ( Extensions )
            .Concat ( typeof ( Enumerable ).GetMethods ( Extensions ) )
            .Select ( m => m.Name )
            .Except ( queryableMethods.Select ( q => q.Key ) )
            .ToHashSet ( );

        if ( enumerableMethodsWithoutEquivalents.Contains ( method.Name ) )
            return null;

        var genericTypeCount = method.GetGenericArguments ( ).Length;
        var argumentTypes    = GetEnumerableMethodArgumentTypes ( method );
        var queryableMethod  = queryableMethods [ method.Name ].FirstOrDefault ( q => q.GenericTypeCount == genericTypeCount &&
                                                                                      q.ArgumentTypes.SequenceEqual ( argumentTypes ) );
        if ( queryableMethod == null )
            return null;

        if ( queryableMethod.Method.IsGenericMethod )
            return queryableMethod.Method.MakeGenericMethod ( method.GetGenericArguments ( ) );

        return queryableMethod.Method;
    }

    private static Type [ ] GetEnumerableMethodArgumentTypes ( MethodInfo method )
    {
        return method.GetParameters ( )
                     .Skip          ( 1 )
                     .Select        ( parameter => parameter.ParameterType )
                     .Select        ( type      => type.IsGenericType ? type.GetGenericTypeDefinition ( ) : type )
                     .ToArray       ( );
    }

    private static Type [ ] GetQueryableMethodArgumentTypes ( MethodInfo method )
    {
        return method.GetParameters ( )
                     .Skip          ( 1 )
                     .Select        ( parameter => parameter.ParameterType )
                     .Select        ( type      => type.IsGenericType && type.GetGenericArguments ( ) [ 0 ] is { } a && a.IsGenericType ? a.GetGenericTypeDefinition ( ) : type )
                     .ToArray       ( );
    }
}

public class AggregateInvalidatorVisitor : ExpressionVisitor
{
    private static MethodInfo? invalidates;

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        if ( node.Method.DeclaringType == typeof ( BindableEnumerable ) && node.Method.Name == nameof ( BindableEnumerable.AsBindable ) )
        {
            invalidates ??= typeof ( BindableQueryable ).GetMethod ( nameof ( BindableQueryable.Invalidates ) );

            return Expression.Call ( invalidates.MakeGenericMethod ( node.Method.GetGenericArguments ( ) ), node, Expression.Constant ( node.Arguments [ 0 ] ) );
        }

        if ( node.Method.DeclaringType != typeof ( Queryable ) || ExprHelper.GetGenericInterfaceArguments ( node.Method.ReturnType, typeof ( IQueryable < > ) ) != null )
            return node;

        return base.VisitMethodCall ( node );
    }
}