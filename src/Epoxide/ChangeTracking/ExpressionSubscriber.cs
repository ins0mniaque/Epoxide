using System.Linq.Expressions;
using System.Reflection;

using Epoxide.Linq.Expressions;

namespace Epoxide.ChangeTracking;

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
            t.Access      ?.Dispose ( );
            t.Subscription?.Dispose ( );
            t.Subscription = null;
            t.Access       = t.Accessor.Read ( source, t, ReadAndSubscribe );
        }
    }

    private void ReadAndSubscribe ( TSource source, Trigger < TSource > t, ExpressionReadResult result )
    {
        t.Access = null;

        if ( result.Faulted )
        {
            services.UnhandledExceptionHandler.Catch ( result.Exception );
            return;
        }

        if ( result.Succeeded && result.Value != null )
            t.Subscription = services.MemberSubscriber.Subscribe ( result.Value, t.Member, (o, m) => callback ( t.Accessor.Expression, Source, o, m ) );
    }

    public void Unsubscribe ( )
    {
        foreach ( var t in triggers )
        {
            t.Access      ?.Dispose ( );
            t.Subscription?.Dispose ( );
            t.Subscription = null;
            t.Access       = null;
        }
    }

    public void Dispose ( ) => Unsubscribe ( );
}

// TODO: Clean up and add clonable TriggerCollection
public sealed class Trigger < TSource >
{
    public ExpressionAccessor < TSource > Accessor;
    public MemberInfo Member;
    public IDisposable? Access;
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
                Accessor = Services.SchedulerSelector.SelectScheduler ( expression ) is { } scheduler ?
                           new ScheduledExpressionAccessor < TSource > ( scheduler, expression ) :
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
        //        Accessor = Services.SchedulerSelector.SelectScheduler ( expression ) is { } scheduler ?
        //                   new ScheduledExpressionAccessor < TSource > ( expression, scheduler ) :
        //                   new ExpressionAccessor          < TSource > ( expression ),
        //        Member   = node.Method
        //    } );
        //
        //     return node;
        // }
    }
}