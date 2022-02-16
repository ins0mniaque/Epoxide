using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Reflection;

namespace Epoxide;

public interface IBinderServices
{
    IMemberSubscriber     MemberSubscriber     { get; }
    ICollectionSubscriber CollectionSubscriber { get; }
    IScheduler            Scheduler            { get; }
}

public class DefaultBindingServices : IBinderServices
{
    public IMemberSubscriber     MemberSubscriber     { get; } = new MemberSubscriber     ( new MemberSubscriptionFactory     ( ) );
    public ICollectionSubscriber CollectionSubscriber { get; } = new CollectionSubscriber ( new CollectionSubscriptionFactory ( ) );
    public IScheduler            Scheduler            { get; } = new NoScheduler ( );
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
        var binding = Parse ( specifications.Body );

        binding.Bind ( );

        return binding;
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

public interface IBinding : IDisposable
{
    // NOTE: Hide behind interface?
    IBinderServices Services { get; }

    void Bind   ( );
    void Unbind ( );

    void Attach ( IDisposable disposable );
    bool Detach ( IDisposable disposable );
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

    public static void SetValue ( this MemberInfo member, object target, object? value )
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

public interface IScheduler
{
    IDisposable? Schedule < TState > ( Expression expression, TState state, Action < TState > action );
}

public static class ExpressionMember
{
    // TODO: Find a way to avoid second read
    // TODO: Cache sentinel propagated expressions 
    public static bool TryRead ( this Expression expr, out object? value )
    {
        var coalescing = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( expr );
        value = CachedExpressionCompiler.Evaluate ( coalescing );

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
                    return false;
            }

            value = null;
        }

        return value != SentinelPropogationVisitor.Sentinel;
    }

    public static bool TryWrite ( this Expression expr, object? value, out object target, out MemberInfo member )
    {
        if ( expr.NodeType != ExpressionType.MemberAccess )
            throw new ArgumentException ( "Expression must be of type MemberAccess" );

        var m = (MemberExpression) expr;

        member = m.Member;

        var anotherExpr = new SentinelPropogationVisitor ( ).VisitAndAddSentinelSupport ( m.Expression );

        target = CachedExpressionCompiler.Evaluate ( anotherExpr ) ?? SentinelPropogationVisitor.Sentinel;
        if ( target == SentinelPropogationVisitor.Sentinel )
            return false;

        member.SetValue ( target, value );

        return true;
    }
}



