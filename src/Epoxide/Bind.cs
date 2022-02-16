using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Reflection;

namespace Epoxide;

public interface IBinderServices
{
    IMemberSubscriber     MemberSubscriber     { get; }
    ICollectionSubscriber CollectionSubscriber { get; }
    IBindingScheduler     Scheduler            { get; }
}

public class DefaultBindingServices : IBinderServices
{
    public IMemberSubscriber     MemberSubscriber     { get; } = new MemberSubscriber     ( new MemberSubscriptionFactory     ( ) );
    public ICollectionSubscriber CollectionSubscriber { get; } = new CollectionSubscriber ( new CollectionSubscriptionFactory ( ) );
    public IBindingScheduler     Scheduler            { get; } = new NoScheduler ( );
}

public interface IBinder
{
    IBinderServices Services { get; }

    IBinding Bind ( );
    IBinding Bind < T > ( Expression < Func < T > > specifications );
}

public class Binder : IBinder
{
    private static Binder? defaultBinder;
    public  static Binder  Default
    {
        get => defaultBinder ??= new Binder ( );
        set => defaultBinder = value;
    }

    public Binder ( ) : this ( new DefaultBindingServices ( ) ) { }
    public Binder ( IBinderServices services )
    {
        Services = services;
    }

    public IBinderServices Services { get; }

    public IBinding Bind ( )
    {
        return new CompositeBinding ( Services, Enumerable.Empty < IBinding > ( ) );
    }

    public IBinding Bind < T > ( Expression < Func < T > > specifications )
    {
        return Parse ( specifications.Body );
    }

    IBinding Parse ( Expression expr )
    {
        if ( expr.NodeType == ExpressionType.AndAlso )
        {
            var b = (BinaryExpression) expr;

            var parts = new List<Expression> ( );

            while ( b != null )
            {
                var l = b.Left;
                parts.Add ( b.Right );
                if ( l.NodeType == ExpressionType.AndAlso )
                {
                    b = (BinaryExpression) l;
                }
                else
                {
                    parts.Add ( l );
                    b = null;
                }
            }

            parts.Reverse ( );

            return new CompositeBinding ( Services, parts.Select ( Parse ) );
        }

        if ( expr.NodeType == ExpressionType.Equal )
        {
            var b = (BinaryExpression) expr;
            return new Binding ( Services, b.Left, b.Right );
        }

        throw new NotSupportedException ( "Only equality bindings are supported." );
    }
}

// TODO: For disposing of BindableEnumerable subscriptions, add Register/Unregister ( IDisposable )
// TODO: Add easy way to create empty Binding for AsBindable stand-alone support (AsBindable ( IBinder, out var binding )
// TODO: Add Bind/Unbind, and bind outside ctor
public interface IBinding : IDisposable
{
    // NOTE: Hide behind interface?
    IBinderServices Services { get; }
}

public static class ExprHelper
{
    public static bool IsWritable ( Expression expr )
    {
        return expr.NodeType == ExpressionType.MemberAccess &&
               ( (MemberExpression) expr ).Member is PropertyInfo { CanWrite: true } or FieldInfo;
    }

    public static bool IsReadOnlyCollection ( Expression expr )
    {
        return expr.NodeType == ExpressionType.MemberAccess &&
               ( (MemberExpression) expr ).Member is PropertyInfo { CanRead: true, CanWrite: false } p &&
               GetGenericInterfaceArguments ( p.PropertyType, typeof ( ICollection < > ) ) != null;
    }

    public static MemberInfo? GetMemberInfo ( Expression expr )
    {
        return expr.NodeType == ExpressionType.MemberAccess &&
               ( (MemberExpression) expr ).Member is PropertyInfo { CanWrite: true } or FieldInfo ?
            ( (MemberExpression) expr ).Member : null;
    }

