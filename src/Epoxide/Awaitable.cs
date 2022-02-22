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
    public static object? AsAwaitable < T, TResult > ( this Task < T > source, IScheduler? scheduler, Func < T, TResult >? selector, CancellationToken cancellationToken )
    {
        if ( source == null )
            throw new ArgumentNullException ( nameof ( source ) );

        return new AwaitableTask < T, TResult > ( source, scheduler, selector, cancellationToken );
    }
}

public class AwaitableTask < T, TResult > : IAwaitable
{
    public AwaitableTask ( Task < T > task, IScheduler? scheduler, Func < T, TResult >? selector, CancellationToken cancellationToken )
    {
        Task              = task;
        Scheduler         = scheduler;
        Selector          = selector;
        CancellationToken = cancellationToken;
    }

    public Task < T >           Task              { get; }
    public IScheduler?          Scheduler         { get; }
    public Func < T, TResult >? Selector          { get; }
    public CancellationToken    CancellationToken { get; }

    public IDisposable Await < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        return Scheduler != null ? AwaitWithScheduler    ( Scheduler, state, callback ) :
                                   AwaitWithoutScheduler ( state, callback );
    }

    private IDisposable AwaitWithScheduler < TState > ( IScheduler scheduler, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        var token = new SerialDisposable ( );

        token.Disposable = AwaitTask ( state, (state, value, exception) =>
        {
            if ( exception != null ) callback ( state, default, exception );
            else                     token.Disposable = scheduler.Schedule ( state, state => SelectResult ( state, value, callback ) );
        } );

        return token;
    }

    private IDisposable AwaitWithoutScheduler < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        return AwaitTask ( state, (state, value, exception) =>
        {
            if ( exception != null ) callback ( state, default, exception );
            else                     SelectResult ( state, value, callback );
        } );
    }

    private IDisposable AwaitTask < TState > ( TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        if ( Task.IsCompleted )
        {
            if      (   Task.IsFaulted  ) callback ( state, default, ExceptionDispatchInfo.Capture ( Task.Exception ) );
            else if ( ! Task.IsCanceled ) callback ( state, Task.Result, default );

            return Disposable.Empty;
        }

        var cancellation = Disposable.Empty;
        if ( CancellationToken != CancellationToken.None )
            cancellation = CancellationTokenSource.CreateLinkedTokenSource ( CancellationToken );

        AwaitTask ( Task, state, callback );

        return cancellation;

        async static void AwaitTask ( Task < T > task, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
        {
            try                                  { callback ( state, await task.ConfigureAwait ( false ), default ); }
            catch ( OperationCanceledException ) { }
            catch ( Exception exception )        { callback ( state, default, ExceptionDispatchInfo.Capture ( exception ) ); }
        }
    }

    private void SelectResult < TState > ( TState state, object? value, Action < TState, object?, ExceptionDispatchInfo? > callback )
    {
        if ( Selector is { } selector ) SelectResult ( (T) value, selector, state, callback );
        else                            callback     ( state, value, default );

        static void SelectResult ( T source, Func < T, TResult > selector, TState state, Action < TState, object?, ExceptionDispatchInfo? > callback )
        {
            try                                  { callback ( state, selector ( source ), default ); }
            catch ( OperationCanceledException ) { }
            catch ( Exception exception )        { callback ( state, default, ExceptionDispatchInfo.Capture ( exception ) ); }
        }
    }
}