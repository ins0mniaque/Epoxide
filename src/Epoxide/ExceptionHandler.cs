using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;

namespace Epoxide;

public interface IExceptionHandler
{
    void Catch ( ExceptionDispatchInfo exception );
}

public class RethrowExceptionHandler : IExceptionHandler
{
    public void Catch ( ExceptionDispatchInfo exception ) => exception.Throw ( );
}

public class BindingExceptionHandler : IExceptionHandler
{
    public BindingExceptionHandler ( IBinding binding, IExceptionHandler unhandledExceptionHandler )
    {
        Binding                   = binding;
        UnhandledExceptionHandler = unhandledExceptionHandler;
    }

    public IBinding          Binding                   { get; }
    public IExceptionHandler UnhandledExceptionHandler { get; }

    public void Catch ( ExceptionDispatchInfo exception )
    {
        if ( exception.SourceException is BindingException bindingException )
            bindingException.Binding = Binding;

        UnhandledExceptionHandler.Catch ( exception );
    }
}

/// <summary>
/// An exception that is thrown when an error is encountered while binding.
/// </summary>
[ Serializable ]
public sealed class BindingException : Exception
{
    public static ExceptionDispatchInfo Capture ( Exception exception )
    {
        exception = Unwrap ( exception );

        return ExceptionDispatchInfo.Capture ( new BindingException ( exception.Message, exception ) );
    }

    private static Exception Unwrap ( Exception exception )
    {
        if ( exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1 )
            exception = aggregate.InnerException;

        if ( exception is TargetInvocationException invocation )
            exception = invocation.InnerException;

        return exception;
    }

    [ NonSerialized ]
    private IBinding? binding;

    /// <summary>
    /// Initializes a new instance of the <see cref="BindingException" /> class.
    /// </summary>
    public BindingException ( ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BindingException" /> class.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public BindingException ( string message ) : base ( message ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BindingException" /> class.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public BindingException ( string message, Exception? innerException ) : base ( message, innerException ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BindingException" /> class.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="binding">The binding that was involved in the error.</param>
    public BindingException ( string message, IBinding binding ) : this ( message, null, binding ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BindingException" /> class.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    /// <param name="binding">The binding that was involved in the error.</param>
    public BindingException ( string message, Exception? innerException, IBinding binding ) : base ( message, innerException )
    {
        Binding = binding;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BindingException" /> class from a serialized form.
    /// </summary>
    /// <param name="info">The serialization info.</param>
    /// <param name="context">The streaming context being used.</param>
    public BindingException ( SerializationInfo info, StreamingContext context ) : base ( info, context ) { }

    /// <summary>
    /// Gets or sets the binding that was involved in the error.
    /// </summary>
    public IBinding? Binding
    {
        get => binding;
        set => binding = value;
    }

    public override string Message => $"Binding error: { base.Message }\nSource: { DebugView.Display ( Binding ) }";
}