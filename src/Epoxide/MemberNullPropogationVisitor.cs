using System.Diagnostics.CodeAnalysis;

using Epoxide.Linq;
using Epoxide.Linq.Expressions;

namespace Epoxide;

public static class Sentinel
{
    public static Expression             Services    { get; } = ( (Expression < Func < IBinderServices > >) ( ( ) => Binder.Default.Services ) ).Body;
    public static IExpressionTransformer Transformer { get; } = new SentinelExpressionTransformer ( );

    // TODO: Rename...
    public static object Value { get; } = new SentinelObject ( );

    private class SentinelObject
    {
        public override string ToString ( ) => '{' + nameof ( Sentinel ) + '}';
    }
}

public class SentinelExpressionTransformer : IExpressionTransformer
{
    private static readonly NullPropagationVisitor       nullPropagationVisitor = new ( );
    private static readonly TaskResultToAwaitableVisitor awaitableTaskVisitor   = new ( );
    private static readonly AwaitableDelayVisitor        awaitableDelayVisitor  = new ( );

    public Expression Transform ( Expression expression )
    {
        expression = awaitableTaskVisitor  .Visit ( expression );
        expression = awaitableDelayVisitor .Visit ( expression );
        expression = nullPropagationVisitor.Visit ( expression );

        if ( expression.NodeType == ExpressionType.Coalesce && ( (BinaryExpression) expression ).Right is ConstantExpression )
            return expression;

        if ( ! expression.IsNullable ( ) || expression.NodeType == ExpressionType.MemberAccess && ( (MemberExpression) expression ).Expression.IsClosure ( ) )
            return expression;

        if ( expression.Type != typeof ( object ) )
            expression = Expression.Convert ( expression, typeof ( object ) );

        // TODO: Store constant
        return Expression.Coalesce ( expression, Expression.Constant ( Sentinel.Value ) );
    }
}

public class NullPropagationVisitor : ExpressionVisitor
{
    bool recurseLambda = false;

    protected override Expression VisitUnary ( UnaryExpression node )
    {
        if ( node.NodeType == ExpressionType.Quote )
            return node;

        return Visit ( node.Operand );
    }

    protected override Expression VisitLambda < T > ( Expression < T > node )
    {
        return recurseLambda ? base.VisitLambda ( node ) : node;
    }

    protected override Expression VisitConditional ( ConditionalExpression node )
    {
        return node.PropagateNull ( Visit ( node.IfTrue ), Visit ( node.IfFalse ) );
    }

    protected override Expression VisitBinary ( BinaryExpression node )
    {
        return node.PropagateNull ( Visit ( node.Left ), Visit ( node.Right ) );
    }

    protected override Expression VisitMember ( MemberExpression node )
    {
        return node.PropagateNull ( Visit ( node.Expression ) );
    }

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        return node.PropagateNull ( Visit ( node.Object ), node.Arguments.Select ( Visit ) );
    }
}

public class ExpressionReplacer : ExpressionVisitor
{
    private readonly Func < Expression, Expression > replace;

    public ExpressionReplacer ( Func < Expression, Expression > replace )
    {
        this.replace = replace;
    }

    public override Expression Visit ( Expression node )
    {
        if ( node != null && replace ( node ) is { } replaced && replaced != node )
            return replaced;

        return base.Visit ( node );
    }
}

public class ExpressionSplitter : ExpressionVisitor
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
                toListMethod ??= new Func<IEnumerable<object>, List<object>>(BindableEnumerable.ToList<List<object>, object>).Method.GetGenericMethodDefinition();

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
            var bindableMethod = FindBindableEnumerableMethod ( node.Method );
            if ( bindableMethod == null )
                return base.VisitMethodCall ( node );

            if ( Binding != null )
            {
                asBindableBindingMethod ??= new Func<IEnumerable<object>, IBinding, IBindableEnumerable<object>>(BindableEnumerable.AsBindable<object>).Method.GetGenericMethodDefinition();

                var arg0 = Visit ( node.Arguments [ 0 ] );

                var asBindable = asBindableBindingMethod.MakeGenericMethod ( arg0.Type.GetGenericArguments ( ) [ 0 ] );

                arg0 = typeof ( IBindableEnumerable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0, Binding );

                var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : a );

                return Expression.Call ( bindableMethod, arguments );
            }
            else
            {
                asBindableDefaultMethod ??= new Func<IEnumerable<object>, IBindableEnumerable<object>>(BindableEnumerable.AsBindable<object>).Method.GetGenericMethodDefinition();

                var arg0 = Visit ( node.Arguments [ 0 ] );

                var asBindable = asBindableDefaultMethod.MakeGenericMethod ( arg0.Type.GetGenericArguments ( ) [ 0 ] );

                arg0 = typeof ( IBindableEnumerable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0 );

                var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : a );

                return Expression.Call ( bindableMethod, arguments );
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

        var bindableEnumerableMethod = bindableEnumerableMethods [ method.Name ].FirstOrDefault ( q => ( q.Method.ReturnType.IsGenericType && q.Method.ReturnType.GetGenericInterfaceArguments(typeof(IEnumerable<>)) != null ||
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

        return base.VisitMethodCall ( node );
    }
}

