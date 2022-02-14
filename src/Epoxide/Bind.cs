using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Reflection;

namespace Epoxide;

public interface IBinder
{
    IBinding Bind < T > ( Expression < Func < T > > specifications );
}

public class Binder : IBinder
{
    // TODO: Lazy
    public static Binder Default { get; set; } = new Binder ( );

    public IBinding Bind<T> ( Expression<Func<T>> specifications )
    {
        return BindExpression ( specifications.Body );
    }

    public IMemberSubscriber MemberSubscriber { get; } = new MemberSubscriber ( new MemberSubscriptionFactory ( ) );
    public IBindingScheduler BindingScheduler { get; } = new NoScheduler ( );

    IBinding BindExpression ( Expression expr )
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

            return new CompositeBinding ( parts.Select ( BindExpression ) );
        }

        if ( expr.NodeType == ExpressionType.Equal )
        {
            var b = (BinaryExpression) expr;
            return new Binding ( MemberSubscriber, BindingScheduler, b.Left, b.Right );
        }

        throw new NotSupportedException ( "Only equality bindings are supported." );
    }
}

public interface IBinding : IDisposable
{

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
    void Schedule ( Expression expr, Action < object > callback );
    void ScheduleChange ( Expression expr, Expression valueExpression, MemberChangedCallback2 callback );
}

public class NoScheduler : IBindingScheduler
{
    public void Schedule ( Expression expr, Action < object > callback )
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

    public void ScheduleChange ( Expression expr, Expression valueExpression, MemberChangedCallback2 callback )
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

