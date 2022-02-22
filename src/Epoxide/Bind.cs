using System.Linq.Expressions;
using System.Reflection;

using Epoxide.ChangeTracking;
using Epoxide.Disposables;
using Epoxide.Linq.Expressions;

namespace Epoxide;

public interface IBinderServices
{
    IMemberSubscriber     MemberSubscriber     { get; }
    ICollectionSubscriber CollectionSubscriber { get; }
    ISchedulerSelector    SchedulerSelector    { get; }
}

public class DefaultBindingServices : IBinderServices
{
    public IMemberSubscriber     MemberSubscriber     { get; } = new MemberSubscriber     ( new MemberSubscriptionFactory     ( ) );
    public ICollectionSubscriber CollectionSubscriber { get; } = new CollectionSubscriber ( new CollectionSubscriptionFactory ( ) );
    public ISchedulerSelector    SchedulerSelector    { get; } = new NoSchedulerSelector ( );
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

    public static IBinding Bind ( this IBinder binder, Expression < Func < bool > > specifications, params IDisposable [ ] disposables )
    {
        var binding = binder.Bind ( specifications );

        foreach ( var disposable in disposables )
            binding.Attach ( disposable );

        return binding;
    }

    public static IBinding Bind < TSource > ( this IBinder binder, Expression < Func < TSource, bool > > specifications, params IDisposable [ ] disposables )
    {
        var binding = binder.Bind ( specifications );

        foreach ( var disposable in disposables )
            binding.Attach ( disposable );

        return binding;
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

                parse ??= new Func < TSource, LambdaExpression, IBinding < TSource > > ( Parse ).Method.GetGenericMethodDefinition ( );

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

public interface IScheduler
{
    IDisposable Schedule < TState > ( TState state, Action < TState > action );
}

public interface ISchedulerSelector
{
    IScheduler? SelectScheduler ( Expression expression );
}

// TODO: Rename...
public class NoSchedulerSelector : ISchedulerSelector
{
    public IScheduler? SelectScheduler ( Expression expression ) => null;
}

// TODO: Handle exceptions
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

        if      ( leftSide .Accessor.IsWritable ) initialSide = rightSide;
        else if ( rightSide.Accessor.IsWritable ) initialSide = leftSide;
        else if ( leftSide .Accessor.IsCollection && ( leftSide.Accessor.Expression.Body.NodeType == ExpressionType.MemberAccess || ! rightSide.Accessor.IsCollection ) )
        {
            initialSide = leftSide;

            leftSide .Callback = ReadCollectionThenBindToOtherSide;
            rightSide.Callback = ReadOtherSideCollectionThenBind;
        }
        else if ( rightSide.Accessor.IsCollection )
        {
            initialSide = rightSide;

            leftSide .Callback = ReadOtherSideCollectionThenBind;
            rightSide.Callback = ReadCollectionThenBindToOtherSide;
        }
        else
        {
            // TODO: BindingException + nicer Expression.ToString ( )
            if ( leftBody.NodeType == ExpressionType.Convert && leftBody.Type == rightBody.Type && ( (UnaryExpression) leftBody ).Operand.IsWritable ( ) )
                throw new ArgumentException ( $"Cannot assign { leftBody.Type } to { ( (UnaryExpression) leftBody ).Operand.Type }" );
            else if ( rightBody.NodeType == ExpressionType.Convert && rightBody.Type == leftBody.Type && ( (UnaryExpression) rightBody ).Operand.IsWritable ( ) )
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
        BeforeAccess   ( side );
        ScheduleAccess ( side, side.Accessor.Read ( Source, side, WriteToOtherSide ) );
    }

    private void WriteToOtherSide ( TSource source, Side side, ExpressionReadResult result )
    {
        AfterAccess ( side, result.Token );

        if ( ! result.Succeeded )
            return;

        var otherSide = side.OtherSide;

        BeforeAccess   ( otherSide );
        ScheduleAccess ( otherSide, otherSide.Accessor.Write ( Source, otherSide, result.Value, AfterWrite ) );

        void AfterWrite ( TSource source, Side otherSide, ExpressionWriteResult result )
        {
            AfterAccess ( otherSide, result.Token );

            if ( result.Succeeded && ! Equals ( Value, Value = result.Value ) )
                Services.MemberSubscriber.Invalidate ( otherSide.Accessor.Expression, result.Member );
        }
    }

    private void ReadCollectionThenBindToOtherSide ( Side side )
    {
        BeforeAccess   ( side );
        ScheduleAccess ( side, side.Accessor.Read ( Source, side, BindCollectionToOtherSide ) );
    }

    private void ReadOtherSideCollectionThenBind ( Side side )
    {
        ReadCollectionThenBindToOtherSide ( side.OtherSide );
    }

    private void BindCollectionToOtherSide ( TSource source, Side side, ExpressionReadResult result )
    {
        AfterAccess ( side, result.Token );

        if ( ! result.Succeeded || result.Value == null )
            return;

        var otherSide  = side.OtherSide;
        var collection = result.Value;

        BeforeAccess   ( otherSide );
        ScheduleAccess ( otherSide, otherSide.Accessor.Read ( Source, otherSide, BindCollection ) );

        void BindCollection ( TSource source, Side otherSide, ExpressionReadResult result )
        {
            AfterAccess ( otherSide, result.Token );

            if ( result.Succeeded && Services.CollectionSubscriber.BindCollections ( Value = collection, result.Value ) is { } binding )
                otherSide.Container.Add ( binding );
        }
    }

    private void BeforeAccess ( Side side )
    {
        side.Container.Clear ( );

        activeContainer = side.Container;
    }

    private void ScheduleAccess ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Add ( scheduled );
    }