    public static Type [ ]? GetGenericInterfaceArguments(Type type, Type genericInterface)
    {
        foreach (Type @interface in type.GetInterfaces())
        {
            if (@interface.IsGenericType)
            {
                if (@interface.GetGenericTypeDefinition() == genericInterface)
                {
                    return @interface.GetGenericArguments();
                }
            }
        }

        return null;
    }

    public static void SetValue ( this MemberInfo member, object target, object value )
    {
        if ( member is PropertyInfo p )
        {
            if ( p.CanWrite )
            {
                p.SetValue ( target, value, null );
            }
            else
                throw new InvalidOperationException("Trying to SetValue on read-only property " + p.Name );
        }

        else if ( member is FieldInfo f )
            f.SetValue ( target, value );
        else
            throw new InvalidOperationException ( "Cannot set value of " + member.GetType ( ).Name );
    }
}

public delegate void MemberChangedCallback2 ( Expression expression, MemberInfo member, object value );

public interface IBindingScheduler
{
    void Schedule ( Expression expr, Action callback );
    void Read ( Expression expr, Action < object > callback );
    void Write ( Expression expr, Expression valueExpression, MemberChangedCallback2 callback );
}

public class NoScheduler : IBindingScheduler
{
    public void Schedule ( Expression expr, Action callback )
    {
        callback ( );
    }

    public void Read ( Expression expr, Action < object > callback )
    {
        var coalescing = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( expr );
        var value = CachedExpressionCompiler.Evaluate ( coalescing );

        if ( value == SentinelPropogationVisitor.Sentinel )
        {
            if ( expr is MemberExpression mmm )
                expr = mmm.Expression;
            else if ( expr is MethodCallExpression ccc )
                expr = ccc.Object;

            if ( expr is MemberExpression or MethodCallExpression )
            {
                expr = new MemberNullPropogationVisitor ( ).Visit ( expr );
                if ( CachedExpressionCompiler.Evaluate ( expr ) == null )
                    return;
            }

            value = null;
        }

        if ( value != SentinelPropogationVisitor.Sentinel )
            callback ( value );
    }

    public void Write ( Expression expr, Expression valueExpression, MemberChangedCallback2 callback )
    {
        var coalescing = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( valueExpression );
        var value = CachedExpressionCompiler.Evaluate ( coalescing );

        if ( value == SentinelPropogationVisitor.Sentinel )
        {
            if ( valueExpression is MemberExpression mmm )
                valueExpression = mmm.Expression;
            else if ( valueExpression is MethodCallExpression ccc )
                valueExpression = ccc.Object;

            if ( valueExpression is MemberExpression or MethodCallExpression )
            {
                valueExpression = new MemberNullPropogationVisitor ( ).Visit ( valueExpression );
                if ( CachedExpressionCompiler.Evaluate ( valueExpression ) == null )
                    return;
            }

            value = null;
        }

        if ( value == SentinelPropogationVisitor.Sentinel )
            return;

        var targetExpr = expr;
        if ( targetExpr.NodeType != ExpressionType.MemberAccess )
            throw new NotSupportedException("Trying to SetValue on " + targetExpr.NodeType + " expression" );


        var m = (MemberExpression) targetExpr;

        var anotherExpr = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( m.Expression );

        var target = CachedExpressionCompiler.Evaluate ( anotherExpr );
        if ( target == SentinelPropogationVisitor.Sentinel )
            return;

        m.Member.SetValue ( target, value );

        callback ( targetExpr, m.Member, value );
    }
}



public static class DefaultBinder
{
    public static readonly Binder factory = new Binder ( );

    public static IBinding Bind<T> ( Expression<Func<T>> specifications )
    {
        return Binder.Default.Bind ( specifications );
    }

    public static void Invalidate<T> ( Expression<Func<T>> lambdaExpr )
    {
        Binder.Default.Invalidate ( lambdaExpr );
    }

    public static void Invalidate<T> ( this IBinder binder, Expression<Func<T>> lambdaExpr )
    {
        binder.Invalidate ( lambdaExpr.Body );
    }

