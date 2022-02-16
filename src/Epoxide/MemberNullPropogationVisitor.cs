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
                toListMethod ??= new Func<IEnumerable<object>, List<object>>(BindableEnumerable.ToList<List<object>, object>).GetMethodInfo().GetGenericMethodDefinition();

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

public class EnumerableToBindableEnumerableVisitor : ExpressionVisitor
{
    public EnumerableToBindableEnumerableVisitor ( ) { }
    public EnumerableToBindableEnumerableVisitor ( Expression binding )
    {
        Binding = binding;
    }

    private Expression? Binding { get; }

    private static MethodInfo? asBindableDefaultMethod;
    private static MethodInfo? asBindableBindingMethod;

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        if ( node.Method.DeclaringType == typeof ( Enumerable ) )
        {
            var queryableMethod = FindBindableEnumerableMethod ( node.Method );
            if ( queryableMethod == null )
                return base.VisitMethodCall ( node );

            if ( Binding != null )
            {
                asBindableBindingMethod ??= new Func<IEnumerable<object>, IBinding, IBindableEnumerable<object>>(BindableEnumerable.AsBindable<object>).GetMethodInfo().GetGenericMethodDefinition();

                var arg0 = Visit ( node.Arguments [ 0 ] );

                var asBindable = asBindableBindingMethod.MakeGenericMethod ( arg0.Type.GetGenericArguments ( ) [ 0 ] );

                arg0 = typeof ( IBindableEnumerable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0, Binding );

                var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : typeof ( Expression ).IsAssignableFrom ( a.Type ) ? Expression.Lambda ( a ) : a );

                return Expression.Call ( queryableMethod, arguments );
            }
            else
            {
                asBindableDefaultMethod ??= new Func<IEnumerable<object>, IBindableEnumerable<object>>(BindableEnumerable.AsBindable<object>).GetMethodInfo().GetGenericMethodDefinition();

                var arg0 = Visit ( node.Arguments [ 0 ] );

                var asBindable = asBindableDefaultMethod.MakeGenericMethod ( arg0.Type.GetGenericArguments ( ) [ 0 ] );

                arg0 = typeof ( IBindableEnumerable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0 );

                var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : typeof ( Expression ).IsAssignableFrom ( a.Type ) ? Expression.Lambda ( a ) : a );

                return Expression.Call ( queryableMethod, arguments );
            }
        }

        return base.VisitMethodCall ( node );
    }

    private class BindableEnumerableMethod
    {
        public BindableEnumerableMethod ( MethodInfo method )
        {
            Method           = method;
            GenericTypeCount = method.GetGenericArguments ( ).Length;
            ArgumentTypes    = GetBindableEnumerableMethodArgumentTypes ( method );
        }

        public MethodInfo Method           { get; }
        public int        GenericTypeCount { get; }
        public Type [ ]   ArgumentTypes    { get; }
    }

    private static ILookup < string, BindableEnumerableMethod >? bindableEnumerableMethods;

    private static MethodInfo? FindBindableEnumerableMethod(MethodInfo method)
    {
        const BindingFlags Extensions = BindingFlags.Static | BindingFlags.Public;

        bindableEnumerableMethods ??= typeof ( BindableEnumerable ).GetMethods ( Extensions )
            .Select ( m => new BindableEnumerableMethod ( m ) )
            .ToLookup ( q => q.Method.Name );

        var genericTypeCount = method.GetGenericArguments ( ).Length;
        var argumentTypes    = GetEnumerableMethodArgumentTypes ( method );

        var bindableEnumerableMethod = bindableEnumerableMethods [ method.Name ].FirstOrDefault ( q => ( q.Method.ReturnType.IsGenericType && ExprHelper.GetGenericInterfaceArguments ( q.Method.ReturnType, typeof(IEnumerable<>)) != null ||
                                                                                                         q.Method.ReturnType == method.ReturnType ) &&
                                                                                                       q.GenericTypeCount == genericTypeCount &&
                                                                                                       q.ArgumentTypes.SequenceEqual ( argumentTypes ) );
        if ( bindableEnumerableMethod == null )
            throw new InvalidOperationException($"Missing {nameof(BindableEnumerable)} equivalent for {method.DeclaringType.Name}.{method.Name}");

        if ( bindableEnumerableMethod.Method.IsGenericMethod )
            return bindableEnumerableMethod.Method.MakeGenericMethod ( method.GetGenericArguments ( ) );

        return bindableEnumerableMethod.Method;
    }

    private static Type [ ] GetEnumerableMethodArgumentTypes ( MethodInfo method )
    {
        return method.GetParameters ( )
                     .Skip          ( 1 )
                     .Select        ( parameter => parameter.ParameterType )
                     .Select        ( type      => type.IsGenericType ? type.GetGenericTypeDefinition ( ) : type )
                     .ToArray       ( );
    }

    private static Type [ ] GetBindableEnumerableMethodArgumentTypes ( MethodInfo method )
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
            invalidates ??= typeof ( BindableEnumerable ).GetMethod ( nameof ( BindableEnumerable.Invalidates ) );

            return Expression.Call ( invalidates.MakeGenericMethod ( node.Method.GetGenericArguments ( ) ), node, Expression.Constant ( node.Arguments [ 0 ] ) );
        }

        if ( node.Method.DeclaringType != typeof ( BindableEnumerable ) || ExprHelper.GetGenericInterfaceArguments ( node.Method.ReturnType, typeof ( IBindableEnumerable < > ) ) != null )
            return node;

        return base.VisitMethodCall ( node );
    }
}