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

public interface ISchedulerSelector
{
    IScheduler? SelectScheduler ( Expression expression );
}

// TODO: Rename...
public class NoSchedulerSelector : ISchedulerSelector
{
    public IScheduler? SelectScheduler ( Expression expression ) => null;
}

public interface IScheduler
{
    IDisposable Schedule < TState > ( TState state, Action < TState > action );
}

public delegate void ExpressionAccessCallback < TSource, TState, TResult > ( TSource source, TState state, TResult result );

public abstract class ExpressionAccessResult
{
    protected ExpressionAccessResult ( IDisposable token, bool succeeded )
    {
        Token = token;
        Succeeded = succeeded;
    }

    protected ExpressionAccessResult ( IDisposable token, Exception exception )
    {
        Token = token;
        Exception = exception;
    }

    public IDisposable Token { get; }
    public Exception? Exception { get; }

    public bool Succeeded { get; }
    public bool Faulted   => Exception != null;
}

public sealed class ExpressionReadResult : ExpressionAccessResult
{
    public static ExpressionReadResult Failure ( IDisposable token )                      => new ExpressionReadResult ( token );
    public static ExpressionReadResult Fault   ( IDisposable token, Exception exception ) => new ExpressionReadResult ( token, exception );
    public static ExpressionReadResult Success ( IDisposable token, object?   value     ) => new ExpressionReadResult ( token, value );

    private ExpressionReadResult ( IDisposable token )                      : base ( token, false     ) { }
    private ExpressionReadResult ( IDisposable token, Exception exception ) : base ( token, exception ) { }
    private ExpressionReadResult ( IDisposable token, object?   value     ) : base ( token, true      )
    {
        Value = value;
    }

    public object? Value { get; }
}

public sealed class ExpressionWriteResult : ExpressionAccessResult
{
    public static ExpressionWriteResult Failure ( IDisposable token )                      => new ExpressionWriteResult ( token );
    public static ExpressionWriteResult Fault   ( IDisposable token, Exception exception ) => new ExpressionWriteResult ( token, exception );
    public static ExpressionWriteResult Success ( IDisposable token, object target, MemberInfo member, object? value )
    {
        return new ExpressionWriteResult ( token, target, member, value );
    }

    private ExpressionWriteResult ( IDisposable token )                      : base ( token, false     ) { }
    private ExpressionWriteResult ( IDisposable token, Exception exception ) : base ( token, exception ) { }
    private ExpressionWriteResult ( IDisposable token, object target, MemberInfo member, object? value ) : base ( token, true )
    {
        Target = target;
        Member = member;
        Value  = value;
    }

    public object     Target { get; }
    public MemberInfo Member { get; }
    public object?    Value  { get; }
}

// TODO: Properties 
public interface IExpressionAccessor < TSource >
{
    LambdaExpression Expression   { get; }
    bool             IsCollection { get; }
    bool             IsWritable   { get; }

    IDisposable Read  < TState > ( TSource source, TState state,                ExpressionAccessCallback < TSource, TState, ExpressionReadResult  > callback );
    IDisposable Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback );
}

public interface IAwaitable
{
    IDisposable Await < TState > ( TState state, Action < TState, object?, Exception? > callback );
}

public class ExpressionAccessor < TSource > : IExpressionAccessor < TSource >
{
    public ExpressionAccessor ( LambdaExpression expression )
    {
        Expression   = expression ?? throw new ArgumentNullException ( nameof ( expression ) );
        IsCollection = ExprHelper.GetGenericInterfaceArguments ( expression.Body.Type, typeof ( ICollection < > ) ) != null;
        IsWritable   = expression.Body.NodeType == ExpressionType.MemberAccess && ( (MemberExpression) expression.Body ).Member is PropertyInfo { CanWrite: true } or FieldInfo;
    }

    public LambdaExpression Expression   { get; }
    public bool             IsCollection { get; }
    public bool             IsWritable   { get; }

    public virtual IDisposable Read < TState > ( TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        var token = new SerialDisposable ( );

        Read ( token, source, state, callback );

        return token;
    }

