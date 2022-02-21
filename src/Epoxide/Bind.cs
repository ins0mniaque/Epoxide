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

    IBinding < TSource > Bind < TSource > ( TSource source, Expression < Func < TSource, bool > > specifications );
}

public static class BinderExtensions
{
    public static IBinding Bind ( this IBinder binder, Expression < Func < bool > > specifications )
    {
        return binder.Bind ( null, Expression.Lambda < Func < object?, bool > > ( specifications.Body, CachedExpressionCompiler.UnusedParameter ) );
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

    public IBinding < TSource > Bind < TSource > ( TSource source, Expression < Func < TSource, bool > > specifications )
    {
        var binding = Parse ( source, specifications );

        binding.Bind ( );

        return binding;
    }

    private static MethodInfo? parse;

    IBinding < TSource > Parse < TSource > ( TSource source, LambdaExpression lambda )
    {
        var expr = lambda.Body;

        if ( expr.NodeType == ExpressionType.Call )
        {
            var m = (MethodCallExpression) expr;
            var b = m.Method.GetCustomAttribute < BindableEventAttribute > ( );
            if ( b != null )
            {
                // TODO: Validate arguments
                var eventName     = m.Arguments.Count == 3 ? (string) ( (ConstantExpression) m.Arguments [ 1 ] ).Value : b.EventName;
                var eventSource   = Expression.Lambda ( m.Arguments [ 0 ], lambda.Parameters );
                var eventLambda   = (LambdaExpression) m.Arguments [ ^1 ];
                var eventInfo     = eventSource.Body.Type.GetEvent ( eventName ) ??
                                    throw new InvalidOperationException ( $"Event { eventName } not found on type { eventSource.Body.Type.FullName }" );
                var eventArgsType = eventInfo.EventHandlerType.GetMethod ( nameof ( Action.Invoke ) ).GetParameters ( ).Last ( ).ParameterType;

                if ( eventLambda.Parameters.Count == 0 )
                    eventLambda = Expression.Lambda ( eventLambda.Body, Expression.Parameter ( eventArgsType, "e" ) );

                parse ??= new Func < TSource, LambdaExpression, IBinding < TSource > > ( Parse ).GetMethodInfo ( ).GetGenericMethodDefinition ( );

                var eventBinding     = parse.MakeGenericMethod ( eventArgsType ).Invoke ( this, new [ ] { Activator.CreateInstance ( eventArgsType ), eventLambda } );
                var eventBindingType = typeof ( EventBinding < , > ).MakeGenericType ( typeof ( TSource ), eventArgsType );

                // TODO: Create static method to cache reflection
                var eventBindingCtor = eventBindingType.GetConstructor ( new [ ] { typeof ( IBinderServices ), typeof ( LambdaExpression ), typeof ( EventInfo ), typeof ( IBinding < > ).MakeGenericType ( eventArgsType ) } );

                return (IBinding < TSource >) eventBindingCtor.Invoke ( new object [ ] { Services, eventSource, eventInfo, eventBinding } );
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

            return new CompositeBinding < TSource > ( Services, parts.Select ( part => Parse ( source, Expression.Lambda ( part, lambda.Parameters ) ) ) ) { Source = source };
        }

        if ( expr.NodeType == ExpressionType.Equal )
        {
            var b = (BinaryExpression) expr;

            var left  = Expression.Lambda ( b.Left,  lambda.Parameters );
            var right = Expression.Lambda ( b.Right, lambda.Parameters );

            return new Binding < TSource > ( Services, left, right ) { Source = source };
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

public interface IBinding < TSource > : IBinding
{
    TSource Source { get; set; }
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
    public static ExpressionAccessor < TSource > Create < TSource > ( LambdaExpression expression )
    {
        if ( ExprHelper.IsWritable ( expression.Body ) )
            return new Writable < TSource > ( expression );

        return new Readable < TSource > ( expression );
    }

    protected class Readable < T > : ExpressionAccessor < T >
    {
        public Readable ( LambdaExpression expression ) : base ( expression )
        {
            CanAdd = ExprHelper.GetGenericInterfaceArguments ( expression.Body.Type, typeof ( ICollection < > ) ) != null;
        }

        private Func < T, object? >? read;
        public  Func < T, object? >  Read => read ??= Compile ( Expression.Body, Expression.Parameters );

        public override bool       CanAdd       { get; }
        public override bool       CanWrite     => false;
        public override MemberInfo TargetMember => throw NotWritable;

        public override bool TryRead ( T source, out object? value )
        {
            value = Read ( source );

            return value != Sentinel.Value;
        }

        public override bool TryReadTarget ( T      source, [ NotNullWhen ( true ) ] out object? target ) => throw NotWritable;
        public override void Write         ( object target, object? value )                               => throw NotWritable;

        private InvalidOperationException NotWritable => new InvalidOperationException ( $"Expression { Expression } is not writable." );
    }

    protected class Writable < T > : Readable < T >
    {
        public Writable ( LambdaExpression expression ) : base ( expression )
        {
            TargetMember = ( (MemberExpression) Expression.Body ).Member;
        }

        private Func < T, object? >? readTarget;
        public  Func < T, object? >  ReadTarget => readTarget ??= Compile ( ( (MemberExpression) Expression.Body ).Expression, Expression.Parameters );

        public override bool       CanWrite     => true;
        public override MemberInfo TargetMember { get; }

        public override bool TryReadTarget ( T source, [ NotNullWhen ( true ) ] out object? target )
        {
            target = ReadTarget ( source );

            return target != null && target != Sentinel.Value;
        }

        // TODO: Emit code to set value
        public override void Write ( object target, object? value )
        {
            TargetMember.SetValue ( target, value );
        }
    }
}

public abstract class ExpressionAccessor < TSource >
{
    protected ExpressionAccessor ( LambdaExpression expression )
    {
        Expression = expression;
    }

    public LambdaExpression Expression { get; }

    public abstract bool       CanAdd       { get; }
    public abstract bool       CanWrite     { get; }
    public abstract MemberInfo TargetMember { get; }

    public abstract bool TryRead       ( TSource source, out object? value );
    public abstract bool TryReadTarget ( TSource source, [ NotNullWhen ( true ) ] out object? target );
    public abstract void Write         ( object  target, object? value );

    protected static Func < TSource, object? > Compile ( Expression expression, IReadOnlyCollection < ParameterExpression > parameters )
    {
        expression = expression.AddSentinel ( );
        if ( expression.Type != typeof ( object ) )
            expression = System.Linq.Expressions.Expression.Convert ( expression, typeof ( object ) );

        return CachedExpressionCompiler.Compile ( System.Linq.Expressions.Expression.Lambda < Func < TSource, object? > > ( expression, parameters ) );
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

// TODO: Clean up and add clonable TriggerCollection
public sealed class Trigger < TSource >
{
    public ExpressionAccessor < TSource > Accessor;
    public MemberInfo Member;
    public IDisposable? Subscription;

    public static List < Trigger < TSource > > ExtractTriggers ( LambdaExpression lambda )
    {
        var extractor = new TriggerExtractorVisitor ( lambda.Parameters );
        extractor.Visit ( lambda.Body );
        return extractor.Triggers;
    }

    private class TriggerExtractorVisitor : ExpressionVisitor
    {
        public TriggerExtractorVisitor ( IReadOnlyCollection < ParameterExpression > parameters )
        {
            Parameters = parameters;
        }

        public List < Trigger < TSource > >                Triggers   { get; } = new ( );
        public IReadOnlyCollection < ParameterExpression > Parameters { get; }

        protected override Expression VisitLambda < T > ( Expression < T > node ) => node;

        protected override Expression VisitMember ( MemberExpression node )
        {
            base.VisitMember ( node );

            var expression = Expression.Lambda ( node.Expression, Parameters );

            Triggers.Add ( new Trigger < TSource > { Accessor = ExpressionAccessor.Create < TSource > ( expression ), Member = node.Member } );

            return node;
        }

        // TODO: Add method support?
        // protected override Expression VisitMethodCall ( MethodCallExpression node )
        // {
        //     base.VisitMethodCall ( node );
        //
        //     var expression = Expression.Lambda ( node.Expression, Parameters );
        //
        //     Triggers.Add ( new Trigger < TSource > { Accessor = ExpressionAccessor.Create < TSource > ( expression ), Member = node.Method } );
        //
        //     return node;
        // }
    }
}

public delegate void ExpressionChangedCallback < TSource > ( LambdaExpression expression, TSource source, object target, MemberInfo member );

public sealed class ExpressionSubscriber < TSource >
{
    readonly IBinderServices services;
    readonly List<Trigger<TSource>> triggers;

    public ExpressionSubscriber ( IBinderServices services, LambdaExpression expression )
    {
        this.services = services;

        triggers = Trigger < TSource >.ExtractTriggers ( expression );
    }

    public IDisposable Subscribe ( TSource source, ExpressionChangedCallback < TSource > callback )
    {
        var triggers     = this.triggers.Select ( t => new Trigger < TSource > { Accessor = t.Accessor, Member = t.Member } ).ToList ( );
        var subscription = new ExpressionSubscription < TSource > ( services, triggers, callback );

        subscription.Subscribe ( source );

        return subscription;
    }
}

public sealed class ExpressionSubscription < TSource > : IDisposable
{
    readonly IBinderServices services;
    readonly List<Trigger<TSource>> triggers;

    readonly ExpressionChangedCallback < TSource > callback;

    public ExpressionSubscription ( IBinderServices services, LambdaExpression expression, ExpressionChangedCallback < TSource > callback )
    {
        this.services = services;
        this.callback = callback;
        this.triggers = Trigger < TSource >.ExtractTriggers ( expression );
    }

    public ExpressionSubscription ( IBinderServices services, List<Trigger<TSource>> triggers, ExpressionChangedCallback < TSource > callback )
    {
        this.services = services;
        this.callback = callback;
        this.triggers = triggers;
    }

    public TSource Source { get; private set; }

    public void Subscribe ( TSource source )
    {
        Source = source;

        // TODO: Group by scheduler, then schedule
        foreach ( var t in triggers )
        {
            t.Subscription?.Dispose ( );
            t.Subscription = services.Scheduler.Schedule ( t.Accessor.Expression, t, ReadAndSubscribe ) ?? t.Subscription;
        }
    }

    private void ReadAndSubscribe ( Trigger < TSource > t )
    {
        if ( t.Accessor.TryRead ( Source, out var target ) && target != null )
            t.Subscription = services.MemberSubscriber.Subscribe ( target, t.Member, (o, m) => callback ( t.Accessor.Expression, Source, o, m ) );
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

public static class ScheduledExpressionAccessor
{
    public static IDisposable ScheduleRead < TSource, TState > ( this IScheduler scheduler, ExpressionAccessor < TSource > accessor, TSource source, TState state, Action < TState, IDisposable?, object? > callback, Action < TState, IDisposable? > failed )
    {
        var single = new SerialDisposable ( );

        single.Disposable = scheduler.Schedule ( accessor.Expression, state, state =>
        {
            if ( accessor.TryRead ( source, out var value ) )
            {
                if ( value is not IBindableTask asyncResult )
                {
                    callback ( state, single, value );

                    single.Disposable = null;
                }
                else
                    scheduler.ReadAsync ( asyncResult, source, state, callback, failed, single );
            }
            else
            {
                failed ( state, single );

                single.Disposable = null;
            }
        } );

        return single;
    }

    public static IDisposable ScheduleReadTarget < TSource, TState > ( this IScheduler scheduler, ExpressionAccessor < TSource > accessor, TSource source, TState state, Action < TState, IDisposable?, object > callback, Action < TState, IDisposable? > failed )
    {
        var single = new SerialDisposable ( );

        single.Disposable = scheduler.Schedule ( accessor.Expression, state, state =>
        {
            if ( accessor.TryReadTarget ( source, out var target ) )
            {
                if ( target is not IBindableTask asyncResult )
                {
                    callback ( state, single, target );

                    single.Disposable = null;
                }
                else
                    scheduler.ReadAsync ( asyncResult, source, state, callback, failed, single );
            }
            else
            {
                failed ( state, single );

                single.Disposable = null;
            }
        } );

        return single;
    }

    private static async void ReadAsync < TSource, TState > ( this IScheduler scheduler, IBindableTask asyncResult, TSource source, TState state, Action < TState, IDisposable?, object? > callback, Action < TState, IDisposable? > failed, SerialDisposable single )
    {
        var value = await asyncResult.Run ( );

        if ( asyncResult.Selector is { } selector )
        {
            single.Disposable = scheduler.Schedule ( selector, state, state =>
            {
                // TODO: Add Sentinel support for Selector
                value = asyncResult.RunSelector ( value );

                if ( value is not IBindableTask subAsyncResult )
                {
                    callback ( state, single, value );

                    single.Disposable = null;
                }
                else
                    scheduler.ReadAsync ( subAsyncResult, source, state, callback, failed, single );
            } );
        }
        else if ( value is not IBindableTask subAsyncResult )
        {
            callback ( state, single, value );

            single.Disposable = null;
        }
        else
            scheduler.ReadAsync ( subAsyncResult, source, state, callback, failed, single );
    }
}

public sealed class Binding < TSource > : IBinding < TSource >
{
    private readonly CompositeDisposable disposables;

    private readonly Side leftSide;
    private readonly Side rightSide;
    private readonly Side initialSide;

    public Binding ( IBinderServices services, LambdaExpression left, LambdaExpression right )
    {
        disposables = new CompositeDisposable ( 4 );

        Services = services;

        var binding = Expression.Constant ( this );

        var leftBody = new EnumerableToCollectionVisitor         ( right.Body.Type ).Visit ( left.Body );
            leftBody = new EnumerableToBindableEnumerableVisitor ( binding )        .Visit ( leftBody );
            leftBody = new AggregateInvalidatorVisitor           ( )                .Visit ( leftBody );

        var rightBody = new EnumerableToCollectionVisitor         ( left.Body.Type ).Visit ( right.Body );
            rightBody = new EnumerableToBindableEnumerableVisitor ( binding )       .Visit ( rightBody );
            rightBody = new AggregateInvalidatorVisitor           ( )               .Visit ( rightBody );

        if ( left .Body != leftBody  ) left  = Expression.Lambda ( leftBody,  left .Parameters );
        if ( right.Body != rightBody ) right = Expression.Lambda ( rightBody, right.Parameters );

        leftSide  = new Side ( services, left,  ReadThenWriteToOtherSide );
        rightSide = new Side ( services, right, ReadThenWriteToOtherSide );

        leftSide .OtherSide = rightSide;
        rightSide.OtherSide = leftSide;

        if      ( leftSide .Accessor.CanWrite ) initialSide = rightSide;
        else if ( rightSide.Accessor.CanWrite ) initialSide = leftSide;
        else if ( leftSide .Accessor.CanAdd && ( leftSide.Accessor.Expression.Body.NodeType == ExpressionType.MemberAccess || ! rightSide.Accessor.CanAdd ) )
        {
            initialSide = leftSide;

            leftSide .Callback = ReadCollectionThenBindToOtherSide;
            rightSide.Callback = ReadOtherSideCollectionThenBind;
        }
        else if ( rightSide.Accessor.CanAdd )
        {
            initialSide = rightSide;

            leftSide .Callback = ReadOtherSideCollectionThenBind;
            rightSide.Callback = ReadCollectionThenBindToOtherSide;
        }
        else
        {
            // TODO: BindingException + nicer Expression.ToString ( )
            if ( leftBody.NodeType == ExpressionType.Convert && leftBody.Type == rightBody.Type && ExprHelper.IsWritable ( ( (UnaryExpression) leftBody ).Operand ) )
                throw new ArgumentException ( $"Cannot assign { leftBody.Type } to { ( (UnaryExpression) leftBody ).Operand.Type }" );
            else if ( rightBody.NodeType == ExpressionType.Convert && rightBody.Type == leftBody.Type && ExprHelper.IsWritable ( ( (UnaryExpression) rightBody ).Operand ) )
                throw new ArgumentException ( $"Cannot assign { rightBody.Type } to { ( (UnaryExpression) rightBody ).Operand.Type }" );
            
            throw new ArgumentException ( $"Neither side is writable { leftBody } == { rightBody }" );
        }

        disposables.Add ( leftSide .Container    );
        disposables.Add ( leftSide .Subscription );
        disposables.Add ( rightSide.Container    );
        disposables.Add ( rightSide.Subscription );
    }

    public IBinderServices Services { get; }

    // TODO: Invalidate on source change
    public TSource Source { get; set; }
    public object? Value  { get; private set; }

    public void Bind ( )
    {
        initialSide.Callback ( initialSide );
    }

    public void Unbind ( )
    {
        leftSide .Subscription.Unsubscribe ( );
        leftSide .Container   .Clear       ( );
        rightSide.Subscription.Unsubscribe ( );
        rightSide.Container   .Clear       ( );
    }

    private CompositeDisposable? activeContainer;

    public void Attach  ( IDisposable disposable ) => ( activeContainer ?? disposables ).Add ( disposable );
    public bool Detach  ( IDisposable disposable ) => leftSide.Container.Remove ( disposable ) || rightSide.Container.Remove ( disposable ) || disposables.Remove ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );

    private void ReadThenWriteToOtherSide ( Side side )
    {
        BeforeRead   ( side );
        ScheduleRead ( side, Services.Scheduler.ScheduleRead ( side.Accessor, Source, side, WriteToOtherSide, UnscheduleRead ) );
    }

    private void WriteToOtherSide ( Side side, IDisposable? disposable, object? value )
    {
        AfterRead ( side, disposable );

        var otherSide = side.OtherSide;

        BeforeRead   ( otherSide );
        ScheduleRead ( otherSide, Services.Scheduler.ScheduleReadTarget ( otherSide.Accessor, Source, otherSide, WriteValueToTarget, UnscheduleRead ) );

        void WriteValueToTarget ( Side otherSide, IDisposable? disposable, object target )
        {
            AfterRead ( otherSide, disposable );

            otherSide.Accessor.Write ( target, value );
            if ( ! Equals ( Value, Value = value ) )
                Services.MemberSubscriber.Invalidate ( otherSide.Accessor.Expression, otherSide.Accessor.TargetMember );
        }
    }

    private void ReadCollectionThenBindToOtherSide ( Side side )
    {
        BeforeRead   ( side );
        ScheduleRead ( side, Services.Scheduler.ScheduleRead ( side.Accessor, Source, side, BindCollectionToOtherSide, UnscheduleRead ) );
    }

    private void ReadOtherSideCollectionThenBind ( Side side )
    {
        ReadCollectionThenBindToOtherSide ( side.OtherSide );
    }

    private void BindCollectionToOtherSide ( Side side, IDisposable? disposable, object? collection )
    {
        AfterRead ( side, disposable );

        if ( collection == null )
            return;

        var otherSide = side.OtherSide;

        BeforeRead   ( otherSide );
        ScheduleRead ( otherSide, Services.Scheduler.ScheduleRead ( otherSide.Accessor, Source, otherSide, BindCollection, UnscheduleRead ) );

        void BindCollection ( Side otherSide, IDisposable? disposable, object enumerable )
        {
            AfterRead ( otherSide, disposable );

            if ( Services.CollectionSubscriber.BindCollections ( Value = collection, enumerable ) is { } binding )
                otherSide.Container.Add ( binding );
        }
    }

    private void BeforeRead ( Side side )
    {
        side.Container.Clear ( );

        activeContainer = side.Container;
    }

    private void ScheduleRead ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Add ( scheduled );
    }

    private void AfterRead ( Side side, IDisposable? scheduled )
    {
        side.Subscription.Subscribe ( Source );

        UnscheduleRead ( side, scheduled );
    }

    private void UnscheduleRead ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Remove ( scheduled );

        activeContainer = null;
    }

    private class Side
    {
        public Side ( IBinderServices services, LambdaExpression expression, Action < Side > callback )
        {
            Accessor     = ExpressionAccessor.Create < TSource > ( expression );
            Callback     = callback;
            Container    = new CompositeDisposable ( );
            Subscription = new ExpressionSubscription < TSource > ( services, expression, (e, s, o, m) => Callback ( this ) );
        }

        public ExpressionAccessor < TSource >     Accessor     { get; }
        public Action < Side >                    Callback     { get; set; }
        public CompositeDisposable                Container    { get; }
        public ExpressionSubscription < TSource > Subscription { get; }
        public Side                               OtherSide    { get; set; }
    }
}

public sealed class EventBinding < TSource, TArgs > : IBinding < TSource >
{
    readonly CompositeDisposable disposables;

    private readonly Side eventSourceSide;

    public EventBinding ( IBinderServices services, LambdaExpression eventSource, EventInfo eventInfo, IBinding < TArgs > subscribedBinding )
    {
        disposables = new CompositeDisposable ( 3 );

        Services          = services;
        Event             = eventInfo;
        SubscribedBinding = subscribedBinding;

        var binding = Expression.Constant ( this );

        var eventSourceBody = new EnumerableToBindableEnumerableVisitor ( binding ).Visit ( eventSource.Body );
            eventSourceBody = new AggregateInvalidatorVisitor           ( )        .Visit ( eventSourceBody );

        if ( eventSource.Body != eventSourceBody )
            eventSource = Expression.Lambda ( eventSourceBody, eventSource.Parameters );

        eventSourceSide = new Side ( services, eventSource, ReadThenSubscribeToEvent );

        disposables.Add ( eventSourceSide.Container    );
        disposables.Add ( eventSourceSide.Subscription );
        disposables.Add ( subscribedBinding );
    }

    public IBinderServices    Services          { get; }
    public EventInfo          Event             { get; }
    public IBinding < TArgs > SubscribedBinding { get; }

    // TODO: Invalidate on source change
    public TSource Source { get; set; }

    public void Bind ( )
    {
        ReadThenSubscribeToEvent ( eventSourceSide );
    }

    public void Unbind ( )
    {
        SubscribedBinding.Unbind ( );

        eventSourceSide.Subscription.Unsubscribe ( );
        eventSourceSide.Container   .Clear       ( );
    }

    private CompositeDisposable? activeContainer;

    public void Attach  ( IDisposable disposable ) => ( activeContainer ?? disposables ).Add ( disposable );
    public bool Detach  ( IDisposable disposable ) => eventSourceSide.Container.Remove ( disposable ) || disposables.Remove ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );

    private void ReadThenSubscribeToEvent ( Side side )
    {
        BeforeRead   ( side );
        ScheduleRead ( side, Services.Scheduler.ScheduleRead ( side.Accessor, Source, side, SubscribeToEvent, UnscheduleRead ) );
    }

    private void SubscribeToEvent ( Side side, IDisposable? disposable, object? eventSource )
    {
        AfterRead ( side, disposable );

        if ( eventSource == null )
            return;

        Event.AddEventHandler ( eventSource, EventHandler );

        side.Container.Add ( new Token ( Event, eventSource, EventHandler ) );
    }

    private Delegate? eventHandler;
    private Delegate  EventHandler => eventHandler ??= Delegate.CreateDelegate ( Event.EventHandlerType, this, nameof ( HandleEvent ) );

    // TODO: Add support for any events using code from GenericEventMemberSubscription
    private void HandleEvent ( object sender, TArgs args )
    {
        SubscribedBinding.Unbind ( );
        SubscribedBinding.Source = args;
        SubscribedBinding.Bind ( );
    }

    private void BeforeRead ( Side side )
    {
        side.Container.Clear ( );

        activeContainer = side.Container;
    }

    private void ScheduleRead ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Add ( scheduled );
    }

    private void AfterRead ( Side side, IDisposable? scheduled )
    {
        side.Subscription.Subscribe ( Source );

        UnscheduleRead ( side, scheduled );
    }

    private void UnscheduleRead ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Remove ( scheduled );

        activeContainer = null;
    }

    private class Side
    {
        public Side ( IBinderServices services, LambdaExpression expression, Action < Side > callback )
        {
            Accessor     = ExpressionAccessor.Create < TSource > ( expression );
            Callback     = callback;
            Container    = new CompositeDisposable ( );
            Subscription = new ExpressionSubscription < TSource > ( services, expression, (e, s, o, m) => Callback ( this ) );
        }

        public ExpressionAccessor < TSource >     Accessor     { get; }
        public Action < Side >                    Callback     { get; set; }
        public CompositeDisposable                Container    { get; }
        public ExpressionSubscription < TSource > Subscription { get; }
    }

    private sealed class Token : IDisposable
    {
        public Token ( EventInfo eventInfo, object eventSource, Delegate eventHandler )
        {
            Event        = eventInfo;
            EventSource  = eventSource;
            EventHandler = eventHandler;
        }

        public EventInfo Event        { get; }
        public object    EventSource  { get; }
        public Delegate  EventHandler { get; }

        public void Dispose ( )
        {
            Event.RemoveEventHandler ( EventSource, EventHandler );
        }
    }
}

public sealed class ContainerBinding : IBinding
{
    private readonly CompositeDisposable disposables;

    public ContainerBinding ( IBinderServices services )
    {
        disposables = new CompositeDisposable ( );
        Services    = services;
    }

    public IBinderServices Services { get; }

    public void Bind   ( ) { }
    public void Unbind ( ) { }

    public void Attach  ( IDisposable disposable ) => disposables.Add     ( disposable );
    public bool Detach  ( IDisposable disposable ) => disposables.Remove  ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );
}

public sealed class CompositeBinding < TSource > : IBinding < TSource >
{
    private readonly CompositeDisposable disposables;

    public CompositeBinding ( IBinderServices services, IEnumerable < IBinding > bindings )
    {
        disposables = new CompositeDisposable ( );

        Services = services;

        foreach ( var binding in bindings )
            disposables.Add ( binding );
    }

    public IBinderServices Services { get; }

    // TODO: Copy source to bindings on change
    public TSource Source { get; set; }

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
        if ( enumerable == null )
        {
            collection.Clear ( );

            return null;
        }

        collection.ReplicateChanges ( Enumerable.Repeat ( CollectionChange < T >.Invalidated ( ), 1 ), enumerable );

        return subscriber.Subscribe ( enumerable, (o, e) => collection.ReplicateChanges ( Enumerable.Repeat ( e, 1 ), enumerable ) );
    }
}