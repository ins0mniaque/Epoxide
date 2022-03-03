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
        if ( exception.SourceException is StateMachineException stateMachineException )
            stateMachineException.Source = Binding;

        UnhandledExceptionHandler.Catch ( exception );
    }
}

// TODO: Rename exception

/// <summary>
/// An exception that is thrown when an error is encountered while running the state machine.
/// </summary>
[ Serializable ]
public sealed class StateMachineException : Exception
{
    public static ExceptionDispatchInfo Capture ( Exception exception )
    {
        exception = Unwrap ( exception );

        return ExceptionDispatchInfo.Capture ( new StateMachineException ( exception.Message, exception ) );
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
    private object? source;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMachineException" /> class.
    /// </summary>
    public StateMachineException ( ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMachineException" /> class.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public StateMachineException ( string message ) : base ( message ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMachineException" /> class.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public StateMachineException ( string message, Exception? innerException ) : base ( message, innerException ) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMachineException" /> class from a serialized form.
    /// </summary>
    /// <param name="info">The serialization info.</param>
    /// <param name="context">The streaming context being used.</param>
    public StateMachineException ( SerializationInfo info, StreamingContext context ) : base ( info, context ) { }

    /// <summary>
    /// Gets or sets the source that was involved in the error.
    /// </summary>
    public object? Source
    {
        get => source;
        set => source = value;
    }

    public override string Message => $"Error: { base.Message }\nSource: { DebugView.Display ( Source ) }";
}