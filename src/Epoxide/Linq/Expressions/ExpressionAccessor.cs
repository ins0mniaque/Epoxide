using System.Runtime.ExceptionServices;

using Epoxide.Disposables;

namespace Epoxide.Linq.Expressions;

public delegate void ExpressionAccessCallback < TSource, TState, TResult > ( TSource source, TState state, TResult result );

public abstract class ExpressionAccessResult
{
    protected ExpressionAccessResult ( IDisposable token, bool succeeded )
    {
        Token     = token;
        Succeeded = succeeded;
    }

    protected ExpressionAccessResult ( IDisposable token, ExceptionDispatchInfo exception )
    {
        Token     = token;
        Exception = exception;
    }

    public IDisposable            Token     { get; }
    public ExceptionDispatchInfo? Exception { get; }
    public bool                   Succeeded { get; }
    public bool                   Faulted   => Exception != null;
}

public sealed class ExpressionReadResult : ExpressionAccessResult
{
    public static ExpressionReadResult Failure ( IDisposable token )                                  => new ExpressionReadResult ( token );
    public static ExpressionReadResult Fault   ( IDisposable token, ExceptionDispatchInfo exception ) => new ExpressionReadResult ( token, exception );
    public static ExpressionReadResult Success ( IDisposable token, object?               value     ) => new ExpressionReadResult ( token, value );

    private ExpressionReadResult ( IDisposable token )                                  : base ( token, false     ) { }
    private ExpressionReadResult ( IDisposable token, ExceptionDispatchInfo exception ) : base ( token, exception ) { }
    private ExpressionReadResult ( IDisposable token, object?               value     ) : base ( token, true      )
    {
        Value = value;
    }

    public object? Value { get; }
}

public sealed class ExpressionWriteResult : ExpressionAccessResult
{
    public static ExpressionWriteResult Failure ( IDisposable token )                                  => new ExpressionWriteResult ( token );
    public static ExpressionWriteResult Fault   ( IDisposable token, ExceptionDispatchInfo exception ) => new ExpressionWriteResult ( token, exception );
    public static ExpressionWriteResult Success ( IDisposable token, object target, MemberInfo member, object? value )
    {
        return new ExpressionWriteResult ( token, target, member, value );
    }

    private ExpressionWriteResult ( IDisposable token )                                  : base ( token, false     ) { }
    private ExpressionWriteResult ( IDisposable token, ExceptionDispatchInfo exception ) : base ( token, exception ) { }
    private ExpressionWriteResult ( IDisposable token, object target, MemberInfo member, object? value ) : base ( token, true )
    {
        Target = target;
        Member = member;
        Value  = value;
    }

    public object     Target { get; }
    public MemberInfo Member { get; }
    public object?    Value  { get; }
}

public interface IExpressionAccessor < TSource >
{
    LambdaExpression Expression   { get; }
    bool             IsCollection { get; }
    bool             IsWritable   { get; }

    IDisposable Read  < TState > ( TSource source, TState state,                ExpressionAccessCallback < TSource, TState, ExpressionReadResult  > callback );
    IDisposable Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback );
}

public interface IExpressionTransformer
{
    Expression Transform ( Expression expression );
}

[ DebuggerDisplay ( "{Expression}" ) ]
public class ExpressionAccessor < TSource > : IExpressionAccessor < TSource >
{
    public ExpressionAccessor ( LambdaExpression expression )                                     : this ( expression, Sentinel.Transformer, null           ) { }
    public ExpressionAccessor ( LambdaExpression expression, Type writeValueType )                : this ( expression, Sentinel.Transformer, writeValueType ) { }
    public ExpressionAccessor ( LambdaExpression expression, IExpressionTransformer transformer ) : this ( expression, transformer,          null           ) { }
    public ExpressionAccessor ( LambdaExpression expression, IExpressionTransformer transformer, Type? writeValueType = null )
    {
        Expression     = expression ?? throw new ArgumentNullException ( nameof ( expression ) );
        Transformer    = transformer;
        IsCollection   = expression.Body.Type.GetGenericInterfaceArguments ( typeof ( ICollection < > ) ) != null;
        Target         = expression.Body.ToWritable ( );
        IsWritable     = Target != null && Target.Member.CanSetFrom ( writeValueType ?? expression.Body.Type );
        WriteValueType = writeValueType ?? typeof ( object );
    }

    public LambdaExpression       Expression     { get; }
    public IExpressionTransformer Transformer    { get; }
    public bool                   IsCollection   { get; }
    public bool                   IsWritable     { get; }
    public Type                   WriteValueType { get; }

    private   MemberExpression? Target { get; }
    protected Expression        TargetExpression => Target?.Expression ?? throw NotWritable ( );
    protected MemberInfo        TargetMember     => Target?.Member     ?? throw NotWritable ( );

    public virtual IDisposable Read < TState > ( TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        var token = new SerialDisposable ( );

        Read ( token, source, state, callback );

        return token;
    }

