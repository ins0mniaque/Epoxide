using Epoxide.ChangeTracking;
using Epoxide.Linq.Expressions;

namespace Epoxide;

public interface IBinderServices
{
    IMemberSubscriber     MemberSubscriber          { get; }
    ICollectionSubscriber CollectionSubscriber      { get; }
    ISchedulerSelector    SchedulerSelector         { get; }
    IExceptionHandler     UnhandledExceptionHandler { get; }
}

public class BindingServices : IBinderServices
{
    public BindingServices ( IMemberSubscriber     memberSubscriber,
                             ICollectionSubscriber collectionSubscriber,
                             ISchedulerSelector    schedulerSelector,
                             IExceptionHandler     unhandledExceptionHandler )
    {
        MemberSubscriber          = memberSubscriber;
        CollectionSubscriber      = collectionSubscriber;
        SchedulerSelector         = schedulerSelector;
        UnhandledExceptionHandler = unhandledExceptionHandler;
    }

    public IMemberSubscriber     MemberSubscriber          { get; }
    public ICollectionSubscriber CollectionSubscriber      { get; }
    public ISchedulerSelector    SchedulerSelector         { get; }
    public IExceptionHandler     UnhandledExceptionHandler { get; }
}

public class DefaultBindingServices : IBinderServices
{
    public IMemberSubscriber     MemberSubscriber          { get; } = new MemberSubscriber        ( new DefaultMemberSubscriptionFactory     ( ) );
    public ICollectionSubscriber CollectionSubscriber      { get; } = new CollectionSubscriber    ( new DefaultCollectionSubscriptionFactory ( ) );
    public ISchedulerSelector    SchedulerSelector         { get; } = new NoSchedulerSelector     ( );
    public IExceptionHandler     UnhandledExceptionHandler { get; } = new RethrowExceptionHandler ( );
}

// TODO: Rename to avoid conflict with System.Reflection.Binder
public interface IBinder
{
    IBinderServices Services { get; }

    IBinding < TSource > Bind < TSource > ( IBinderServices services, TSource source, Expression < Func < TSource, bool > > specifications );
}

public static class BinderExtensions
{
    public static IBinding Bind < T > ( this IBinder binder, T source, Expression < Func < T, bool > > specifications )
    {
        return binder.Bind ( binder.Services, source, specifications );
    }

    public static IBinding Bind < T > ( this IBinder binder, T source, Expression < Func < T, bool > > specifications, IExceptionHandler unhandledExceptionHandler )
    {
        var services = new BindingServices ( binder.Services.MemberSubscriber,
                                             binder.Services.CollectionSubscriber,
                                             binder.Services.SchedulerSelector,
                                             unhandledExceptionHandler );

        return binder.Bind ( services, source, specifications );
    }

    public static IBinding Bind ( this IBinder binder, Expression < Func < bool > > specifications )
    {
        return binder.Bind ( binder.Services, null, Expression.Lambda < Func < object?, bool > > ( specifications.Body, CachedExpressionCompiler.UnusedParameter ) );
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
            var x = Sentinel.Transformer.Transform ( m.Expression );
            if ( CachedExpressionCompiler.Evaluate ( x ) is { } obj && obj != Sentinel.Value )
                services.MemberSubscriber.Invalidate ( obj, m.Member, InvalidationMode.Forced );
        }
        else
            throw new NotSupportedException();
    }
}

public class Binder : IBinder
{
    private static IBinder? defaultBinder;
    public  static IBinder  Default
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

    public IBinding < TSource > Bind < TSource > ( IBinderServices services, TSource source, Expression < Func < TSource, bool > > specifications )
    {
        var binding = Parse ( services, source, specifications );

        binding.Bind ( );

        return binding;
    }

    private static MethodInfo? parse;

    private static IBinding < TSource > Parse < TSource > ( IBinderServices services, TSource source, LambdaExpression lambda )
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

                parse ??= new Func < IBinderServices, TSource, LambdaExpression, IBinding < TSource > > ( Parse ).Method.GetGenericMethodDefinition ( );

                var eventBinding     = parse.MakeGenericMethod ( eventArgsType ).Invoke ( null, new [ ] { services, Activator.CreateInstance ( eventArgsType ), eventLambda } );
                var eventBindingType = typeof ( EventBinding < , > ).MakeGenericType ( typeof ( TSource ), eventArgsType );

                // TODO: Create static method to cache reflection
                var eventBindingCtor = eventBindingType.GetConstructor ( new [ ] { typeof ( IBinderServices ), typeof ( LambdaExpression ), typeof ( EventInfo ), typeof ( IBinding < > ).MakeGenericType ( eventArgsType ) } );

                // TODO: Set source
                return (IBinding < TSource >) eventBindingCtor.Invoke ( new object [ ] { services, eventSource, eventInfo, eventBinding } );
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

            return new CompositeBinding < TSource > ( services, parts.Select ( part => Parse ( services, source, Expression.Lambda ( part, lambda.Parameters ) ) ) ) { Source = source };
        }

        if ( expr.NodeType == ExpressionType.Equal )
        {
            var b = (BinaryExpression) expr;

            var left  = Expression.Lambda ( b.Left,  lambda.Parameters );
            var right = Expression.Lambda ( b.Right, lambda.Parameters );

            return new Binding < TSource > ( services, left, right ) { Source = source };
        }

        throw new FormatException ( $"Invalid binding format: { expr }" );
    }
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