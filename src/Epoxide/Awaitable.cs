using System.Linq.Expressions;

using Epoxide.Disposables;

namespace Epoxide;

public interface IAwaitable
{
    IDisposable Await < TState > ( TState state, Action < TState, object?, Exception? > callback );
}

// TODO: Add IAsyncEnumerable/IObservable support
public static class Awaitable
{
    public static object? AsAwaitable < T, TResult > ( this Task < T > source, Expression expression, Func < T, TResult >? selector, CancellationToken cancellationToken )
    {
        if ( source == null )
            throw new ArgumentNullException ( nameof ( source ) );

        return new AwaitableTask < T, TResult > ( source, expression, selector, cancellationToken );
    }
}

// TODO: Needs access to scheduler selector
public class AwaitableTask < T, TResult > : IAwaitable
{
    public AwaitableTask ( Task < T > task, Expression selector, Func < T, TResult >? compiledSelector, CancellationToken cancellationToken )
    {
        Task              = task;
        Selector          = selector;
        CompiledSelector  = compiledSelector;
        CancellationToken = cancellationToken;
    }

    public Task < T >           Task              { get; }
    public Expression?          Selector          { get; }
    public Func < T, TResult >? CompiledSelector  { get; }
    public CancellationToken    CancellationToken { get; }

    public IDisposable Await < TState > ( TState state, Action < TState, object?, Exception? > callback )
    {
        // TODO: Scheduling
        var scheduler = (IScheduler?) null;

        return scheduler != null ? AwaitWithScheduler    ( scheduler, state, callback ) :
                                   AwaitWithoutScheduler ( state, callback );
    }

    private IDisposable AwaitWithScheduler < TState > ( IScheduler scheduler, TState state, Action < TState, object?, Exception? > callback )
    {
        var token = new SerialDisposable ( );

        token.Disposable = AwaitTask ( state, (state, value, exception) =>
        {
            if ( exception != null ) callback ( state, default, exception );
            else                     token.Disposable = scheduler.Schedule ( state, state => SelectResult ( state, value, callback ) );
        } );

        return token;
    }

    private IDisposable AwaitWithoutScheduler < TState > ( TState state, Action < TState, object?, Exception? > callback )
    {
        return AwaitTask ( state, (state, value, exception) =>
        {
            if ( exception != null ) callback ( state, default, exception );
            else                     SelectResult ( state, value, callback );
        } );
    }

    private IDisposable AwaitTask < TState > ( TState state, Action < TState, object?, Exception? > callback )
    {
        if ( Task.IsCompleted )
        {
            if      (   Task.IsFaulted  ) callback ( state, default, Task.Exception );
            else if ( ! Task.IsCanceled ) callback ( state, Task.Result, default );

            return Disposable.Empty;
        }

        var cancellation = Disposable.Empty;
        if ( CancellationToken != CancellationToken.None )
            cancellation = CancellationTokenSource.CreateLinkedTokenSource ( CancellationToken );

        AwaitTask ( Task, state, callback );

        return cancellation;

        async static void AwaitTask ( Task < T > task, TState state, Action < TState, object?, Exception? > callback )
        {
            try                                  { callback ( state, await task.ConfigureAwait ( false ), default ); }
            catch ( OperationCanceledException ) { }
            catch ( Exception exception )        { callback ( state, default, exception ); }
        }
    }

    private void SelectResult < TState > ( TState state, object? value, Action < TState, object?, Exception? > callback )
    {
        if ( CompiledSelector is { } selector ) SelectResult ( (T) value, selector, state, callback );
        else                                    callback     ( state, value, default );

        static void SelectResult ( T source, Func < T, TResult > selector, TState state, Action < TState, object?, Exception? > callback )
        {
            try                                  { callback ( state, selector ( source ), default ); }
            catch ( OperationCanceledException ) { }
            catch ( Exception exception )        { callback ( state, default, exception ); }
        }
    }
}