using System.ComponentModel;

namespace Epoxide;

public class ChangeTrackingTests
{
    class PropertyChangedEventObject
    {
        string stringValue = "";

        public int StringValueChangedCount { get; private set; }

        EventHandler stringValueChanged;

        public event EventHandler StringValueChanged
        {
            add
            {
                stringValueChanged += value;
                StringValueChangedCount++;
            }
            remove
            {
                stringValueChanged -= value;
                StringValueChangedCount--;
            }
        }

        public string StringValue
        {
            get { return stringValue; }
            set
            {
                if ( stringValue != value )
                {
                    stringValue = value;
                    if ( stringValueChanged != null )
                    {
                        stringValueChanged ( this, EventArgs.Empty );
                    }
                }
            }
        }
    }

    [ Fact ]
    public void PropertyChanged ( )
    {
        var obj = new PropertyChangedEventObject {StringValue = "Hello",};
        var left = "";

        Binder.Default.Bind ( ( ) => left == obj.StringValue );

        Assert.Equal ( 1, obj.StringValueChangedCount );

        Assert.Equal ( obj.StringValue, left );

        obj.StringValue = "Goodbye";

        Assert.Equal ( obj.StringValue, left );
    }

    [ Fact ]
    public void MultipleObjectPropertyChanged ( )
    {
        var objA = new PropertyChangedEventObject {StringValue = "Hello",};
        var objB = new PropertyChangedEventObject {StringValue = "World",};
        var left = "";

        Binder.Default.Bind ( ( ) => left == objA.StringValue + ", " + objB.StringValue );

        Assert.Equal ( "Hello, World", left );

        objA.StringValue = "Goodbye";

        Assert.Equal ( "Goodbye, World", left );

        objB.StringValue = "Mars";

        Assert.Equal ( "Goodbye, Mars", left );
    }

    [ Fact ]
    public void MultiplePropertyChanged ( )
    {
        var obj = new PropertyChangedEventObject {StringValue = "Hello",};
        var leftA = "";
        var leftB = "";

        Binder.Default.Bind ( ( ) => leftA == obj.StringValue );
        Binder.Default.Bind ( ( ) => leftB == obj.StringValue + "..." );

        Assert.Equal ( 1, obj.StringValueChangedCount );

        Assert.Equal ( "Hello", leftA );
        Assert.Equal ( "Hello...", leftB );

        obj.StringValue = "Goodbye";

        Assert.Equal ( "Goodbye", leftA );
        Assert.Equal ( "Goodbye...", leftB );
    }

    [ Fact ]
    public void RemoveMultiplePropertyChanged ( )
    {
        var obj = new PropertyChangedEventObject {StringValue = "Hello",};
        var leftA = "";
        var leftB = "";

        var bA = Binder.Default.Bind ( ( ) => leftA == obj.StringValue );
        var bB = Binder.Default.Bind ( ( ) => leftB == obj.StringValue + "..." );

        Assert.Equal ( 1, obj.StringValueChangedCount );

        Assert.Equal ( "Hello", leftA );
        Assert.Equal ( "Hello...", leftB );

        obj.StringValue = "Goodbye";

        Assert.Equal ( "Goodbye", leftA );
        Assert.Equal ( "Goodbye...", leftB );

        bA.Dispose ( );

        Assert.Equal ( 1, obj.StringValueChangedCount );

        obj.StringValue = "Hello Again";

        Assert.Equal ( "Goodbye", leftA );
        Assert.Equal ( "Hello Again...", leftB );

        bB.Dispose ( );

        Assert.Equal ( 0, obj.StringValueChangedCount );

        obj.StringValue = "Goodbye Again";

        Assert.Equal ( "Goodbye", leftA );
        Assert.Equal ( "Hello Again...", leftB );
    }

    [ Fact ]
    public void RemovePropertyChanged ( )
    {
        var obj = new PropertyChangedEventObject {StringValue = "Hello",};
        var left = "";

        var b = Binder.Default.Bind ( ( ) => left == obj.StringValue );

        Assert.Equal ( 1, obj.StringValueChangedCount );

        Assert.Equal ( obj.StringValue, left );

        obj.StringValue = "Goodbye";

        Assert.Equal ( obj.StringValue, left );

        b.Dispose ( );

        Assert.Equal ( 0, obj.StringValueChangedCount );

        obj.StringValue = "Hello Again";

        Assert.Equal ( "Goodbye", left );
    }

