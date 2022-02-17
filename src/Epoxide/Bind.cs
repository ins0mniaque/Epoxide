using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
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

// TODO: Merge all helper methods as extensions
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

public abstract class ExpressionAccessor
{
    public static ExpressionAccessor Create ( Expression expression )
    {
        if ( ExprHelper.IsWritable ( expression ) )
            return new Writable ( expression );

        return new Readable ( expression );
    }

    protected ExpressionAccessor ( Expression expression )
    {
        Expression = expression;
    }

    public Expression Expression { get; }

    // NOTE: Expressions are always readable...
    public abstract bool CanRead  { get; }
    public abstract bool CanWrite { get; }

    public abstract bool TryRead  ( out object? value );
    public abstract bool TryWrite ( object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member );

    public static implicit operator Expression ( ExpressionAccessor accessor ) => accessor.Expression;

    private class Readable : ExpressionAccessor
    {
        public Readable ( Expression expression ) : base ( expression ) { }

        private Expression? sentinelExpression;
        public  Expression  SentinelExpression => sentinelExpression ??= Expression.AddSentinel ( );

        public override bool CanRead  => true;
        public override bool CanWrite => false;

        // TODO: Cache compilation
        public override bool TryRead ( out object? value )
        {
            value = CachedExpressionCompiler.Evaluate ( SentinelExpression );

            return value != Sentinel.Value;
        }

        public override bool TryWrite ( object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member )
        {
            throw new InvalidOperationException ( $"Expression { Expression } is not writable." );
        }
    }

    private class Writable : Readable
    {
        public Writable ( Expression expression ) : base ( expression ) { }

        private Expression? targetSentinelExpression;

        public override bool CanWrite => true;

        // TODO: Cache compilation
        public override bool TryWrite ( object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member )
        {
            targetSentinelExpression ??= ( (MemberExpression) Expression ).Expression.AddSentinel ( );

            member = ( (MemberExpression) Expression ).Member;
            target = CachedExpressionCompiler.Evaluate ( targetSentinelExpression );
            if ( target == null || target == Sentinel.Value )
                return false;

            member.SetValue ( target, value );

            return true;
        }
    }
}

// TODO: Refactor to avoid parsing expression on each call to find the right scheduler
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
            var x = m.Expression.AddSentinel ( );
            if ( CachedExpressionCompiler.Evaluate ( x ) is { } obj && obj != Sentinel.Value )
                services.MemberSubscriber.Invalidate ( obj, m.Member );
        }
        else
            throw new NotSupportedException();
    }
}

// Internal
public sealed class Trigger
{
    public ExpressionAccessor Accessor;
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

