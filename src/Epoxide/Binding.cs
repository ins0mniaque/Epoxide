using Epoxide.ChangeTracking;
using Epoxide.Disposables;
using Epoxide.Linq.Expressions;

namespace Epoxide;

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

[ DebuggerDisplay ( DebugView.DebuggerDisplay ) ]
public sealed class Binding < TSource > : IBinding < TSource >, IExpressionTransformer, IDebugView
{
    private readonly CompositeDisposable disposables;

    private readonly Side leftSide;
    private readonly Side rightSide;
    private readonly Side initialSide;

    public Binding ( IBinderServices services, LambdaExpression left, LambdaExpression right )
    {
        disposables = new CompositeDisposable ( 4 );

        Services = new BindingServices ( services.MemberSubscriber,
                                         services.CollectionSubscriber,
                                         services.SchedulerSelector,
                                         new BindingExceptionHandler ( this, services.UnhandledExceptionHandler ) );

        leftSide  = new Side ( this, left,  ReadThenWriteToOtherSide );
        rightSide = new Side ( this, right, ReadThenWriteToOtherSide );

        leftSide .OtherSide = rightSide;
        rightSide.OtherSide = leftSide;

        if      ( leftSide .Accessor.IsWritable ) initialSide = rightSide;
        else if ( rightSide.Accessor.IsWritable ) initialSide = leftSide;
        else if ( leftSide .Accessor.IsCollection && ( IsMemberAccess ( leftSide.Accessor ) || ! rightSide.Accessor.IsCollection ) )
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
            if ( left.Body.NodeType == ExpressionType.Convert && left.Body.Type == right.Body.Type && ( (UnaryExpression) left.Body ).Operand.IsWritable ( ) )
                throw new ArgumentException ( $"Cannot assign { left.Body.Type } to { ( (UnaryExpression) left.Body ).Operand.Type }" );
            else if ( right.Body.NodeType == ExpressionType.Convert && right.Body.Type == left.Body.Type && ( (UnaryExpression) right.Body ).Operand.IsWritable ( ) )
                throw new ArgumentException ( $"Cannot assign { right.Body.Type } to { ( (UnaryExpression) right.Body ).Operand.Type }" );
            
            throw new ArgumentException ( $"Neither side is writable { left.Body } == { right.Body }" );
        }