    public virtual IDisposable Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback )
    {
        if ( ! IsWritable )
            throw NotWritable;

        var token = new SerialDisposable ( );

        Write ( token, source, state, value, callback );

        return token;
    }

    protected void Read < TState > ( SerialDisposable token, TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        var value = TryReadValue ( source, out var exception );

        if      ( exception != null                 ) callback ( source, state, ExpressionReadResult.Fault   ( Disconnected ( token ), exception ) );
        else if ( value == Sentinel.Value           ) callback ( source, state, ExpressionReadResult.Failure ( Disconnected ( token ) ) );
        else if ( value is not IAwaitable awaitable ) callback ( source, state, ExpressionReadResult.Success ( Disconnected ( token ), value ) );
        else                                          Await    ( awaitable, token, source, state, callback );
    }

    protected static async void Await < TState > ( IAwaitable awaitable, SerialDisposable token, TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        token.Disposable = awaitable.Await ( state, (state, value, exception) =>
        {
            if      ( exception != null                 ) callback ( source, state, ExpressionReadResult.Fault   ( Disconnected ( token ), exception ) );
            else if ( value == Sentinel.Value           ) callback ( source, state, ExpressionReadResult.Failure ( Disconnected ( token ) ) );
            else if ( value is not IAwaitable awaitable ) callback ( source, state, ExpressionReadResult.Success ( Disconnected ( token ), value ) );
            else                                          Await    ( awaitable, token, source, state, callback );
        } );
    }

    protected IDisposable Write < TState > ( SerialDisposable token, TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback )
    {
        var target = TryReadTarget ( source, out var exception );

        if ( exception != null )
        {
            callback ( source, state, ExpressionWriteResult.Fault ( Disconnected ( token ), exception ) );
            return token;
        }

        if ( target == null || target == Sentinel.Value )
        {
            callback ( source, state, ExpressionWriteResult.Failure ( Disconnected ( token ) ) );
            return token;
        }

        if ( value is not IAwaitable awaitable )
        {
            TryWrite ( target, value, out exception );

            if ( exception != null ) callback ( source, state, ExpressionWriteResult.Fault   ( Disconnected ( token ), exception ) );
            else                     callback ( source, state, ExpressionWriteResult.Success ( Disconnected ( token ), target, Target.Member, value ) );
        }
        else Await ( awaitable, token, source, state, (source, state, read) =>
        {
            if ( read.Succeeded )
            {
                TryWrite ( target, read.Value, out exception );

                if ( exception != null ) callback ( source, state, ExpressionWriteResult.Fault   ( Disconnected ( token ), exception ) );
                else                     callback ( source, state, ExpressionWriteResult.Success ( Disconnected ( token ), target, Target.Member, read.Value ) );
            }
            else if ( read.Faulted ) callback ( source, state, ExpressionWriteResult.Fault   ( Disconnected ( token ), read.Exception ) );
            else                     callback ( source, state, ExpressionWriteResult.Failure ( Disconnected ( token ) ) );
        } );

        return token;
    }

    protected object? TryReadValue ( TSource source, out Exception? exception )
    {
        try                   { exception = null; return ReadValue ( source ); }
        catch ( Exception e ) { exception = e;    return null; }
    }

    protected object? TryReadTarget ( TSource source, out Exception? exception )
    {
        try                   { exception = null; return ReadTarget ( source ); }
        catch ( Exception e ) { exception = e;    return null; }
    }

    // TODO: Emit code to set value
    protected void TryWrite ( object target, object? value, out Exception? exception )
    {
        try                   { exception = null; Target.Member.SetValue ( target, value ); }
        catch ( Exception e ) { exception = e; }
    }

    protected static SerialDisposable Disconnected ( SerialDisposable token )
    {
        token.Disposable = null;

        return token;
    }

    private   Func < TSource, object? >? readValue;
    protected Func < TSource, object? >  ReadValue => readValue ??= Compile ( Expression.Body, Expression.Parameters );

    private   Func < TSource, object? >? readTarget;
    protected Func < TSource, object? >  ReadTarget   => readTarget ??= Compile ( Target.Expression, Expression.Parameters );
    protected MemberExpression           Target       => IsWritable ? (MemberExpression) Expression.Body : throw NotWritable;
    protected InvalidOperationException  NotWritable  => new InvalidOperationException ( $"Expression { Expression } is not writable." );

    protected static Func < TSource, object? > Compile ( Expression expression, IReadOnlyCollection < ParameterExpression > parameters )
    {
        expression = expression.AddSentinel ( );
        if ( expression.Type != typeof ( object ) )
            expression = System.Linq.Expressions.Expression.Convert ( expression, typeof ( object ) );

        return CachedExpressionCompiler.Compile ( System.Linq.Expressions.Expression.Lambda < Func < TSource, object? > > ( expression, parameters ) );
    }
}


