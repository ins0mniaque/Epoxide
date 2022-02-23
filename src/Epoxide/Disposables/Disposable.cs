namespace Epoxide.Disposables;

public static class Disposable
{
    public static IDisposable Empty => EmptyDisposable.Instance;

    [ DebuggerDisplay ( nameof ( Disposable ) + "." + nameof ( Empty ) ) ]
    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new EmptyDisposable ( );

        private EmptyDisposable ( ) { }

        public void Dispose ( ) { }
    }
}