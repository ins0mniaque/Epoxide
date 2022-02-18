using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Epoxide;

public static class Sentinel
{
    public static object Value { get; } = new SentinelObject ( );

    public static Expression AddSentinel ( this Expression node )
    {
        node = bindableTaskVisitor   .Visit ( node );
        node = nullPropagationVisitor.Visit ( node );

        if ( node.NodeType == ExpressionType.Coalesce && ( (BinaryExpression) node ).Right is ConstantExpression )
            return node;

        if ( ! IsNullable ( node.Type ) || node.NodeType == ExpressionType.MemberAccess && IsClosure ( ( (MemberExpression) node ).Expression ) )
            return node;

        if ( node.Type != typeof ( object ) )
            node = Expression.Convert ( node, typeof ( object ) );

        return Expression.Coalesce ( node, Expression.Constant ( Value ) );
    }

    private static bool IsClosure ( Expression ex )
    {
        return Attribute.IsDefined ( ex.Type, typeof ( CompilerGeneratedAttribute ) );
    }

    private static bool IsNullable(Type type)
    {
        if (type.IsClass)
            return true;
        return type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(Nullable<>);
    }

    private static readonly NullPropagationVisitor          nullPropagationVisitor = new ( );
    private static readonly TaskResultToBindableTaskVisitor bindableTaskVisitor    = new ( );

    private class SentinelObject
    {
        public override string ToString ( ) => '{' + nameof ( Sentinel ) + '}';
    }
}

// TODO: Fix simple field accesses being transformed in variables
public class NullPropagationVisitor : ExpressionVisitor
{
    protected override Expression VisitUnary(UnaryExpression node)
    {
        if (node.Operand is MemberExpression mem)
            return VisitMember(mem);

        if (node.Operand is MethodCallExpression met)
            return VisitMethodCall(met);

        if (node.Operand is ConditionalExpression cond)
            return Expression.Condition(
                    test: cond.Test,
                    ifTrue: MakeNullable(Visit(cond.IfTrue)),
                    ifFalse: MakeNullable(Visit(cond.IfFalse)));

        // TODO: Fix support for lambdas
        if (node.Operand is LambdaExpression lambda)
            return node;

        return base.VisitUnary(node);
    }

    protected override Expression VisitBinary ( BinaryExpression node )
    {
        if ( node.NodeType == ExpressionType.Coalesce )
            return Expression.Coalesce ( MakeNullable(Visit(node.Left)),
                                         MakeNullable(Visit(node.Right)) );

        return base.VisitBinary ( node );
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if ( IsClosure ( node.Expression ) )
            return base.VisitMember ( node );

        return PropagateNull(node.Expression, node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // TODO: Fix support for functions (NodeType == Parameter)
        if ( node.Object == null || node.Object.NodeType == ExpressionType.Parameter )
           return node;

        return PropagateNull(node.Object, node);
    }

    private BlockExpression PropagateNull(Expression instance, Expression propertyAccess)
    {
        var safe = base.Visit(instance);
        var caller = Expression.Variable(safe.Type, "caller");
        var assign = Expression.Assign(caller, safe);
        var acess = MakeNullable(new ExpressionReplacer(instance,
                IsNullableStruct(instance) ? caller : RemoveNullable(caller)).Visit(propertyAccess));
        var ternary = Expression.Condition(
                    test: Expression.Equal(caller, Expression.Constant(null)),
                    ifTrue: Expression.Constant(null, acess.Type),
                    ifFalse: acess);

        return Expression.Block(
            type: acess.Type,
            variables: new[]
            {
                caller,
            },
            expressions: new Expression[]
            {
                assign,
                ternary,
            });
    }

    private static bool IsClosure ( Expression node )
    {
        return Attribute.IsDefined ( node.Type, typeof ( CompilerGeneratedAttribute ) );
    }

    private static Expression MakeNullable(Expression node)
    {
        if (IsNullable(node))
            return node;

        return Expression.Convert(node, typeof(Nullable<>).MakeGenericType(node.Type));
    }

    private static bool IsNullable(Expression node)
    {
        return !node.Type.IsValueType || (Nullable.GetUnderlyingType(node.Type) != null);
    }

    private static bool IsNullableStruct(Expression node)
    {
        return node.Type.IsValueType && (Nullable.GetUnderlyingType(node.Type) != null);
    }

    private static Expression RemoveNullable(Expression node)
    {
        if (IsNullableStruct(node))
            return Expression.Convert(node, node.Type.GenericTypeArguments[0]);

        return node;
    }

    private class ExpressionReplacer : ExpressionVisitor
    {
        private readonly Expression oldNode;
        private readonly Expression newNode;

        internal ExpressionReplacer(Expression oldNode, Expression newNode)
        {
            this.oldNode = oldNode;
            this.newNode = newNode;
        }

        public override Expression Visit(Expression node)
        {
            if (node == oldNode)
                return newNode;

            return base.Visit(node);
        }
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

public class TaskResultToBindableTaskVisitor : ExpressionVisitor
{
    private static MethodInfo? await;

    public override Expression Visit ( Expression node )
    {
        var splitter = new ExpressionSplitter ( IsTaskResult );

        // TODO: Sentinel support for right part
        if ( splitter.TrySplit ( node, out var left, out var right ) )
        {
            await ??= typeof ( BindableTask ).GetMethod ( nameof ( BindableTask.Create ) );

            var task = ( (MemberExpression) left ).Expression;

            return Expression.Call ( await.MakeGenericMethod ( left.Type, right.Body.Type ),
                                     task,
                                     Expression.Constant ( right.Body ),
                                     right,
                                     GetCancellationToken ( task ) );
        }

        return node;
    }

    private static Expression? defaultCancellationToken;

    private static Expression GetCancellationToken ( Expression task )
    {
        var token = (Expression?) null;
        if ( task.NodeType == ExpressionType.Call )
            token = ( (MethodCallExpression) task ).Arguments.FirstOrDefault ( argument => argument.Type == typeof ( CancellationToken ) );

        return token ?? ( defaultCancellationToken ??= Expression.Constant ( CancellationToken.None ) );
    }

    private static bool IsTaskResult ( Expression node )
    {
        return node.NodeType == ExpressionType.MemberAccess &&
               ( (MemberExpression) node ).Member.Name == nameof ( Task < object >.Result ) &&
               ( (MemberExpression) node ).Member.DeclaringType.BaseType == typeof ( Task );
    }

    private class ExpressionSplitter : ExpressionVisitor
    {
        private readonly Func<Expression, bool> predicate;
        private Expression? split;
        private ParameterExpression? parameter;

        public ExpressionSplitter(Func<Expression, bool> predicate)
        {
            this.predicate = predicate;
        }

        public bool TrySplit ( Expression node, [NotNullWhen(true)] out Expression? left, [NotNullWhen(true)] out LambdaExpression? right )
        {
            var body = Visit ( node );

            left  = split;
            right = split != null ? Expression.Lambda ( body, parameter ) : null;

            return left != null;
        }

        public override Expression Visit ( Expression node )
        {
            if ( node != null && predicate ( node ) )
            {
                split = node;
                return parameter = Expression.Parameter(node.Type);
            }

            return base.Visit ( node );
        }
    }
}