    public virtual IDisposable Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback )
    {
        if ( ! IsWritable )
            throw NotWritable ( );

        var token = new SerialDisposable ( );

        Write ( token, source, state, value, callback );

        return token;
    }

    protected void Read < TState > ( SerialDisposable token, TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        var value = TryReadValue ( source, out var exception );

        if      ( exception != null                 ) callback ( source, state, ExpressionReadResult.Fault   ( Disconnected ( token ), exception ) );
        else if ( value == Sentinel.Value           ) callback ( source, state, ExpressionReadResult.Failure ( Disconnected ( token ) ) );
        else if ( value is not IAwaitable awaitable ) callback ( source, state, ExpressionReadResult.Success ( Disconnected ( token ), value ) );
        else                                          Await    ( awaitable, token, source, state, callback );
    }

    protected static async void Await < TState > ( IAwaitable awaitable, SerialDisposable token, TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        token.Disposable = awaitable.Await ( state, (state, value, exception) =>
        {
            if      ( exception != null                 ) callback ( source, state, ExpressionReadResult.Fault   ( Disconnected ( token ), exception ) );
            else if ( value == Sentinel.Value           ) callback ( source, state, ExpressionReadResult.Failure ( Disconnected ( token ) ) );
            else if ( value is not IAwaitable awaitable ) callback ( source, state, ExpressionReadResult.Success ( Disconnected ( token ), value ) );
            else                                          Await    ( awaitable, token, source, state, callback );
        } );
    }

    protected IDisposable Write < TState > ( SerialDisposable token, TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback )
    {
        var target = TryReadTarget ( source, out var exception );

        if ( exception != null )
        {
            callback ( source, state, ExpressionWriteResult.Fault ( Disconnected ( token ), exception ) );
            return token;
        }

        if ( target == null || target == Sentinel.Value )
        {
            callback ( source, state, ExpressionWriteResult.Failure ( Disconnected ( token ) ) );
            return token;
        }

        if ( value is not IAwaitable awaitable )
        {
            TryWrite ( target, value, out exception );

            if ( exception != null ) callback ( source, state, ExpressionWriteResult.Fault   ( Disconnected ( token ), exception ) );
            else                     callback ( source, state, ExpressionWriteResult.Success ( Disconnected ( token ), target, TargetMember, value ) );
        }
        else Await ( awaitable, token, source, state, (source, state, read) =>
        {
            if ( read.Succeeded )
            {
                TryWrite ( target, read.Value, out exception );

                if ( exception != null ) callback ( source, state, ExpressionWriteResult.Fault   ( Disconnected ( token ), exception ) );
                else                     callback ( source, state, ExpressionWriteResult.Success ( Disconnected ( token ), target, TargetMember, read.Value ) );
            }
            else if ( read.Faulted ) callback ( source, state, ExpressionWriteResult.Fault   ( Disconnected ( token ), read.Exception ) );
            else                     callback ( source, state, ExpressionWriteResult.Failure ( Disconnected ( token ) ) );
        } );

        return token;
    }

    protected object? TryReadValue ( TSource source, out ExceptionDispatchInfo? exception )
    {
        try                   { exception = null; return ReadValue ( source ); }
        catch ( Exception e ) { exception = BindingException.Capture ( e ); return null; }
    }

    protected object? TryReadTarget ( TSource source, out ExceptionDispatchInfo? exception )
    {
        try                   { exception = null; return ReadTarget ( source ); }
        catch ( Exception e ) { exception = BindingException.Capture ( e ); return null; }
    }

    protected void TryWrite ( object target, object? value, out ExceptionDispatchInfo? exception )
    {
        try                   { exception = null; WriteTarget ( target, value ); }
        catch ( Exception e ) { exception = BindingException.Capture ( e ); }
    }

    protected static SerialDisposable Disconnected ( SerialDisposable token )
    {
        token.Disposable = null;

        return token;
    }

    private   Func < TSource, object? >? readValue;
    protected Func < TSource, object? >  ReadValue => readValue ??= Compile ( Expression.Body, Expression.Parameters );

    private   Func < TSource, object? >? readTarget;
    protected Func < TSource, object? >  ReadTarget => readTarget ??= Compile ( TargetExpression, Expression.Parameters );

    private   Action < object, object? >? writeTarget;
    protected Action < object, object? >  WriteTarget => writeTarget ??= DynamicTypeAccessor.CompileSetter ( TargetMember, WriteValueType );

    protected Func < TSource, object? > Compile ( Expression expression, IReadOnlyCollection < ParameterExpression > parameters )
    {
        expression = Transformer.Transform ( expression );
        if ( expression.Type != typeof ( object ) )
            expression = System.Linq.Expressions.Expression.Convert ( expression, typeof ( object ) );

        return CachedExpressionCompiler.Compile ( System.Linq.Expressions.Expression.Lambda < Func < TSource, object? > > ( expression, parameters ) );
    }

    protected InvalidOperationException NotWritable ( )
    {
        return new InvalidOperationException ( $"Expression { Expression.Body } is not writable." );
    }
}

public class ScheduledExpressionAccessor < TSource > : ExpressionAccessor < TSource >
{
    public ScheduledExpressionAccessor ( IScheduler scheduler, LambdaExpression expression ) : this ( scheduler, expression, Sentinel.Transformer ) { }
    public ScheduledExpressionAccessor ( IScheduler scheduler, LambdaExpression expression, IExpressionTransformer transformer ) : base ( expression, transformer )
    {
        Scheduler = scheduler;
    }

    public IScheduler Scheduler { get; }

    public override IDisposable Read < TState > ( TSource source, TState state, ExpressionAccessCallback < TSource, TState, ExpressionReadResult > callback )
    {
        var token = new SerialDisposable ( );

        token.Disposable = Scheduler.Schedule ( state, state => Read ( token, source, state, callback ) );

        return token;
    }

    public override IDisposable Write < TState > ( TSource source, TState state, object? value, ExpressionAccessCallback < TSource, TState, ExpressionWriteResult > callback )
    {
        if ( ! IsWritable )
            throw NotWritable ( );

        var token = new SerialDisposable ( );

        token.Disposable = Scheduler.Schedule ( state, state => Write ( token, source, state, value, callback ) );

        return token;
    }
}