public class BinderServicesReplacer : ExpressionVisitor
{
    private static PropertyInfo? services;

    public BinderServicesReplacer ( ) { }
    public BinderServicesReplacer ( Expression binding )
    {
        Binding = binding;
    }

    private Expression? Binding { get; }

    public override Expression Visit ( Expression node )
    {
        if ( node == Sentinel.Services )
        {
            services ??= typeof ( IBinding ).GetProperty ( nameof ( IBinding.Services ) );

            return Expression.MakeMemberAccess ( Binding, services );
        }

        return base.Visit ( node );
    }
}

public class AwaitableDelayVisitor : ExpressionVisitor
{
    private static MethodInfo? asDelayed;

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        if ( node.Method.DeclaringType == typeof ( Awaitable ) && node.Method.Name == nameof ( Awaitable.Delay ) )
        {
            asDelayed ??= typeof ( Awaitable ).GetMethod ( nameof ( Awaitable.AsDelayed ) );

            return Expression.Call ( node.Object, asDelayed.MakeGenericMethod ( node.Method.GetGenericArguments ( ) ), node.Arguments );
        }

        return base.VisitMethodCall ( node );
    }
}

public class TaskResultToAwaitableVisitor : ExpressionVisitor
{
    private static MethodInfo? await;
    private static MethodInfo? selectScheduler;
    private static Expression? schedulerSelector;
    private static MethodInfo? createLinkedTokenSource;
    private static Expression? defaultCancellationToken;

    public override Expression Visit ( Expression node )
    {
        var splitter = new ExpressionSplitter ( IsTaskResult );

        if ( splitter.TrySplit ( node, out var left, out var right ) )
        {
            await             ??= typeof ( Awaitable )         .GetMethod ( nameof ( Awaitable.AsAwaitable ) );
            selectScheduler   ??= typeof ( ISchedulerSelector ).GetMethod ( nameof ( ISchedulerSelector.SelectScheduler ) );
            schedulerSelector ??= Expression.MakeMemberAccess ( Sentinel.Services,
                                                                typeof ( IBinderServices ).GetProperty ( nameof ( IBinderServices.SchedulerSelector ) ) );

            var task      = ( (MemberExpression) left ).Expression;
            var scheduler = (Expression) Expression.Constant ( null, typeof ( IScheduler ) );

            if ( right != null )
                 scheduler = Expression.Call ( schedulerSelector, selectScheduler, Expression.Quote ( right ) );

            var cancellationTokenSource = (Expression) Expression.New ( typeof ( CancellationTokenSource ) );
            var cancellationToken       = GetCancellationToken ( task );

            if ( ! IsDefaultCancellationToken ( cancellationToken ) )
            {
                createLinkedTokenSource  ??= new Func < CancellationToken, CancellationToken, CancellationTokenSource > ( CancellationTokenSource.CreateLinkedTokenSource ).Method;
                defaultCancellationToken ??= Expression.Constant ( CancellationToken.None );

                cancellationTokenSource = Expression.Call ( createLinkedTokenSource, cancellationToken, defaultCancellationToken );
            }

            return Expression.Call ( await.MakeGenericMethod ( left.Type, right?.Body.Type ?? left.Type ),
                                     task,
                                     scheduler,
                                     right,
                                     cancellationTokenSource );
        }

        return node;
    }

    private static bool IsDefaultCancellationToken ( Expression cancellationToken )
    {
        if ( cancellationToken == defaultCancellationToken )
            return true;

        if ( cancellationToken.NodeType != ExpressionType.Constant )
            return false;

        return ( (ConstantExpression) cancellationToken ).Value is CancellationToken token && token == CancellationToken.None;
    }

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
}