        disposables.Add ( leftSide .Container    );
        disposables.Add ( leftSide .Subscription );
        disposables.Add ( rightSide.Container    );
        disposables.Add ( rightSide.Subscription );
    }

    Expression IExpressionTransformer.Transform ( Expression expression )
    {
        var binding       = Expression.Constant ( this );
        var otherSideType = expression == rightSide.Accessor.Expression.Body ? leftSide .Accessor.Expression.Body.Type :
                                                                               rightSide.Accessor.Expression.Body.Type;

        expression = new EnumerableToCollectionVisitor         ( otherSideType ).Visit ( expression );
        expression = new EnumerableToBindableEnumerableVisitor ( binding )      .Visit ( expression );
        expression = new AggregateInvalidatorVisitor           ( )              .Visit ( expression );
        expression = Sentinel.Transformer.Transform                                    ( expression );
        expression = new BinderServicesReplacer                ( binding )      .Visit ( expression );

        return expression;
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

        if ( result.Faulted )
        {
            Services.UnhandledExceptionHandler.Catch ( result.Exception );
            return;
        }

        if ( ! result.Succeeded )
            return;

        var otherSide = side.OtherSide;

        BeforeAccess   ( otherSide );
        ScheduleAccess ( otherSide, otherSide.Accessor.Write ( Source, otherSide, result.Value, AfterWrite ) );

        void AfterWrite ( TSource source, Side otherSide, ExpressionWriteResult result )
        {
            AfterAccess ( otherSide, result.Token );

            if ( result.Faulted )
                Services.UnhandledExceptionHandler.Catch ( result.Exception );
            else if ( result.Succeeded && ! Equals ( Value, Value = result.Value ) )
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

        if ( result.Faulted )
        {
            Services.UnhandledExceptionHandler.Catch ( result.Exception );
            return;
        }

        if ( ! result.Succeeded || result.Value == null )
            return;

        var otherSide  = side.OtherSide;
        var collection = result.Value;

        BeforeAccess   ( otherSide );
        ScheduleAccess ( otherSide, otherSide.Accessor.Read ( Source, otherSide, BindCollection ) );

        void BindCollection ( TSource source, Side otherSide, ExpressionReadResult result )
        {
            AfterAccess ( otherSide, result.Token );

            if ( result.Faulted )
                Services.UnhandledExceptionHandler.Catch ( result.Exception );
            else if ( result.Succeeded && Services.CollectionSubscriber.BindCollections ( Value = collection, result.Value ) is { } binding )
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

    private static bool IsMemberAccess ( ExpressionAccessor < TSource > accessor )
    {
        return accessor.Expression.Body.NodeType == ExpressionType.MemberAccess;
    }

    string IDebugView.Display ( ) => DebugView.Display ( leftSide.Accessor.Expression.Body ) + " " +
                                     ( leftSide .Accessor.IsWritable        ? "<" :
                                       rightSide.Accessor.IsWritable        ? ""  :
                                       leftSide .Accessor.IsCollection &&
                                       IsMemberAccess ( leftSide.Accessor ) ? "<" : "" ) + "=" +
                                     ( rightSide.Accessor.IsWritable        ? ">" :
                                       leftSide .Accessor.IsWritable        ? ""  :
                                       rightSide.Accessor.IsCollection &&
                                     ! IsMemberAccess ( leftSide.Accessor ) ? ">" : "" ) + " " +
                                     DebugView.Display ( rightSide.Accessor.Expression.Body );

    private class Side
    {
        public Side ( Binding < TSource > binding, LambdaExpression expression, Action < Side > callback )
        {
            Accessor     = binding.Services.SchedulerSelector.SelectScheduler ( expression ) is { } scheduler ?
                           new ScheduledExpressionAccessor < TSource > ( scheduler, expression, binding ) :
                           new ExpressionAccessor          < TSource > ( expression, binding );
            Callback     = callback;
            Container    = new CompositeDisposable ( );
            Subscription = new ExpressionSubscription < TSource > ( binding.Services, expression, (e, s, o, m) => Callback ( this ) );
        }

        public ExpressionAccessor < TSource >     Accessor     { get; }
        public Action < Side >                    Callback     { get; set; }
        public CompositeDisposable                Container    { get; }
        public ExpressionSubscription < TSource > Subscription { get; }
        public Side                               OtherSide    { get; set; }
    }
}

[ DebuggerDisplay ( DebugView.DebuggerDisplay ) ]
public sealed class EventBinding < TSource, TArgs > : IBinding < TSource >, IExpressionTransformer, IDebugView
{
    readonly CompositeDisposable disposables;

    private readonly Side eventSourceSide;

    public EventBinding ( IBinderServices services, LambdaExpression eventSource, EventInfo eventInfo, IBinding < TArgs > subscribedBinding )
    {
        disposables = new CompositeDisposable ( 3 );

        Services = new BindingServices ( services.MemberSubscriber,
                                         services.CollectionSubscriber,
                                         services.SchedulerSelector,
                                         new BindingExceptionHandler ( this, services.UnhandledExceptionHandler ) );

        Event             = eventInfo;
        SubscribedBinding = subscribedBinding;

        eventSourceSide = new Side ( this, eventSource, ReadThenSubscribeToEvent );

        disposables.Add ( eventSourceSide.Container    );
        disposables.Add ( eventSourceSide.Subscription );
        disposables.Add ( subscribedBinding );
    }

    Expression IExpressionTransformer.Transform ( Expression expression )
    {
        var binding = Expression.Constant ( this );

        expression = new EnumerableToBindableEnumerableVisitor ( binding ).Visit ( expression );
        expression = new AggregateInvalidatorVisitor           ( )        .Visit ( expression );
        expression = Sentinel.Transformer.Transform                              ( expression );
        expression = new BinderServicesReplacer                ( binding ).Visit ( expression );

        return expression;
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

        if ( result.Faulted )
        {
            Services.UnhandledExceptionHandler.Catch ( result.Exception );
            return;
        }

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

    string IDebugView.Display ( ) => DebugView.Display ( eventSourceSide.Accessor.Expression.Body ) + "." + Event.Name +
                                     "(" + DebugView.Display ( SubscribedBinding ) + ")";

    private class Side
    {
        public Side ( EventBinding < TSource, TArgs > binding, LambdaExpression expression, Action < Side > callback )
        {
            Accessor     = binding.Services.SchedulerSelector.SelectScheduler ( expression ) is { } scheduler ?
                           new ScheduledExpressionAccessor < TSource > ( scheduler, expression, binding ) :
                           new ExpressionAccessor          < TSource > ( expression, binding );
            Callback     = callback;
            Container    = new CompositeDisposable ( );
            Subscription = new ExpressionSubscription < TSource > ( binding.Services, expression, (e, s, o, m) => Callback ( this ) );
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

[ DebuggerDisplay ( DebugView.DebuggerDisplay ) ]
public sealed class ContainerBinding : IBinding, IDebugView
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

    string IDebugView.Display ( ) => $"Disposables = { disposables.Count }";
}

[ DebuggerDisplay ( DebugView.DebuggerDisplay ) ]
public sealed class CompositeBinding < TSource > : IBinding < TSource >, IDebugView
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

    string IDebugView.Display ( )
    {
        var bindingsCount = disposables.ToArray ( ).OfType < IBinding > ( ).Count ( );

        return $"Bindings = { bindingsCount }, Disposables = { disposables.Count - bindingsCount }";
    }
}