public class NoScheduler : IScheduler
{
    public IDisposable? Schedule < TState > ( Expression expression, TState state, Action < TState > action )
    {
        action ( state );

        return null;
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
                services.MemberSubscriber.Invalidate ( obj, m.Member );
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
    readonly MemberChangedCallback callback;

    public ExpressionSubscription ( IBinderServices services, Expression expression, MemberChangedCallback callback )
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
            t.Subscription = services.Scheduler.Schedule ( t.Expression, t, ReadAndSubscribe ) ?? t.Subscription;
        }
    }

    private void ReadAndSubscribe ( Trigger t )
    {
        if ( t.Expression.TryRead ( out var target ) )
            t.Subscription = services.MemberSubscriber.Subscribe ( target, t.Member, callback );
        else
            t.Subscription = null;
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
    readonly CompositeDisposable disposables;

    object Value;

    readonly Expression left;
    readonly Expression right;

    // TODO: Split into 2 binding classes
    readonly bool isReadOnlyCollection;

    readonly ExpressionSubscription leftSub;
    readonly ExpressionSubscription rightSub;

    readonly CompositeDisposable leftContainer;
    readonly CompositeDisposable rightContainer;

    CompositeDisposable? activeContainer;

    public Binding ( IBinderServices services, Expression left, Expression right )
    {
        disposables = new CompositeDisposable ( 4 );

        Services = services;

        if ( ExprHelper.IsWritable ( right ) && ! ExprHelper.IsWritable ( left ) )
        {
            var rightToLeft = left;

            left  = right;
            right = rightToLeft;
        }

        isReadOnlyCollection = ExprHelper.IsReadOnlyCollection ( left );

        var binding = Expression.Constant ( this );

        left = new EnumerableToCollectionVisitor         ( right.Type ).Visit ( left );
        left = new EnumerableToBindableEnumerableVisitor ( binding )   .Visit ( left );
        left = new AggregateInvalidatorVisitor           ( )           .Visit ( left );

        right = new EnumerableToCollectionVisitor         ( left.Type ).Visit ( right );
        right = new EnumerableToBindableEnumerableVisitor ( binding )  .Visit ( right );
        right = new AggregateInvalidatorVisitor           ( )          .Visit ( right );

        this.left  = left;
        this.right = right;

        disposables.Add ( leftSub  = CreateSubscription ( left, right ) );
        disposables.Add ( rightSub = CreateSubscription ( right, left ) );
        disposables.Add ( leftContainer  = new CompositeDisposable ( ) );
        disposables.Add ( rightContainer = new CompositeDisposable ( ) );
    }

    public IBinderServices Services { get; }

    public void Bind ( )
    {
        if ( isReadOnlyCollection )
        {
            Schedule ( left, (object?) null, ReadAndBindCollection );
        }
        else
        {
            Schedule ( right, (object?) null, ReadAndWrite );
        }
    }

    private void Schedule < TState > ( Expression expression, TState state, Action < TState > action )
    {
        var scheduled = (IDisposable?) null;

        scheduled = Services.Scheduler.Schedule ( expression, state, InvokeAndUnschedule );

        if ( scheduled != null )
            disposables.Add ( scheduled );

        void InvokeAndUnschedule ( TState state )
        {
            action ( state );

            if ( scheduled != null )
                disposables.Remove ( scheduled );
        }
    }

    private bool TryRead ( Expression expression, out object? value )
    {
        if      ( expression == left  ) activeContainer = leftContainer;
        else if ( expression == right ) activeContainer = rightContainer;
        else                            activeContainer = null;

        activeContainer?.Clear ( );

        var read = expression.TryRead ( out value );

        activeContainer = null;

        return read;
    }

    private void ReadAndWrite ( object? state )
    {
        if ( ! TryRead ( right, out var rightValue ) )
        {
            leftSub .Subscribe ( );
            rightSub.Subscribe ( );

            return;
        }

        Schedule ( left, rightValue, Write );
    }

    private void Write ( object? leftValue )
    {
        if ( ! left.TryWrite ( leftValue, out var target, out var member ) )
            Callback ( left, member, leftValue );

        leftSub .Subscribe ( );
        rightSub.Subscribe ( );
    }

    private void ReadAndBindCollection ( object? state )
    {
        if ( ! TryRead ( left, out var leftValue ) || leftValue == null )
        {
            leftSub .Subscribe ( );
            rightSub.Subscribe ( );

            return;
        }

        Schedule ( right, leftValue, BindCollection );
    }

    private void BindCollection ( object leftValue )
    {
        if ( ! TryRead ( right, out var rightValue ) )
            return;

        if ( Services.CollectionSubscriber.BindCollections ( leftValue, rightValue ) is { } binding )
            rightContainer.Add ( binding );

        leftSub .Subscribe ( );
        rightSub.Subscribe ( );
    }

    public void Unbind ( )
    {
        leftSub .Unsubscribe ( );
        rightSub.Unsubscribe ( );
    }

    public void Attach  ( IDisposable disposable ) => ( activeContainer ?? disposables ).Add ( disposable );
    public bool Detach  ( IDisposable disposable ) => leftContainer.Remove ( disposable ) || rightContainer.Remove ( disposable ) || disposables.Remove ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );

    private void Callback ( Expression expression, MemberInfo member, object? value )
    {
        var e = Equals ( Value, value );

        Value = value;

        if ( ! e )
            Services.MemberSubscriber.Invalidate ( expression, member );
    }

    ExpressionSubscription CreateSubscription ( Expression expr, Expression dependentExpr )
    {
        return new ExpressionSubscription ( Services, expr, (o, m) => OnSideChanged ( expr, dependentExpr ) );
    }

    void OnSideChanged ( Expression expr, Expression dependentExpr )
    {
        if ( isReadOnlyCollection )
        {
            Schedule ( left, (object?) null, ReadAndBindCollection );
            return;
        }

        Schedule ( expr, this, _ =>
        {
            if ( TryRead ( expr, out var leftValue ) )
            {
                Schedule ( dependentExpr, this, _ =>
                {
                    if ( dependentExpr.TryWrite ( leftValue, out var target, out var member ) )
                        Callback ( dependentExpr, member, leftValue );
                } );
            }
        } );
    }
}

public sealed class CompositeBinding : IBinding
{
    readonly CompositeDisposable disposables;

    public CompositeBinding ( IBinderServices services, IEnumerable < IBinding > bindings )
    {
        disposables = new CompositeDisposable ( );

        Services = services;

        foreach ( var binding in bindings )
            disposables.Add ( binding );
    }

    public IBinderServices Services { get; }

    public void Bind ( )
    {
        foreach ( var binding in disposables.ToArray ( ).OfType < IBinding > ( ) )
            binding.Bind ( );
    }

    public void Unbind ( )
    {
        foreach ( var binding in disposables.ToArray ( ).OfType < IBinding > ( ) )
            binding.Unbind ( );
    }

    public void Attach  ( IDisposable disposable ) => disposables.Add     ( disposable );
    public bool Detach  ( IDisposable disposable ) => disposables.Remove  ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );
}

public static class CollectionBinder
{
    public static IDisposable? BindCollections ( this ICollectionSubscriber subscriber, object leftValue, object? rightValue )
    {
        BindCollectionsMethod ??= new Func<ICollectionSubscriber, ICollection<object>, ICollection<object>, IDisposable?>(CollectionBinder.BindCollections).GetMethodInfo().GetGenericMethodDefinition();

        var elementType = ExprHelper.GetGenericInterfaceArguments ( leftValue.GetType ( ), typeof ( ICollection < > ) )? [ 0 ];
        var bindCollections = BindCollectionsMethod.MakeGenericMethod ( elementType );

        return (IDisposable?) bindCollections.Invoke ( null, new [ ] { subscriber, leftValue, rightValue } );
    }

    private static MethodInfo? BindCollectionsMethod;

    public static IDisposable? BindCollections < T > ( this ICollectionSubscriber subscriber, ICollection < T > leftValue, IEnumerable < T >? rightValue )
    {
        leftValue.Clear ( );
        if ( rightValue == null )
            return null;

        foreach ( var item in rightValue )
            leftValue.Add ( item );

        return subscriber.Subscribe ( rightValue, (o, e) =>
        {
            leftValue.Clear ( );
            if ( rightValue != null )
                foreach ( var item in rightValue )
                    leftValue.Add ( item );
        } );
    }
}