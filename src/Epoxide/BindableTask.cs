using System.Linq.Expressions;

namespace Epoxide;

// TODO: Add IAsyncEnumerable support
public static class AsyncResult
{
    public static object? Create < T, TResult > ( this Task < T > source, Expression expression, Func< T, TResult >? selector, CancellationToken cancellationToken )
    {
        if ( source == null )
            throw new ArgumentNullException ( nameof ( source ) );

        return new AsyncResult < T, TResult > ( source, expression, selector, cancellationToken );
    }
}

public interface IAsyncResult
{
    Expression?       Selector          { get; }
    CancellationToken CancellationToken { get; }

    Task < object? > Run         ( );
    object?          RunSelector ( object? result );
}

public class AsyncResult < T, TResult > : IAsyncResult
{
    public AsyncResult ( Task < T > task, Expression selector, Func < T, TResult >? compiledSelector, CancellationToken cancellationToken )
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

    public async Task < object? > Run         ( )                => await Task;
    public object?                RunSelector ( object? result ) => CompiledSelector ( (T) result );
}