    public static void Invalidate ( this IBinder binder, Expression expression )
    {
        binder.Services.Invalidate ( expression );
    }

    public static void Invalidate ( this IBinding binding, Expression expression )
    {
        binding.Services.Invalidate ( expression );
    }

    public static void Invalidate ( this IBinderServices services, Expression expression )
    {
        if ( expression.NodeType == ExpressionType.MemberAccess )
        {
            var m = (MemberExpression) expression;
            var x = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( m.Expression );
            if ( CachedExpressionCompiler.Evaluate ( x ) is { } obj && obj != SentinelPropogationVisitor.Sentinel )
                services.MemberSubscriber.Invalidate ( obj, m.Member, 0 );
        }
        else
            throw new NotSupportedException();
    }
}

// Internal
public sealed class Trigger
{
    public Expression Expression;
    public MemberInfo Member;
    public IDisposable? Subscription;

    public static List < Trigger > ExtractTriggers ( Expression s )
    {
        var extractor = new TriggerExtractorVisitor ( );
        extractor.Visit ( s );
        return extractor.Triggers;
    }

    private class TriggerExtractorVisitor : ExpressionVisitor
    {
        public List < Trigger > Triggers { get; } = new ( );

        protected override Expression VisitLambda < T > ( Expression < T > node ) => node;

        protected override Expression VisitMember ( MemberExpression node )
        {
            Visit ( node.Expression );

            Triggers.Add ( new Trigger {Expression = node.Expression, Member = node.Member} );

            return node;
        }
    }
}

public sealed class ExpressionSubscription : IDisposable
{
    readonly IBinderServices services;
    readonly Expression expression;
    readonly List<Trigger> triggers;
    readonly Action<object, MemberInfo, int> callback;

    public ExpressionSubscription ( IBinderServices services, Expression expression, Action<object, MemberInfo, int> callback )
    {
        this.services = services;
        this.expression = expression;
        this.callback = callback;

        triggers = Trigger.ExtractTriggers ( expression );
    }

    public void Subscribe ( )
    {
        foreach ( var t in triggers )
        {
            t.Subscription?.Dispose ( );
            t.Subscription = null;

            services.Scheduler.Read ( t.Expression, target =>
            {
                t.Subscription = services.MemberSubscriber.Subscribe ( target, t.Member, changeid => callback ( target, t.Member, changeid ) );
            } );
        }
    }

    public void Unsubscribe ( )
    {
        foreach ( var t in triggers )
        {
            t.Subscription?.Dispose ( );
            t.Subscription = null;
        }
    }

    public void Dispose ( ) => Unsubscribe ( );
}

// TODO: Cache or reuse rewritten expressions in scheduler
public sealed class Binding : IBinding
{
    object Value;

    readonly Expression left;
    readonly Expression right;

    readonly ExpressionSubscription leftSub;
    readonly ExpressionSubscription rightSub;

    public Binding ( IBinderServices services, Expression left, Expression right )
    {
        Services = services;

        left = new EnumerableToCollectionVisitor         ( right.Type ).Visit ( left );
        left = new EnumerableToBindableEnumerableVisitor ( )           .Visit ( left );
        left = new AggregateInvalidatorVisitor           ( )           .Visit ( left );

        right = new EnumerableToCollectionVisitor         ( left.Type ).Visit ( right );
        right = new EnumerableToBindableEnumerableVisitor ( )          .Visit ( right );
        right = new AggregateInvalidatorVisitor           ( )          .Visit ( right );

        this.left  = left;
        this.right = right;

        leftSub  = CreateSubscription ( left, right );
        rightSub = CreateSubscription ( right, left );

        if ( ExprHelper.IsWritable ( left ) )
        {
            services.Scheduler.Write ( left, right, CallbackAndSubscribe );
        }
        else if ( ! ExprHelper.IsWritable ( right ) && ExprHelper.IsReadOnlyCollection ( left ) )
        {
            services.Scheduler.Read ( collectionExpression = left, SubscribeToCollection );
        }
        else
        {
            services.Scheduler.Write ( right, left, CallbackAndSubscribe );
        }
    }

