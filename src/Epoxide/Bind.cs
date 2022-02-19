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
    IBinding Bind ( Expression specifications );
}

public static class BinderExtensions
{
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
        if ( expression.NodeType == ExpressionType.Lambda )
            expression = ( (LambdaExpression) expression ).Body;

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

    public IBinding Bind ( Expression specifications )
    {
        var binding = Parse ( specifications );

        binding.Bind ( );

        return binding;
    }

    IBinding Parse ( Expression expr )
    {
        if ( expr.NodeType == ExpressionType.Lambda )
            expr = ( (LambdaExpression) expr ).Body;

        if ( expr.NodeType == ExpressionType.Call )
        {
            var m = (MethodCallExpression) expr;
            var b = m.Method.GetCustomAttribute < BindableEventAttribute > ( );
            if ( b != null )
            {
                // TODO: Validate arguments
                var name = m.Arguments.Count == 3 ? (string) ( (ConstantExpression) m.Arguments [ 1 ] ).Value : b.EventName;

                return new EventBinding ( Services, m.Arguments[ 0 ], name, Parse ( m.Arguments [ ^1 ] ) );
            }
        }

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

        throw new FormatException ( $"Invalid binding format: { expr }" );
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

// TODO: Add state to evaluation and Binding < TSource >
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

    public abstract bool CanAdd   { get; }
    public abstract bool CanWrite { get; }

    public abstract bool TryRead  ( out object? value );
    public abstract bool TryWrite ( object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member );

    private class Readable : ExpressionAccessor
    {
        public Readable ( Expression expression ) : base ( expression )
        {
            CanAdd = ExprHelper.GetGenericInterfaceArguments ( expression.Type, typeof ( ICollection < > ) ) != null;
        }

        private Expression? sentinelExpression;
        public  Expression  SentinelExpression => sentinelExpression ??= Expression.AddSentinel ( );

        public override bool CanAdd { get; }
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
            base.VisitMember ( node );

            Triggers.Add ( new Trigger { Accessor = ExpressionAccessor.Create ( node.Expression ), Member = node.Member } );

            return node;
        }

        // TODO: Add method support?
        // protected override Expression VisitMethodCall ( MethodCallExpression node )
        // {
        //     base.VisitMethodCall ( node );
        // 
        //     Triggers.Add ( new Trigger { Accessor = ExpressionAccessor.Create ( node ), Member = node.Method } );
        // 
        //     return node;
        // }
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
    private readonly CompositeDisposable disposables;

    private readonly Side leftSide;
    private readonly Side rightSide;
    private readonly Side initialSide;

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

        leftSide  = new Side ( services, left,  ScheduleReadThenWriteToOtherSide );
        rightSide = new Side ( services, right, ScheduleReadThenWriteToOtherSide );

        leftSide .OtherSide = rightSide;
        rightSide.OtherSide = leftSide;

        if      ( Left .CanWrite ) initialSide = rightSide;
        else if ( Right.CanWrite ) initialSide = leftSide;
        else if ( Left.CanAdd && ( Left.Expression.NodeType == ExpressionType.MemberAccess || ! Right.CanAdd ) )
        {
            initialSide = leftSide;

            leftSide .Callback = ScheduleReadThenAddFromOtherSide;
            rightSide.Callback = ScheduleOtherSideReadThenAddFromThisSide;
        }
        else if ( Right.CanAdd )
        {
            initialSide = rightSide;

            leftSide .Callback = ScheduleOtherSideReadThenAddFromThisSide;
            rightSide.Callback = ScheduleReadThenAddFromOtherSide;
        }
        else
        {
            // TODO: BindingException + nicer Expression.ToString ( )
            if ( left.NodeType == ExpressionType.Convert && left.Type == right.Type && ExprHelper.IsWritable ( ( (UnaryExpression) left ).Operand ) )
                throw new ArgumentException ( $"Cannot assign { left.Type } to { ( (UnaryExpression) left ).Operand.Type }" );
            else if ( right.NodeType == ExpressionType.Convert && right.Type == left.Type && ExprHelper.IsWritable ( ( (UnaryExpression) right ).Operand ) )
                throw new ArgumentException ( $"Cannot assign { right.Type } to { ( (UnaryExpression) right ).Operand.Type }" );
            
            throw new ArgumentException ( $"Neither side is writable { left } == { right }" );
        }

        disposables.Add ( leftSide .Container    );
        disposables.Add ( leftSide .Subscription );
        disposables.Add ( rightSide.Container    );
        disposables.Add ( rightSide.Subscription );
    }

