using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Epoxide.ChangeTracking;

public abstract class PropertyChangedEventArgsFactory
{
    protected static PropertyChangedEventArgs Create ( [ CallerMemberName ] string? propertyName = null )
    {
        return new PropertyChangedEventArgs ( propertyName );
    }
}