    public IBinderServices Services { get; }

    private void CallbackAndSubscribe ( Expression expression, MemberInfo member, object? value )
    {
        Callback ( expression, member, value );

        leftSub .Subscribe ( );
        rightSub.Subscribe ( );
    }

    private Expression?  collectionExpression;
    private IDisposable? collectionSubscription;

    private void SubscribeToCollection ( object? leftValue )
    {
        if ( leftValue == null )
        {
            leftSub .Subscribe ( );
            rightSub.Subscribe ( );

            return;
        }

        Services.Scheduler.Read ( right, rightValue =>
        {
            collectionSubscription = BindCollections2 ( leftValue, rightValue );

            leftSub .Subscribe ( );
            rightSub.Subscribe ( );
        } );
    }

    private IDisposable? BindCollections2 ( object leftValue, object rightValue )
    {
        BindCollectionsMethod ??= typeof ( Binding ).GetMethod ( nameof ( BindCollections ), BindingFlags.NonPublic | BindingFlags.Instance );

        var elementType = ExprHelper.GetGenericInterfaceArguments ( leftValue.GetType ( ), typeof ( ICollection < > ) )? [ 0 ];
        var bindCollections = BindCollectionsMethod.MakeGenericMethod ( elementType );

        return (IDisposable?) bindCollections.Invoke ( this, new [ ] { leftValue, rightValue } );
    }

    private static MethodInfo? BindCollectionsMethod;

    private IDisposable? BindCollections < T > ( ICollection < T > leftValue, IEnumerable < T >? rightValue )
    {
        leftValue.Clear ( );
        if ( rightValue == null )
            return null;

        foreach ( var item in rightValue )
            leftValue.Add ( item );

        return Services.CollectionSubscriber.Subscribe ( rightValue, (o, e) =>
        {
            leftValue.Clear ( );
            if ( rightValue != null )
                foreach ( var item in rightValue )
                    leftValue.Add ( item );
        } );
    }

    private void Callback ( Expression expression, MemberInfo member, object? value )
    {
        var e = Equals ( Value, value );

        Value = value;

        if ( ! e )
            Services.MemberSubscriber.Invalidate ( expression, member, nextChangeId++ );
    }

    public void Dispose ( )
    {
        leftSub .Dispose ( );
        rightSub.Dispose ( );
    }

    ExpressionSubscription CreateSubscription ( Expression expr, Expression dependentExpr )
    {
        return new ExpressionSubscription ( Services, expr, (o, m, changeId) => OnSideChanged ( expr, dependentExpr, changeId ) );
    }

    // TODO: Remove HashSet usages
    int nextChangeId = 1;
    readonly HashSet<int> activeChangeIds = new HashSet<int> ( );

    void OnSideChanged ( Expression expr, Expression dependentExpr, int causeChangeId )
    {
        if ( activeChangeIds.Contains ( causeChangeId ) ) return;

        if ( dependentExpr == collectionExpression )
        {
            collectionSubscription?.Dispose ( );
            collectionSubscription = null;

            Services.Scheduler.Read ( collectionExpression, SubscribeToCollection );
            return;
        }

        var changeId = nextChangeId++;
        activeChangeIds.Add ( changeId );
        Services.Scheduler.Write ( dependentExpr, expr, (e, m, a) =>
        {
            Callback (e, m, a);
            activeChangeIds.Remove ( changeId );
        } );
    }
}

public sealed class CompositeBinding : IBinding
{
    readonly List<IBinding> bindings;

    public CompositeBinding ( IBinderServices services, IEnumerable<IBinding> bindings )
    {
        Services = services;

        this.bindings = bindings.ToList ( );
    }

    public IBinderServices Services { get; }

    public void Dispose ( )
    {
        foreach ( var b in bindings )
        {
            b.Dispose ( );
        }

        bindings.Clear ( );
    }
}