    public IBinderServices Services { get; }

    public ExpressionAccessor Left  => leftSide .Accessor;
    public ExpressionAccessor Right => rightSide.Accessor;
    public object?            Value { get; private set; }

    public void Bind ( )
    {
        initialSide.Callback ( initialSide );
    }

    public void Unbind ( )
    {
        leftSide .Subscription.Unsubscribe ( );
        rightSide.Subscription.Unsubscribe ( );
    }

    private CompositeDisposable? activeContainer;

    public void Attach  ( IDisposable disposable ) => ( activeContainer ?? disposables ).Add ( disposable );
    public bool Detach  ( IDisposable disposable ) => leftSide.Container.Remove ( disposable ) || rightSide.Container.Remove ( disposable ) || disposables.Remove ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );

    private void ScheduleReadThenWriteToOtherSide         ( Side side ) => Schedule ( side.Accessor.Expression, side, ReadThenWriteToOtherSide );
    private void ScheduleReadThenAddFromOtherSide         ( Side side ) => Schedule ( side.Accessor.Expression, side, ReadThenReadThenAddFromOtherSide );
    private void ScheduleOtherSideReadThenAddFromThisSide ( Side side ) => Schedule ( side.OtherSide.Accessor.Expression, side.OtherSide, ReadThenReadThenAddFromOtherSide );

    private void ReadThenWriteToOtherSide ( Side side )
    {
        if ( ! TryRead ( side ) )
            return;

        if ( ! TryReadAsync ( side, side => Schedule ( side.OtherSide.Accessor.Expression, side.OtherSide, WriteFromOtherSide ) ) )
            Schedule ( side.OtherSide.Accessor.Expression, side.OtherSide, WriteFromOtherSide );
    }

    private void WriteFromOtherSide ( Side side )
    {
        if ( side.Accessor.TryWrite ( side.OtherSide.Value, out var target, out var member ) )
            if ( ! Equals ( Value, Value = side.OtherSide.Value ) )
                Services.MemberSubscriber.Invalidate ( side.Accessor.Expression, member );

        side.Subscription.Subscribe ( );
    }

    private void ReadThenReadThenAddFromOtherSide ( Side side )
    {
        if ( ! TryRead ( side ) || side.Value == null )
            return;

        if ( ! TryReadAsync ( side, ScheduleReadThenAddToOtherSide ) )
            ScheduleReadThenAddToOtherSide ( side );
    }

    private void ScheduleReadThenAddToOtherSide ( Side side )
    {
        if ( side.Value != null )
            Schedule ( side.OtherSide.Accessor.Expression, side.OtherSide, ReadThenAddToOtherSide );
    }

    private void ReadThenAddToOtherSide ( Side side )
    {
        if ( ! TryRead ( side ) )
            return;

        if ( ! TryReadAsync ( side, AddToOtherSide ) )
            AddToOtherSide ( side );
    }

    private void AddToOtherSide ( Side side )
    {
        if ( side.OtherSide.Value is { } collection && Services.CollectionSubscriber.BindCollections ( Value = collection, side.Value ) is { } binding )
            side.Container.Add ( binding );
    }

    private bool TryRead ( Side side )
    {
        side.Container.Clear ( );

        activeContainer = side.Container;

        var read = side.Accessor.TryRead ( out var value );

        side.Value = read ? value : null;

        if ( value is not IBindableTask )
            side.Subscription.Subscribe ( );

        activeContainer = null;

        return read;
    }

    private bool TryReadAsync ( Side side, Action < Side > callback )
    {
        if ( side.Value is IBindableTask asyncResult )
        {
            ReadAsync ( side, asyncResult, callback );
            return true;
        }

        return false;
    }

    private async void ReadAsync ( Side side, IBindableTask asyncResult, Action < Side > callback )
    {
        var value = await asyncResult.Run ( );

        if ( asyncResult.Selector is { } selector )
        {
            Schedule ( selector, (object?) null, _ =>
            {
                side.Value = asyncResult.RunSelector ( value );

                if ( side.Value is IBindableTask subAsyncResult )
                    ReadAsync ( side, subAsyncResult, callback );
                else
                    callback ( side );
            } );
        }
        else
        {
            side.Value = value;

            if ( side.Value is IBindableTask subAsyncResult )
                ReadAsync ( side, subAsyncResult, callback );
            else
                callback ( side );
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

    private class Side
    {
        public Side ( IBinderServices services, Expression expression, Action < Side > callback )
        {
            Accessor     = ExpressionAccessor.Create ( expression );
            Callback     = callback;
            Container    = new CompositeDisposable ( );
            Subscription = new ExpressionSubscription ( services, expression, (o, m) => Callback ( this ) );
        }

        public ExpressionAccessor     Accessor     { get; }
        public Action < Side >        Callback     { get; set; }
        public CompositeDisposable    Container    { get; }
        public ExpressionSubscription Subscription { get; }
        public Side                   OtherSide    { get; set; }
        public object?                Value        { get; set; }
    }
}

public sealed class EventBinding : IBinding
{
    readonly CompositeDisposable disposables;
    readonly IBinding            binding;

    public EventBinding ( IBinderServices services, Expression source, string eventName, IBinding binding )
    {
        disposables = new CompositeDisposable ( 1 );

        Services = services;

        disposables.Add ( this.binding = binding );

        // TODO: Read source, hook event
    }

    public IBinderServices Services { get; }

    public void Bind   ( ) => binding.Bind   ( );
    public void Unbind ( ) => binding.Unbind ( );

    public void Attach  ( IDisposable disposable ) => disposables.Add     ( disposable );
    public bool Detach  ( IDisposable disposable ) => disposables.Remove  ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );
}

