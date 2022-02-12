using System.Linq.Expressions;
using System.Reflection;

namespace Epoxide;

public interface IBinder
{
    IBinding Bind < T > ( Expression < Func < T > > specifications );
}

public class Binder : IBinder
{
    public IBinding Bind<T> ( Expression<Func<T>> specifications )
    {
        return BindExpression ( specifications.Body );
    }

    public IMemberObserver MemberObserver { get; } = new MemberObserver ( );
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
            return new Binding ( MemberObserver, BindingScheduler, b.Left, b.Right );
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

    public static MemberInfo? GetMemberInfo ( Expression expr )
    {
        return expr.NodeType == ExpressionType.MemberAccess &&
               ( (MemberExpression) expr ).Member is PropertyInfo { CanWrite: true } or FieldInfo ?
            ( (MemberExpression) expr ).Member : null;
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
    private static readonly Binder factory = new Binder ( );

    public static IBinding Bind<T> ( Expression<Func<T>> specifications )
    {
        return factory.Bind ( specifications );
    }

    public static void Invalidate<T> ( Expression<Func<T>> lambdaExpr )
    {
        var body = lambdaExpr.Body;
        if ( body.NodeType == ExpressionType.MemberAccess )
        {
            var m = (MemberExpression) body;
            var x = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( m.Expression );
            if ( CachedExpressionCompiler.Evaluate ( x ) is { } obj && obj != SentinelPropogationVisitor.Sentinel )
                factory.MemberObserver.Invalidate ( obj, m.Member, 0 );
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

    public static IEnumerable < Trigger > EnumerateTriggers ( Expression s )
    {
        if ( s is MemberExpression me )
        {
            foreach ( var ss in EnumerateTriggers ( me.Expression ) )
                yield return ss;

            yield return new Trigger {Expression = me.Expression, Member = me.Member};
        }
        else
        {
            var b = s as BinaryExpression;
            if ( b != null )
            {
                foreach ( var l in EnumerateTriggers ( b.Left ) )
                    yield return l;
                foreach ( var r in EnumerateTriggers ( b.Right ) )
                    yield return r;
            }
        }
    }
}

// TODO: Cache or reuse rewritten expressions...
public sealed class Binding : IBinding
{
    public IMemberObserver MemberObserver { get; }
    public IBindingScheduler Scheduler { get; }

    object Value;

    readonly Expression left;
    readonly Expression right;

    readonly List<Trigger> leftTriggers;
    readonly List<Trigger> rightTriggers;

    public Binding ( IMemberObserver observer, IBindingScheduler scheduler, Expression left, Expression right )
    {
        MemberObserver = observer;
        Scheduler = scheduler;

        this.left = left = new EnumerableToQueryableVisitor().Visit ( left );
        this.right = right = new EnumerableToQueryableVisitor().Visit ( right );

        leftTriggers = Trigger.EnumerateTriggers ( left ).ToList ( );
        rightTriggers = Trigger.EnumerateTriggers ( right ).ToList ( );

        if ( ExprHelper.IsWritable ( left ) )
        {
            Scheduler.ScheduleChange ( left, right, CallbackAndSubscribe );
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

    private void Callback ( Expression expression, MemberInfo member, object? value )
    {
        var e = Equals ( Value, value );

        Value = value;

        if ( ! e )
            MemberObserver.Invalidate ( expression, member, nextChangeId++ );
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
                t.Subscription = MemberObserver.Observe ( target, t.Member, action );
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