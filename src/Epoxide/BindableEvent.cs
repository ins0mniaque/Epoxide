namespace Epoxide;

[ AttributeUsage ( AttributeTargets.Method ) ]
public sealed class BindableEventAttribute : Attribute
{
    public BindableEventAttribute ( string eventName )
    {
        EventName = eventName;
    }

    internal BindableEventAttribute ( )
    {
        EventName = string.Empty;
    }

    public string EventName { get; }
}

public static class BindableEvent
{
    [ BindableEvent ]
    public static bool Event ( this object button, string name, Func < bool > binding )
    {
        throw new NotImplementedException ( );
    }

    [ BindableEvent ]
    public static bool Event < TArgs > ( this object button, string name, Func < TArgs, bool > binding )
    {
        throw new NotImplementedException ( );
    }
}