public class ScheduledExpressionAccessor < TSource > : ExpressionAccessor < TSource >
{
    public ScheduledExpressionAccessor ( LambdaExpression expression, IScheduler scheduler ) : base ( expression )
    {
        Scheduler = scheduler;
    }

    public IScheduler Scheduler { get; }

    public override IDisposable Read < TState > ( TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        var token = new SerialDisposable ( );

        token.Disposable = Scheduler.Schedule ( state, state => Read ( token, source, state, callback ) );

        return token;
    }

    public override IDisposable Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback )
    {
        if ( ! IsWritable )
            throw NotWritable;

        var token = new SerialDisposable ( );

        token.Disposable = Scheduler.Schedule ( state, state => Write ( token, source, state, value, callback ) );

        return token;
    }
}

// TODO: Clean up and add clonable TriggerCollection
public sealed class Trigger < TSource >
{
    public ExpressionAccessor < TSource > Accessor;
    public MemberInfo Member;
    public IDisposable? Subscription;

    public static List < Trigger < TSource > > ExtractTriggers ( IBinderServices services, LambdaExpression lambda )
    {
        var extractor = new TriggerExtractorVisitor ( services, lambda.Parameters );
        extractor.Visit ( lambda.Body );
        return extractor.Triggers;
    }

    private class TriggerExtractorVisitor : ExpressionVisitor
    {
        public TriggerExtractorVisitor ( IBinderServices services, IReadOnlyCollection < ParameterExpression > parameters )
        {
            Services   = services;
            Parameters = parameters;
        }

        public IBinderServices                             Services   { get; }
        public IReadOnlyCollection < ParameterExpression > Parameters { get; }
        public List < Trigger < TSource > >                Triggers   { get; } = new ( );

        protected override Expression VisitLambda < T > ( Expression < T > node ) => node;

        protected override Expression VisitMember ( MemberExpression node )
        {
            base.VisitMember ( node );

            var expression = Expression.Lambda ( node.Expression, Parameters );

            Triggers.Add ( new Trigger < TSource >
            {
                Accessor = Services.SchedulerSelector.SelectScheduler  ( expression ) is { } scheduler ?
                           new ScheduledExpressionAccessor < TSource > ( expression, scheduler ) :
                           new ExpressionAccessor          < TSource > ( expression ),
                Member   = node.Member
            } );

            return node;
        }

        // TODO: Add method support?
        // protected override Expression VisitMethodCall ( MethodCallExpression node )
        // {
        //     base.VisitMethodCall ( node );
        //
        //     var expression = Expression.Lambda ( node.Expression, Parameters );
        //
        //    Triggers.Add ( new Trigger < TSource >
        //    {
        //        Accessor = Services.SchedulerSelector.SelectScheduler  ( expression ) is { } scheduler ?
        //                   new ScheduledExpressionAccessor < TSource > ( expression, scheduler ) :
        //                   new ExpressionAccessor          < TSource > ( expression ),
        //        Member   = node.Method
        //    } );
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

        triggers = Trigger < TSource >.ExtractTriggers ( services, expression );
    }

    public IDisposable Subscribe ( TSource source, ExpressionChangedCallback < TSource > callback )
    {
        var triggers     = this.triggers.Select ( t => new Trigger < TSource > { Accessor = t.Accessor, Member = t.Member } ).ToList ( );
        var subscription = new ExpressionSubscription < TSource > ( services, triggers, callback );

        subscription.Subscribe ( source );

        return subscription;
    }
}

// TODO: Handle exceptions
// TODO: Clean up callback
public sealed class ExpressionSubscription < TSource > : IDisposable
{
    readonly IBinderServices services;
    readonly List<Trigger<TSource>> triggers;

    readonly ExpressionChangedCallback < TSource > callback;

    public ExpressionSubscription ( IBinderServices services, LambdaExpression expression, ExpressionChangedCallback < TSource > callback )
    {
        this.services = services;
        this.callback = callback;
        this.triggers = Trigger < TSource >.ExtractTriggers ( services, expression );
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
            t.Subscription = t.Accessor.Read ( source, t, ReadAndSubscribe );
        }
    }

    private void ReadAndSubscribe ( TSource source, Trigger < TSource > t, ExpressionReadResult result )
    {
        if ( result.Succeeded && result.Value != null )
            t.Subscription = services.MemberSubscriber.Subscribe ( result.Value, t.Member, (o, m) => callback ( t.Accessor.Expression, Source, o, m ) );
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