    class NotifyPropertyChangedEventObject : INotifyPropertyChanged
    {
        public int PropertyChangedCount { get; private set; }

        PropertyChangedEventHandler propertyChanged;

        public event PropertyChangedEventHandler PropertyChanged
        {
            add
            {
                propertyChanged += value;
                PropertyChangedCount++;
            }
            remove
            {
                propertyChanged -= value;
                PropertyChangedCount--;
            }
        }

        string stringValue = "";

        public string StringValue
        {
            get { return stringValue; }
            set
            {
                if ( stringValue != value )
                {
                    stringValue = value;
                    if ( propertyChanged != null )
                    {
                        propertyChanged ( this, new PropertyChangedEventArgs ( "StringValue" ) );
                    }
                }
            }
        }

        int intValue = 0;

        public int IntValue
        {
            get { return intValue; }
            set
            {
                if ( intValue != value )
                {
                    intValue = value;
                    if ( propertyChanged != null )
                    {
                        propertyChanged ( this, new PropertyChangedEventArgs ( "IntValue" ) );
                    }
                }
            }
        }
    }

    [ Fact ]
    public void NotifyPropertyChanged ( )
    {
        var obj = new NotifyPropertyChangedEventObject {StringValue = "Hello",};
        var left = "";

        Binder.Default.Bind ( ( ) => left == obj.StringValue );

        Assert.Equal ( 1, obj.PropertyChangedCount );

        Assert.Equal ( obj.StringValue, left );

        obj.StringValue = "Goodbye";

        Assert.Equal ( "Goodbye", left );
    }

    [ Fact ]
    public void RemoveNotifyPropertyChanged ( )
    {
        var obj = new NotifyPropertyChangedEventObject {StringValue = "Hello",};
        var left = "";

        var b = Binder.Default.Bind ( ( ) => left == obj.StringValue );

        Assert.Equal ( 1, obj.PropertyChangedCount );

        Assert.Equal ( obj.StringValue, left );

        obj.StringValue = "Goodbye";

        Assert.Equal ( "Goodbye", left );

        b.Dispose ( );

        Assert.Equal ( 0, obj.PropertyChangedCount );

        obj.StringValue = "Hello Again";

        Assert.Equal ( "Goodbye", left );
    }

    class NotEventHandlerObject
    {
        string stringValue = "";

        public int StringValueChangedCount { get; private set; }

        Action<string, string> stringValueChanged;

        public event Action<string, string> StringValueChanged
        {
            add
            {
                stringValueChanged += value;
                StringValueChangedCount++;
            }
            remove
            {
                stringValueChanged -= value;
                StringValueChangedCount--;
            }
        }

        public string StringValue
        {
            get { return stringValue; }
            set
            {
                if ( stringValue != value )
                {
                    var oldValue = stringValue;
                    stringValue = value;
                    if ( stringValueChanged != null )
                    {
                        stringValueChanged ( oldValue, stringValue );
                    }
                }
            }
        }
    }

    [ Fact ]
    public void NotifyNotEventHandler ( )
    {
        var obj = new NotEventHandlerObject {StringValue = "Hello",};
        var left = "";

        var b = Binder.Default.Bind ( ( ) => left == obj.StringValue );

        Assert.Equal ( 1, obj.StringValueChangedCount );

        Assert.Equal ( obj.StringValue, left );

        obj.StringValue = "Goodbye";

        Assert.Equal ( "Goodbye", left );

        b.Dispose ( );

        Assert.Equal ( 0, obj.StringValueChangedCount );

        obj.StringValue = "Hello Again";

        Assert.Equal ( "Goodbye", left );
    }

    class PlainObject
    {
        public string string_property { get; set; }
    }

    [ Fact ]
    public void TriggerInvalidateMember ( )
    {
        var obj = new PlainObject {string_property = "Hello",};
        var left = "";

        var b = Binder.Default.Bind ( ( ) => left == obj.string_property );

        Assert.Equal ( obj.string_property, left );

        obj.string_property = "Goodbye";

        Binder.Default.Invalidate ( ( ) => obj.string_property );

        Assert.Equal ( "Goodbye", left );

        b.Dispose ( );

        obj.string_property = "Hello Again";

        Assert.Equal ( "Goodbye", left );
    }
}