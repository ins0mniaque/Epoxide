using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace Epoxide;

public static class Sentinel
{
    public static object Value { get; } = new SentinelObject ( );

    public static Expression AddSentinel ( this Expression node )
    {
        node = taskResultToAwaitVisitor.Visit ( node );
        node = nullPropagationVisitor  .Visit ( node );

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

    private static readonly NullPropagationVisitor   nullPropagationVisitor   = new ( );
    private static readonly TaskResultToAwaitVisitor taskResultToAwaitVisitor = new ( );

    private class SentinelObject
    {
        public override string ToString ( ) => '{' + nameof ( Sentinel ) + '}';
    }
}

// TODO: Fix simple field accesses being transformed in variables
public class NullPropagationVisitor : ExpressionVisitor
{
    public override Expression Visit ( Expression node ) => base.Visit ( node );
    protected override Expression VisitUnary(UnaryExpression propertyAccess)
    {
        if (propertyAccess.Operand is MemberExpression mem)
            return VisitMember(mem);

        if (propertyAccess.Operand is MethodCallExpression met)
            return VisitMethodCall(met);

        if (propertyAccess.Operand is ConditionalExpression cond)
            return Expression.Condition(
                    test: cond.Test,
                    ifTrue: MakeNullable(Visit(cond.IfTrue)),
                    ifFalse: MakeNullable(Visit(cond.IfFalse)));

        // TODO: Fix support for lambdas
        if (propertyAccess.Operand is LambdaExpression lambda)
            return propertyAccess;

        return base.VisitUnary(propertyAccess);
    }

    protected override Expression VisitBinary ( BinaryExpression node )
    {
        if ( node.NodeType == ExpressionType.Coalesce )
            return Expression.Coalesce ( MakeNullable(Visit(node.Left)),
                                         MakeNullable(Visit(node.Right)) );

        return base.VisitBinary ( node );
    }

    protected override Expression VisitMember(MemberExpression propertyAccess)
    {
        // TODO: This doesn't work...
        if ( IsClosure ( propertyAccess.Expression ) )
            return base.VisitMember ( propertyAccess );

        return PropagateNull(propertyAccess.Expression, propertyAccess);
    }

    protected override Expression VisitMethodCall(MethodCallExpression propertyAccess)
    {
        // TODO: Fix support for functions (NodeType == Parameter)
        if ( propertyAccess.Object == null || propertyAccess.Object.NodeType == ExpressionType.Parameter )
            return base.VisitMethodCall(propertyAccess);

        return PropagateNull(propertyAccess.Object, propertyAccess);
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

    private static bool IsClosure ( Expression ex )
    {
        return Attribute.IsDefined ( ex.Type, typeof ( CompilerGeneratedAttribute ) );
    }

    private static Expression MakeNullable(Expression ex)
    {
        if (IsNullable(ex))
            return ex;

        return Expression.Convert(ex, typeof(Nullable<>).MakeGenericType(ex.Type));
    }

    private static bool IsNullable(Expression ex)
    {
        return !ex.Type.IsValueType || (Nullable.GetUnderlyingType(ex.Type) != null);
    }

    private static bool IsNullableStruct(Expression ex)
    {
        return ex.Type.IsValueType && (Nullable.GetUnderlyingType(ex.Type) != null);
    }

    private static Expression RemoveNullable(Expression ex)
    {
        if (IsNullableStruct(ex))
            return Expression.Convert(ex, ex.Type.GenericTypeArguments[0]);

        return ex;
    }

    private class ExpressionReplacer : ExpressionVisitor
    {
        private readonly Expression _oldEx;
        private readonly Expression _newEx;

        internal ExpressionReplacer(Expression oldEx, Expression newEx)
        {
            _oldEx = oldEx;
            _newEx = newEx;
        }

        public override Expression Visit(Expression node)
        {
            if (node == _oldEx)
                return _newEx;

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

public class TaskResultToAwaitVisitor : ExpressionVisitor
{
    private static MethodInfo? await;

    public override Expression Visit ( Expression node )
    {
        var splitter = new ExpressionSplitter ( IsTaskResult );

        // TODO: Sentinel support for right part
        if ( splitter.TrySplit ( node, out var left, out var right ) )
        {
            await ??= typeof ( AsyncResult ).GetMethod ( nameof ( AsyncResult.Create ) );

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