    private void AfterAccess ( Side side, IDisposable? scheduled )
    {
        side.Subscription.Subscribe ( Source );

        UnscheduleAccess ( side, scheduled );
    }

    private void UnscheduleAccess ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Remove ( scheduled );

        activeContainer = null;
    }

    private class Side
    {
        public Side ( IBinderServices services, LambdaExpression expression, Action < Side > callback )
        {
            Accessor     = services.SchedulerSelector.SelectScheduler  ( expression ) is { } scheduler ?
                           new ScheduledExpressionAccessor < TSource > ( expression, scheduler ) :
                           new ExpressionAccessor          < TSource > ( expression );
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

// TODO: Handle exceptions
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
        BeforeAccess   ( side );
        ScheduleAccess ( side, side.Accessor.Read ( Source, side, SubscribeToEvent ) );
    }

    private void SubscribeToEvent ( TSource source, Side side, ExpressionReadResult result )
    {
        AfterAccess ( side, result.Token );

        if ( ! result.Succeeded || result.Value == null )
            return;

        Event.AddEventHandler ( result.Value, EventHandler );

        side.Container.Add ( new Token ( Event, result.Value, EventHandler ) );
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

    private void BeforeAccess ( Side side )
    {
        side.Container.Clear ( );

        activeContainer = side.Container;
    }

    private void ScheduleAccess ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Add ( scheduled );
    }

    private void AfterAccess ( Side side, IDisposable? scheduled )
    {
        side.Subscription.Subscribe ( Source );

        UnscheduleAccess ( side, scheduled );
    }

    private void UnscheduleAccess ( Side side, IDisposable? scheduled )
    {
        if ( scheduled != null )
            side.Container.Remove ( scheduled );

        activeContainer = null;
    }

    private class Side
    {
        public Side ( IBinderServices services, LambdaExpression expression, Action < Side > callback )
        {
            Accessor     = services.SchedulerSelector.SelectScheduler  ( expression ) is { } scheduler ?
                           new ScheduledExpressionAccessor < TSource > ( expression, scheduler ) :
                           new ExpressionAccessor          < TSource > ( expression );
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