    // TODO: Fix dependency access
    public static void Invalidate ( this IBinder binder, Expression expression )
    {
        if ( expression.NodeType == ExpressionType.MemberAccess )
        {
            var m = (MemberExpression) expression;
            var x = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( m.Expression );
            if ( CachedExpressionCompiler.Evaluate ( x ) is { } obj && obj != SentinelPropogationVisitor.Sentinel )
                ( (Binder) binder ).MemberSubscriber.Invalidate ( obj, m.Member, 0 );
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

// TODO: Cache or reuse rewritten expressions...
public sealed class Binding : IBinding
{
    public IMemberSubscriber MemberSubscriber { get; }
    public IBindingScheduler Scheduler { get; }

    object Value;

    readonly Expression left;
    readonly Expression right;

    readonly List<Trigger> leftTriggers;
    readonly List<Trigger> rightTriggers;

    public Binding ( IMemberSubscriber observer, IBindingScheduler scheduler, Expression left, Expression right )
    {
        MemberSubscriber = observer;
        Scheduler = scheduler;

        left = new EnumerableToCollectionVisitor ( right.Type ).Visit ( left );
        left = new EnumerableToQueryableVisitor  ( )           .Visit ( left );
        left = new AggregateInvalidatorVisitor   ( )           .Visit ( left );

        right = new EnumerableToCollectionVisitor ( left.Type ).Visit ( right );
        right = new EnumerableToQueryableVisitor  ( )          .Visit ( right );
        right = new AggregateInvalidatorVisitor   ( )          .Visit ( right );

        this.left  = left;
        this.right = right;

        leftTriggers = Trigger.ExtractTriggers ( left );
        rightTriggers = Trigger.ExtractTriggers ( right );

        if ( ExprHelper.IsWritable ( left ) )
        {
            Scheduler.ScheduleChange ( left, right, CallbackAndSubscribe );
        }
        else if (!ExprHelper.IsWritable(right) && ExprHelper.IsReadOnlyCollection(left) )
        {
            Scheduler.Schedule ( collectionExpression = left, SubscribeToCollection );
        }
        else
        {
            Scheduler.ScheduleChange ( right, left, CallbackAndSubscribe );
        }
    }

    private void CallbackAndSubscribe ( Expression expression, MemberInfo member, object? value )
    {
        Callback ( expression, member, value );

        Resubscribe ( leftTriggers, left, right );
        Resubscribe ( rightTriggers, right, left );
    }

    private Expression?  collectionExpression;
    private IDisposable? collectionSubscription;

    private void SubscribeToCollection ( object? leftValue )
    {
        if ( leftValue == null )
        {
            Resubscribe ( leftTriggers, left, right );
            Resubscribe ( rightTriggers, right, left );

            return;
        }

        Scheduler.Schedule ( right, rightValue =>
        {
            collectionSubscription = BindCollections2 ( leftValue, rightValue );

            Resubscribe ( leftTriggers, left, right );
            Resubscribe ( rightTriggers, right, left );
        } );
    }

    private static IDisposable? BindCollections2 ( object leftValue, object rightValue )
    {
        BindCollectionsMethod ??= typeof ( Binding ).GetMethod ( nameof ( BindCollections ), BindingFlags.NonPublic | BindingFlags.Static );

        // TODO: Add non-generic ICollection support
        var elementType = ExprHelper.GetGenericInterfaceArguments ( leftValue.GetType ( ), typeof ( ICollection < > ) )? [ 0 ];
        var bindCollections = BindCollectionsMethod.MakeGenericMethod ( elementType );

        return (IDisposable?) bindCollections.Invoke ( null, new [ ] { leftValue, rightValue } );
    }

    private static MethodInfo? BindCollectionsMethod;

    private static IDisposable? BindCollections < T > ( ICollection < T > leftValue, IEnumerable < T >? rightValue )
    {
        leftValue.Clear ( );
        if ( rightValue != null )
            foreach ( var item in rightValue )
                leftValue.Add ( item );

        if ( rightValue is INotifyCollectionChanged ncc )
        {
            ncc.CollectionChanged += (o, e) =>
            {
                leftValue.Clear ( );
                if ( rightValue != null )
                foreach ( var item in rightValue )
                    leftValue.Add ( item );
            };

            // TODO: Return disconnecting disposable
            return null;
        }

        return null;
    }

    private void Callback ( Expression expression, MemberInfo member, object? value )
    {
        var e = Equals ( Value, value );

        Value = value;

        if ( ! e )
            MemberSubscriber.Invalidate ( expression, member, nextChangeId++ );
    }

    public void Dispose ( )
    {
        Unsubscribe ( leftTriggers );
        Unsubscribe ( rightTriggers );
    }

    void Resubscribe ( List<Trigger> triggers, Expression expr, Expression dependentExpr )
    {
        Unsubscribe ( triggers );
        Subscribe ( triggers, changeId => OnSideChanged ( expr, dependentExpr, changeId ) );
    }

    int nextChangeId = 1;
    readonly HashSet<int> activeChangeIds = new HashSet<int> ( );

    void OnSideChanged ( Expression expr, Expression dependentExpr, int causeChangeId )
    {
        if ( activeChangeIds.Contains ( causeChangeId ) ) return;

        if ( dependentExpr == collectionExpression )
        {
            collectionSubscription?.Dispose ( );
            collectionSubscription = null;

            Scheduler.Schedule ( collectionExpression, SubscribeToCollection );
            return;
        }

        var changeId = nextChangeId++;
        activeChangeIds.Add ( changeId );
        Scheduler.ScheduleChange ( dependentExpr, expr, (e, m, a) =>
        {
            Callback (e, m, a);
            activeChangeIds.Remove ( changeId );
        } );
    }

    static void Unsubscribe ( List<Trigger> triggers )
    {
        foreach ( var t in triggers )
        {
            t.Subscription?.Dispose ( );
            t.Subscription = null;
        }
    }

    void Subscribe ( List<Trigger> triggers, Action<int> action )
    {
        foreach ( var t in triggers )
        {
            Scheduler.Schedule ( t.Expression, target =>
            {
                t.Subscription = MemberSubscriber.Subscribe ( target, t.Member, action );
            } );
        }
    }
}

public sealed class CompositeBinding : IBinding
{
    readonly List<IBinding> bindings;

    public CompositeBinding ( IEnumerable<IBinding> bindings )
    {
        this.bindings = bindings.ToList ( );
    }

    public void Dispose ( )
    {
        foreach ( var b in bindings )
        {
            b.Dispose ( );
        }

        bindings.Clear ( );
    }
}