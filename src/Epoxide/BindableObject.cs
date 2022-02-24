using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Epoxide;

public abstract class BindableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    [ SuppressMessage ( "Design", "CA1030:Use events where appropriate", Justification = "Allow raising event from derived class" ) ]
    protected void Raise ( PropertyChangedEventArgs args )
    {
        PropertyChanged?.Invoke ( this, args );
    }

    protected bool Set < T > ( ref T field, T value, PropertyChangedEventArgs args )
    {
        if ( EqualityComparer < T >.Default.Equals ( field, value ) )
            return false;
        
        field = value;

        PropertyChanged?.Invoke ( this, args );

        return true;
    }

    protected abstract class PropertyChangedEventArgsFactory : ChangeTracking.PropertyChangedEventArgsFactory { }
}