public sealed class CompositeBinding : IBinding
{
    readonly CompositeDisposable disposables;

    public CompositeBinding ( IBinderServices services ) : this ( services, Enumerable.Empty < IBinding > ( ) ) { }
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
    public static IDisposable? BindCollections ( this ICollectionSubscriber subscriber, object collection, object? enumerable )
    {
        BindCollectionsMethod ??= new Func<ICollectionSubscriber, ICollection<object>, ICollection<object>, IDisposable?>(CollectionBinder.BindCollections).GetMethodInfo().GetGenericMethodDefinition();

        var elementType = ExprHelper.GetGenericInterfaceArguments ( collection.GetType ( ), typeof ( ICollection < > ) )? [ 0 ];
        var bindCollections = BindCollectionsMethod.MakeGenericMethod ( elementType );

        return (IDisposable?) bindCollections.Invoke ( null, new [ ] { subscriber, collection, enumerable } );
    }

    private static MethodInfo? BindCollectionsMethod;

    public static IDisposable? BindCollections < T > ( this ICollectionSubscriber subscriber, ICollection < T > collection, IEnumerable < T >? enumerable )
    {
        collection.Clear ( );
        if ( enumerable == null )
            return null;

        foreach ( var item in enumerable )
            collection.Add ( item );

        return subscriber.Subscribe ( enumerable, (o, e) =>
        {
            // TODO: Process changes
            collection.Clear ( );
            if ( enumerable != null )
                foreach ( var item in enumerable )
                    collection.Add ( item );
        } );
    }
}