            Triggers.Add ( new Trigger { Accessor = ExpressionAccessor.Create ( node.Expression ), Member = node.Member } );

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
            t.Subscription = services.Scheduler.Schedule ( t.Accessor.Expression, t, ReadAndSubscribe ) ?? t.Subscription;
        }
    }

    private void ReadAndSubscribe ( Trigger t )
    {
        if ( t.Accessor.TryRead ( out var target ) && target != null )
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

public sealed class Binding : IBinding
{
    readonly CompositeDisposable disposables;

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

        var binding = Expression.Constant ( this );

        left = new EnumerableToCollectionVisitor         ( right.Type ).Visit ( left );
        left = new EnumerableToBindableEnumerableVisitor ( binding )   .Visit ( left );
        left = new AggregateInvalidatorVisitor           ( )           .Visit ( left );

        right = new EnumerableToCollectionVisitor         ( left.Type ).Visit ( right );
        right = new EnumerableToBindableEnumerableVisitor ( binding )  .Visit ( right );
        right = new AggregateInvalidatorVisitor           ( )          .Visit ( right );

        this.Left  = ExpressionAccessor.Create ( left );
        this.Right = ExpressionAccessor.Create ( right );

        // TODO: Avoid swapping
        if ( this.Right.CanWrite && ! this.Left.CanWrite )
        {
            var rightToLeft = this.Left;

            this.Left  = this.Right;
            this.Right = rightToLeft;
        }

        isReadOnlyCollection = ExprHelper.IsReadOnlyCollection ( left );

        if ( ! isReadOnlyCollection && ! this.Left.CanWrite )
        {
            // TODO: BindingException + nicer Expression.ToString ( )
            if ( left.NodeType == ExpressionType.Convert && left.Type == right.Type && ExprHelper.IsWritable ( ( (UnaryExpression) left ).Operand ) )
                throw new ArgumentException ( $"Cannot assign { left.Type } to { ( (UnaryExpression) left ).Operand.Type }" );
            else if ( right.NodeType == ExpressionType.Convert && right.Type == left.Type && ExprHelper.IsWritable ( ( (UnaryExpression) right ).Operand ) )
                throw new ArgumentException ( $"Cannot assign { right.Type } to { ( (UnaryExpression) right ).Operand.Type }" );
            
            throw new ArgumentException ( $"Neither side is writable { left } == { right }" );
        }

        disposables.Add ( leftSub  = CreateSubscription ( Left  ) );
        disposables.Add ( rightSub = CreateSubscription ( Right ) );
        disposables.Add ( leftContainer  = new CompositeDisposable ( ) );
        disposables.Add ( rightContainer = new CompositeDisposable ( ) );
    }

    public IBinderServices    Services { get; }
    public ExpressionAccessor Left     { get; }
    public ExpressionAccessor Right    { get; }
    public object?            Value    { get; private set; }

    public void Bind ( )
    {
        if ( isReadOnlyCollection )
        {
            Schedule ( Left.Expression, (object?) null, ReadThenBindCollection );
        }
        else
        {
            Schedule ( Right.Expression, Right, ReadThenWrite );
        }
    }

    public void Unbind ( )
    {
        leftSub .Unsubscribe ( );
        rightSub.Unsubscribe ( );
    }

    public void Attach  ( IDisposable disposable ) => ( activeContainer ?? disposables ).Add ( disposable );
    public bool Detach  ( IDisposable disposable ) => leftContainer.Remove ( disposable ) || rightContainer.Remove ( disposable ) || disposables.Remove ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );

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

    private bool TryRead ( ExpressionAccessor expression, out object? value )
    {
        if      ( expression == Left  ) activeContainer = leftContainer;
        else if ( expression == Right ) activeContainer = rightContainer;
        else                            activeContainer = null;

        activeContainer?.Clear ( );

        var read = expression.TryRead ( out value );

        if      ( expression == Left  ) leftSub .Subscribe ( );
        else if ( expression == Right ) rightSub.Subscribe ( );

        activeContainer = null;

        return read;
    }

    private void ReadThenWrite ( ExpressionAccessor expression )
    {
        if ( ! TryRead ( expression, out var value ) )
            return;

        var otherSide = expression == Left ? Right : Left;

        Schedule ( otherSide.Expression, (otherSide, value), Write );
    }

    private void Write ( (ExpressionAccessor Expression, object? Value) expressionAndValue )
    {
        if ( expressionAndValue.Expression.TryWrite ( expressionAndValue.Value, out var target, out var member ) )
            Callback ( expressionAndValue.Expression.Expression, member, expressionAndValue );

        if      ( expressionAndValue.Expression == Left  ) leftSub .Subscribe ( );
        else if ( expressionAndValue.Expression == Right ) rightSub.Subscribe ( );
    }

    private void ReadThenBindCollection ( object? state )
    {
        if ( ! TryRead ( Left, out var leftValue ) || leftValue == null )
            return;

        Schedule ( Right.Expression, leftValue, BindCollection );
    }

    private void BindCollection ( object leftValue )
    {
        if ( ! TryRead ( Right, out var rightValue ) )
            return;

        if ( Services.CollectionSubscriber.BindCollections ( leftValue, rightValue ) is { } binding )
            rightContainer.Add ( binding );
    }

    private void Callback ( Expression expression, MemberInfo member, object? value )
    {
        if ( ! Equals ( Value, Value = value ) )
            Services.MemberSubscriber.Invalidate ( expression, member );
    }

    private ExpressionSubscription CreateSubscription ( ExpressionAccessor accessor )
    {
        if ( isReadOnlyCollection )
            return new ExpressionSubscription ( Services, accessor.Expression, (o, m) => Schedule ( Left.Expression, (object?) null, ReadThenBindCollection ) );

        return new ExpressionSubscription ( Services, accessor.Expression, (o, m) => Schedule ( accessor.Expression, accessor, ReadThenWrite ) );
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