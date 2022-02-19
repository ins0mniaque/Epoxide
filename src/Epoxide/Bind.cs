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

    IBinding < TSource > Bind < TSource, T > ( TSource source, Expression < Func < TSource, T > > specifications );
}

public static class BinderExtensions
{
    public static IBinding Bind < T > ( this IBinder binder, Expression < Func < T > > specifications )
    {
        return binder.Bind ( null, Expression.Lambda < Func < object?, T > > ( specifications.Body, CachedExpressionCompiler.UnusedParameter ) );
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

    public IBinding < TSource > Bind < TSource, T > ( TSource source, Expression < Func < TSource, T > > specifications )
    {
        var binding = Parse ( source, specifications.Parameters [ 0 ], specifications );

        binding.Bind ( );

        return binding;
    }

    IBinding < TSource > Parse < TSource > ( TSource source, ParameterExpression parameter, Expression expr )
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
                var lamb = (LambdaExpression) m.Arguments [ ^1 ];
                var args = lamb.Parameters.Count > 0 ? lamb.Parameters [ 0 ].Type : typeof ( object );

                // TODO: Call generic version of Parse (EventArgs type)
                throw new NotImplementedException ( );
                // return new EventBinding < TSource > ( Services, m.Arguments[ 0 ], parameter, name, Parse ( Activator.CreateInstance(args), lamb.Parameters [ 0 ], lamb ) );
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

            return new CompositeBinding < TSource > ( Services, source, parts.Select ( part => Parse ( source, parameter, part ) ) );
        }

