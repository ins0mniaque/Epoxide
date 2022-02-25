using System.Runtime.ExceptionServices;

using Epoxide.Disposables;

namespace Epoxide;

public interface IAwaitable
{
    IDisposable Await < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback );
}

// TODO: Add IAsyncEnumerable/IObservable support
public static class Awaitable
{
    public static object? AsAwaitable < T, TResult > ( this Task < T > source, IScheduler? scheduler, Func < T, TResult >? selector, CancellationTokenSource cancellation )
    {
        if ( source == null )
            throw new ArgumentNullException ( nameof ( source ) );

        return new AwaitableTask < T, TResult > ( source, scheduler, selector, cancellation );
    }

    public static object? AsDelayed < T > ( this T source, TimeSpan delay )
    {
        return new AwaitableDelay < T > ( source, delay );
    }

    public static T Delay < T > ( this T source, TimeSpan delay )
    {
        return source;
    }

    // TODO: Move to AwaitableTask?
    public static IDisposable AwaitTask < T, TState > ( Task < T > task, CancellationTokenSource cancellation, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        if ( task.IsCompleted )
        {
            if      (   task.IsFaulted  ) callback ( state, default, BindingException.Capture ( task.Exception ) );
            else if ( ! task.IsCanceled ) callback ( state, task.Result, default );

            return Disposable.Empty;
        }

        AwaitTask ( task, state, callback );

        return cancellation;

        async static void AwaitTask ( Task < T > task, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
        {
            try                                  { callback ( state, await task.ConfigureAwait ( false ), default ); }
            catch ( OperationCanceledException ) { }
            catch ( Exception exception )        { callback ( state, default, BindingException.Capture ( exception ) ); }
        }
    }
}

public class AwaitableDelay < T > : IAwaitable
{
    public AwaitableDelay ( T source, TimeSpan delay )
    {
        Source = source;
        Delay  = delay;
    }

    public T        Source { get; }
    public TimeSpan Delay  { get; }

    public IDisposable Await < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        var cancellation = new CancellationTokenSource ( );

        return Awaitable.AwaitTask ( DelaySource ( cancellation.Token ), cancellation, state, callback );
    }

    private async Task < T > DelaySource ( CancellationToken cancellationToken )
    {
        await Task.Delay ( Delay, cancellationToken );

        return Source;
    }
}

public class AwaitableTask < T, TResult > : IAwaitable
{
    public AwaitableTask ( Task < T > task, IScheduler? scheduler, Func < T, TResult >? selector, CancellationTokenSource cancellation )
    {
        Task         = task;
        Scheduler    = scheduler;
        Selector     = selector;
        Cancellation = cancellation;
    }

    public Task < T >              Task         { get; }
    public IScheduler?             Scheduler    { get; }
    public Func < T, TResult >?    Selector     { get; }
    public CancellationTokenSource Cancellation { get; }

    public IDisposable Await < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        return Scheduler != null ? AwaitWithScheduler    ( Scheduler, state, callback ) :
                                   AwaitWithoutScheduler ( state, callback );
    }

    private IDisposable AwaitWithScheduler < TState > ( IScheduler scheduler, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        var token = new SerialDisposable ( );

        token.Disposable = Awaitable.AwaitTask ( Task, Cancellation, state, (state, value, exception) =>
        {
            if ( exception != null ) callback ( state, default, exception );
            else                     token.Disposable = scheduler.Schedule ( state, state => SelectResult ( state, value, callback ) );
        } );

        return token;
    }

    private IDisposable AwaitWithoutScheduler < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        return Awaitable.AwaitTask ( Task, Cancellation, state, (state, value, exception) =>
        {
            if ( exception != null ) callback ( state, default, exception );
            else                     SelectResult ( state, value, callback );
        } );
    }

    private void SelectResult < TState > ( TState state, object? value, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        if ( Selector is { } selector ) SelectResult ( (T) value, selector, state, callback );
        else                            callback     ( state, value, default );

        static void SelectResult ( T source, Func < T, TResult > selector, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
        {
            try                                  { callback ( state, selector ( source ), default ); }
            catch ( OperationCanceledException ) { }
            catch ( Exception exception )        { callback ( state, default, BindingException.Capture ( exception ) ); }
        }
    }
}