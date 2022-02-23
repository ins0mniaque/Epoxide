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

    public Expression Transform ( Expression expression )
    {
        expression = awaitableTaskVisitor  .Visit ( expression );
        expression = nullPropagationVisitor.Visit ( expression );

        if ( expression.NodeType == ExpressionType.Coalesce && ( (BinaryExpression) expression ).Right is ConstantExpression )
            return expression;

        if ( ! expression.IsNullable ( ) || expression.NodeType == ExpressionType.MemberAccess && ( (MemberExpression) expression ).Expression.IsClosure ( ) )
            return expression;

        if ( expression.Type != typeof ( object ) )
            expression = Expression.Convert ( expression, typeof ( object ) );

        return Expression.Coalesce ( expression, Expression.Constant ( Sentinel.Value ) );
    }
}

// TODO: Fix simple field accesses being transformed in variables
public class NullPropagationVisitor : ExpressionVisitor
{
    protected override Expression VisitUnary ( UnaryExpression node )
    {
        if ( node.Operand is MemberExpression member )
            return VisitMember ( member );

        if ( node.Operand is MethodCallExpression method )
            return VisitMethodCall ( method );

        if ( node.Operand is ConditionalExpression condition )
            return Expression.Condition ( test:    condition.Test,
                                          ifTrue:  Visit ( condition.IfTrue  ).MakeNullable ( ),
                                          ifFalse: Visit ( condition.IfFalse ).MakeNullable ( ) );

        // TODO: Fix support for lambdas
        if ( node.Operand is LambdaExpression lambda )
            return node;

        return base.VisitUnary ( node );
    }

    protected override Expression VisitBinary ( BinaryExpression node )
    {
        if ( node.NodeType == ExpressionType.Coalesce )
            return Expression.Coalesce ( Visit ( node.Left  ).MakeNullable ( ),
                                         Visit ( node.Right ).MakeNullable ( ) );

        return base.VisitBinary ( node );
    }

    protected override Expression VisitMember ( MemberExpression node )
    {
        if ( node.Expression.IsClosure ( ) )
            return base.VisitMember ( node );

        return PropagateNull ( node.Expression, node );
    }

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        // TODO: Fix support for functions (NodeType == Parameter)
        if ( node.Object == null || node.Object.NodeType == ExpressionType.Parameter )
           return node;

        return PropagateNull ( node.Object, node );
    }

    private BlockExpression PropagateNull ( Expression instance, Expression propertyAccess )
    {
        var safe    = Visit ( instance );
        var caller  = Expression.Variable ( safe.Type, GenerateVariableName ( safe ) );
        var assign  = Expression.Assign   ( caller, safe );
        var cast    = instance.IsNullableStruct ( ) ? caller : caller.RemoveNullable ( );
        var access  = new ExpressionReplacer ( node => node == instance ? cast : node ).Visit ( propertyAccess ).MakeNullable ( );
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

    // TODO: Generate better variable names
    private static string GenerateVariableName ( Expression instance )
    {
        return "caller";
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
            var queryableMethod = FindBindableEnumerableMethod ( node.Method );
            if ( queryableMethod == null )
                return base.VisitMethodCall ( node );

            if ( Binding != null )
            {
                asBindableBindingMethod ??= new Func<IEnumerable<object>, IBinding, IBindableEnumerable<object>>(BindableEnumerable.AsBindable<object>).Method.GetGenericMethodDefinition();

                var arg0 = Visit ( node.Arguments [ 0 ] );

                var asBindable = asBindableBindingMethod.MakeGenericMethod ( arg0.Type.GetGenericArguments ( ) [ 0 ] );

                arg0 = typeof ( IBindableEnumerable ).IsAssignableFrom ( arg0.Type ) ? arg0 : Expression.Call ( asBindable, arg0, Binding );

                var arguments = node.Arguments.Select ( (a, i) => i == 0 ? arg0 : typeof ( Expression ).IsAssignableFrom ( a.Type ) ? Expression.Lambda ( a ) : a );

                return Expression.Call ( queryableMethod, arguments );
            }
            else
            {
                asBindableDefaultMethod ??= new Func<IEnumerable<object>, IBindableEnumerable<object>>(BindableEnumerable.AsBindable<object>).Method.GetGenericMethodDefinition();

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

        if ( node.Method.DeclaringType != typeof ( BindableEnumerable ) || node.Method.ReturnType.GetGenericInterfaceArguments ( typeof ( IBindableEnumerable < > ) ) != null )
            return node;

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

public class BinderServicesReplacerVisitor : ExpressionVisitor
{
    private static MethodInfo? invalidates;

    protected override Expression VisitMethodCall ( MethodCallExpression node )
    {
        if ( node.Method.DeclaringType == typeof ( BindableEnumerable ) && node.Method.Name == nameof ( BindableEnumerable.AsBindable ) )
        {
            invalidates ??= typeof ( BindableEnumerable ).GetMethod ( nameof ( BindableEnumerable.Invalidates ) );

            return Expression.Call ( invalidates.MakeGenericMethod ( node.Method.GetGenericArguments ( ) ), node, Expression.Constant ( node.Arguments [ 0 ] ) );
        }

        if ( node.Method.DeclaringType != typeof ( BindableEnumerable ) || node.Method.ReturnType.GetGenericInterfaceArguments ( typeof ( IBindableEnumerable < > ) ) != null )
            return node;

        return base.VisitMethodCall ( node );
    }
}

public class TaskResultToAwaitableVisitor : ExpressionVisitor
{
    private static MethodInfo? await;
    private static MethodInfo? selectScheduler;
    private static Expression? schedulerSelector;

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
            var scheduler = Expression.Call ( schedulerSelector, selectScheduler, Expression.Constant ( right.Body ) );

            return Expression.Call ( await.MakeGenericMethod ( left.Type, right.Body.Type ),
                                     task,
                                     scheduler,
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
}