        if ( expr.NodeType == ExpressionType.Equal )
        {
            var b = (BinaryExpression) expr;
            return new Binding < TSource > ( Services, source, parameter, b.Left, b.Right );
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

// TODO: Add state to evaluation and Binding < TSource >
public abstract class ExpressionAccessor
{
    public static ExpressionAccessor < TSource > Create < TSource > ( ParameterExpression parameter, Expression expression )
    {
        if ( ExprHelper.IsWritable ( expression ) )
            return new Writable < TSource > ( parameter, expression );

        return new Readable < TSource > ( parameter, expression );
    }

    protected class Readable < T > : ExpressionAccessor < T >
    {
        public Readable ( ParameterExpression parameter, Expression expression ) : base ( expression )
        {
            Parameter = parameter;
            CanAdd    = ExprHelper.GetGenericInterfaceArguments ( expression.Type, typeof ( ICollection < > ) ) != null;
        }

        protected ParameterExpression Parameter { get; }

        // TODO: Convert only if necessary
        private Func < T, object? >? read;
        public  Func < T, object? >  Read => read ??= CachedExpressionCompiler.Compile ( Expression.Lambda < Func < T, object? > > ( Expression.Convert ( Expression.AddSentinel ( ), typeof ( object ) ), Parameter ) );

        public override bool CanAdd { get; }
        public override bool CanWrite => false;

        public override bool TryRead ( T source, out object? value )
        {
            value = Read ( source );

            return value != Sentinel.Value;
        }

        public override bool TryWrite ( T source, object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member )
        {
            throw new InvalidOperationException ( $"Expression { Expression } is not writable." );
        }
    }

    protected class Writable < T > : Readable < T >
    {
        public Writable ( ParameterExpression parameter, Expression expression ) : base ( parameter, expression ) { }

        // TODO: Convert only if necessary
        private Func < T, object? >? readTarget;
        public  Func < T, object? >  ReadTarget => readTarget ??= CachedExpressionCompiler.Compile ( Expression.Lambda < Func < T, object? > > ( Expression.Convert ( ( (MemberExpression) Expression ).Expression.AddSentinel ( ), typeof ( object ) ), Parameter ) );

        public override bool CanWrite => true;

        public override bool TryWrite ( T source, object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member )
        {
            member = ( (MemberExpression) Expression ).Member;
            target = ReadTarget ( source );
            if ( target == null || target == Sentinel.Value )
                return false;

            member.SetValue ( target, value );

            return true;
        }
    }
}

public abstract class ExpressionAccessor < TSource >
{
    protected ExpressionAccessor ( Expression expression )
    {
        Expression = expression;
    }

    public Expression Expression { get; }

    public abstract bool CanAdd   { get; }
    public abstract bool CanWrite { get; }

    public abstract bool TryRead  ( TSource source, out object? value );
    public abstract bool TryWrite ( TSource source, object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member );

    private class Readable < T > : ExpressionAccessor < T >
    {
        public Readable ( ParameterExpression parameter, Expression expression ) : base ( expression )
        {
            Parameter = parameter;
            CanAdd    = ExprHelper.GetGenericInterfaceArguments ( expression.Type, typeof ( ICollection < > ) ) != null;
        }

        protected ParameterExpression Parameter { get; }

        private Func < T, object? >? read;
        public  Func < T, object? >  Read => read ??= CachedExpressionCompiler.Compile ( Expression.Lambda < Func < T, object? > > ( Expression.AddSentinel ( ) ) );

        public override bool CanAdd { get; }
        public override bool CanWrite => false;

        public override bool TryRead ( T source, out object? value )
        {
            value = Read ( source );

            return value != Sentinel.Value;
        }

        public override bool TryWrite ( T source, object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member )
        {
            throw new InvalidOperationException ( $"Expression { Expression } is not writable." );
        }
    }

    private class Writable < T > : Readable < T >
    {
        public Writable ( ParameterExpression parameter, Expression expression ) : base ( parameter, expression ) { }

        private Func < T, object? >? readTarget;
        public  Func < T, object? >  ReadTarget => readTarget ??= CachedExpressionCompiler.Compile ( Expression.Lambda < Func < T, object? > > ( ( (MemberExpression) Expression ).Expression.AddSentinel ( ) ) );

        public override bool CanWrite => true;

        public override bool TryWrite ( T source, object? value, [NotNullWhen(true)] out object? target, [NotNullWhen(true)] out MemberInfo? member )
        {
            member = ( (MemberExpression) Expression ).Member;
            target = ReadTarget ( source );
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
public sealed class Trigger < TSource >
{
    public ExpressionAccessor < TSource > Accessor;
    public MemberInfo Member;
    public IDisposable? Subscription;

    public static List < Trigger < TSource > > ExtractTriggers ( ParameterExpression parameter, Expression s )
    {
        var extractor = new TriggerExtractorVisitor ( parameter );
        extractor.Visit ( s );
        return extractor.Triggers;
    }

    private class TriggerExtractorVisitor : ExpressionVisitor
    {
        public TriggerExtractorVisitor ( ParameterExpression parameter )
        {
            Parameter = parameter;
        }

        public List < Trigger < TSource > > Triggers  { get; } = new ( );
        public ParameterExpression          Parameter { get; }

        protected override Expression VisitLambda < T > ( Expression < T > node ) => node;

        protected override Expression VisitMember ( MemberExpression node )
        {
            base.VisitMember ( node );

            Triggers.Add ( new Trigger < TSource > { Accessor = ExpressionAccessor.Create < TSource > ( Parameter, node.Expression ), Member = node.Member } );

            return node;
        }

        // TODO: Add method support?
        // protected override Expression VisitMethodCall ( MethodCallExpression node )
        // {
        //     base.VisitMethodCall ( node );
        // 
        //     Triggers.Add ( new Trigger < TSource > { Accessor = ExpressionAccessor.Create < TSource > ( Parameter, node ), Member = node.Method } );
        // 
        //     return node;
        // }
    }
}

public sealed class ExpressionSubscription<TSource> : IDisposable
{
    readonly IBinderServices services;
    readonly Expression expression;
    readonly List<Trigger<TSource>> triggers;
    readonly MemberChangedCallback callback;

    TSource source;

    public ExpressionSubscription ( IBinderServices services, ParameterExpression parameter, Expression expression, MemberChangedCallback callback )
    {
        this.services = services;
        this.expression = expression;
        this.callback = callback;

        triggers = Trigger<TSource>.ExtractTriggers ( parameter, expression );
    }

    public void Subscribe ( TSource source )
    {
        this.source = source;

        foreach ( var t in triggers )
        {
            t.Subscription?.Dispose ( );
            t.Subscription = services.Scheduler.Schedule ( t.Accessor.Expression, t, ReadAndSubscribe ) ?? t.Subscription;
        }
    }

    private void ReadAndSubscribe ( Trigger < TSource > t )
    {
        if ( t.Accessor.TryRead ( source, out var target ) && target != null )
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

public sealed class Binding < TSource > : IBinding < TSource >
{
    private readonly CompositeDisposable disposables;

    private readonly Side leftSide;
    private readonly Side rightSide;
    private readonly Side initialSide;

    public Binding ( IBinderServices services, TSource source, ParameterExpression parameter, Expression left, Expression right )
    {
        disposables = new CompositeDisposable ( 4 );

        Services = services;
        Source   = source;

        var binding = Expression.Constant ( this );

        left = new EnumerableToCollectionVisitor         ( right.Type ).Visit ( left );
        left = new EnumerableToBindableEnumerableVisitor ( binding )   .Visit ( left );
        left = new AggregateInvalidatorVisitor           ( )           .Visit ( left );

        right = new EnumerableToCollectionVisitor         ( left.Type ).Visit ( right );
        right = new EnumerableToBindableEnumerableVisitor ( binding )  .Visit ( right );
        right = new AggregateInvalidatorVisitor           ( )          .Visit ( right );

        leftSide  = new Side ( services, parameter, left,  ScheduleReadThenWriteToOtherSide );
        rightSide = new Side ( services, parameter, right, ScheduleReadThenWriteToOtherSide );

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

    // TODO: Invalidate on source change
    public TSource                        Source { get; set; }
    public ExpressionAccessor < TSource > Left   => leftSide .Accessor;
    public ExpressionAccessor < TSource > Right  => rightSide.Accessor;
    public object?                        Value  { get; private set; }

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
        if ( side.Accessor.TryWrite ( Source, side.OtherSide.Value, out var target, out var member ) )
            if ( ! Equals ( Value, Value = side.OtherSide.Value ) )
                Services.MemberSubscriber.Invalidate ( side.Accessor.Expression, member );

        side.Subscription.Subscribe ( Source );
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

        var read = side.Accessor.TryRead ( Source, out var value );

        side.Value = read ? value : null;

        if ( value is not IBindableTask )
            side.Subscription.Subscribe ( Source );

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
        public Side ( IBinderServices services, ParameterExpression parameter, Expression expression, Action < Side > callback )
        {
            Accessor     = ExpressionAccessor.Create < TSource > ( parameter, expression );
            Callback     = callback;
            Container    = new CompositeDisposable ( );
            Subscription = new ExpressionSubscription < TSource > ( services, parameter, expression, (o, m) => Callback ( this ) );
        }

        public ExpressionAccessor < TSource >     Accessor     { get; }
        public Action < Side >                    Callback     { get; set; }
        public CompositeDisposable                Container    { get; }
        public ExpressionSubscription < TSource > Subscription { get; }
        public Side                               OtherSide    { get; set; }
        public object?                            Value        { get; set; }
    }
}

public sealed class EventBinding < TSource, TArgs > : IBinding < TSource >
{
    readonly CompositeDisposable disposables;
    readonly IBinding            binding;

    public EventBinding ( IBinderServices services, Expression source, ParameterExpression parameter, string eventName, IBinding < TArgs > binding )
    {
        disposables = new CompositeDisposable ( 1 );

        Services = services;

        disposables.Add ( this.binding = binding );

        // TODO: Read source, hook event
    }

    public IBinderServices Services { get; }

    // TODO: Invalidate on source change
    public TSource Source { get; set; }

    public void Bind   ( ) => binding.Bind   ( );
    public void Unbind ( ) => binding.Unbind ( );

    public void Attach  ( IDisposable disposable ) => disposables.Add     ( disposable );
    public bool Detach  ( IDisposable disposable ) => disposables.Remove  ( disposable );
    public void Dispose ( )                        => disposables.Dispose ( );
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

    public CompositeBinding ( IBinderServices services, TSource source, IEnumerable < IBinding > bindings )
    {
        disposables = new CompositeDisposable ( );

        Services = services;
        Source   = source;

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