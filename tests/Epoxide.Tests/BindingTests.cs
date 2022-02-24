using System.ComponentModel;

using Epoxide.Linq;

namespace Epoxide;

public class BindingTests
{
    [ Fact ]
    public void LocalLeftInit ( )
    {
        var left = "";
        var right = "hello";

        Binder.Default.Bind ( ( ) => left == right );

        Assert.Equal ( left, right );
        Assert.Equal ( left, "hello" );
    }

    [ Fact ]
    public void LocalRightInit ( )
    {
        var left = "hello";
        var right = "";

        Binder.Default.Bind ( ( ) => left == right );

        Assert.Equal ( left, right );
        Assert.Equal ( left, "" );
    }

    class TestObject
    {
        public int State { get; set; }
    }

    [ Fact ]
    public void LocalLeftObjectInit ( )
    {
        TestObject left = null;
        TestObject right = new TestObject ( );

        Binder.Default.Bind ( ( ) => left == right );

        Assert.Equivalent ( left, right );
        Assert.NotNull ( left );
    }

    [ Fact ]
    public void LocalRightObjectInit ( )
    {
        TestObject left = new TestObject ( );
        TestObject right = null;

        Binder.Default.Bind ( ( ) => left == right );

        Assert.Equivalent ( left, right );
        Assert.Null ( left );
    }

    [ Fact ]
    public void LocalAndPropInit ( )
    {
        var left = 69;
        TestObject right = new TestObject {State = 42,};

        Binder.Default.Bind ( ( ) => left == right.State );

        Assert.Equal ( left, right.State );
        Assert.Equal ( left, 42 );
    }

    [ Fact ]
    public void PropAndLocalInit ( )
    {
        TestObject left = new TestObject {State = 42,};
        var right = 1001;

        Binder.Default.Bind ( ( ) => left.State == right );

        Assert.Equal ( left.State, right );
        Assert.Equal ( left.State, 1001 );
    }

    static int Method ( )
    {
        return 33;
    }

    [ Fact ]
    public void LocalAndMethodInit ( )
    {
        var left = 0;

        Binder.Default.Bind ( ( ) => left == Method ( ) );

        Assert.Equal ( left, 33 );
    }

    [ Fact ]
    public void MethodAndLocalInit ( )
    {
        var right = 42;

        Binder.Default.Bind ( ( ) => Method ( ) == right );

        Assert.Equal ( right, 33 );
    }

    [ Fact ]
    public void AutoConvert ( )
    {
        var right = 0.0;

        Binder.Default.Bind ( ( ) => Method ( ) == right );

        Assert.Equal ( 33.0, right );
    }

    [ Fact ]
    public void Null ( )
    {
        var left  = (object?) null;
        var right = "value";

        Binder.Default.Bind ( ( ) => ( left.ToString ( ) ?? null ) == right );

        Assert.Equal ( right, null );

        right = "value";

        Binder.Default.Bind ( ( ) => left.ToString ( ) == right );

        Assert.Equal ( right, "value" );
    }

    [ Fact ]
    public void Nullable ( )
    {
        var left  = (TestObject?) null;
        var right = (int?) -1;

        Binder.Default.Bind ( ( ) => ( (int?) left.ToString ( ).Length ?? null ) == right );

        Assert.Equal ( right, null );

        right = -1;

        Binder.Default.Bind ( ( ) => left.State                     == right );
        Binder.Default.Bind ( ( ) => left.ToString ( ).Length       == right );
        Binder.Default.Bind ( ( ) => left.State.ToString ( ).Length == right );

        Assert.Equal ( right, -1 );
    }

    [ Fact ]
    public void ValueType ( )
    {
        var left   = (TestObject?) null;
        var right  = (int) -1;

        Binder.Default.Bind ( ( ) => ( (int?) left.ToString ( ).Length ?? 42 ) == right );

        Assert.Equal ( right, 42 );

        var exception = Assert.Throws < BindingException > ( ( ) => Binder.Default.Bind ( ( ) => ( (int?) left.ToString ( ).Length ?? null ) == right ) );

        Assert.IsType < InvalidCastException > ( exception.InnerException );

        Assert.Equal ( right, 42 );

        right = -1;

        Binder.Default.Bind ( ( ) => left.State                     == right );
        Binder.Default.Bind ( ( ) => left.ToString ( ).Length       == right );
        Binder.Default.Bind ( ( ) => left.State.ToString ( ).Length == right );

        Assert.Equal ( right, -1 );
    }

    public class Implicit
    {
        public Implicit ( string value )
        {
            Value = value;
        }

        public string Value { get; }

        public class From : Implicit
        {
            public From ( string value ) : base ( value ) { }

            public static implicit operator From ( string value ) => new From ( value );
        }

        public class To : Implicit
        {
            public To ( string value ) : base ( value ) { }

            public static implicit operator string ( To to ) => to.Value;
        }

        public class Both : Implicit
        {
            public Both ( string value ) : base ( value ) { }

            public static implicit operator Both   ( string value ) => new Both ( value );
            public static implicit operator string ( Both   both  ) => both.Value;
        }
    }

    [ Fact ]
    public void ImplicitCast ( )
    {
        var left   = (Implicit.Both?) null;
        var right  = "magic";

        Binder.Default.Bind ( ( ) => left == right );

        Assert.NotNull ( left );
        Assert.Equal   ( "magic", left.Value );
    }

    [ Fact ]
    public void ImplicitCastFrom ( )
    {
        var left   = (Implicit.From?) null;
        var right  = "magic";

        Binder.Default.Bind ( ( ) => left == (Implicit.From?) right );

        Assert.NotNull ( left );
        Assert.Equal   ( "magic", left.Value );
    }

    [ Fact ]
    public void ImplicitCastTo ( )
    {
        var left   = new Implicit.To ( "magic" );
        var right  = (string?) null;

        Binder.Default.Bind ( ( ) => left == right );

        Assert.Equal ( "magic", right );
    }

    [ Fact ]
    public void Aggregate ( )
    {
        var left  = -1;
        var right = new System.Collections.ObjectModel.ObservableCollection<int> ();

        Binder.Default.Bind ( ( ) => left == right.Where ( i => i != 42 ).Select ( i => i + 50 ).Sum ( ) );

        Assert.Equal ( 0, left );

        right.Add ( 42 );

        Assert.Equal ( 0, left );

        right.Add ( 10 );

        Assert.Equal ( 60, left );

        right.Add ( 20 );

        Assert.Equal ( 130, left );
    }

    [ Fact ]
    public void Collection ( )
    {
        var left  = (IReadOnlyCollection<string>?) null;
        var right = new System.Collections.ObjectModel.ObservableCollection<int> ();

        Binder.Default.Bind ( ( ) => left == right.Where ( i => i != 0 ).Select ( i => i.ToString ( ) ).ToList ( ) );

        Assert.NotNull ( left );
        Assert.Empty   ( left );

        right.Add ( 42 );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right.Add ( 0 );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );
    }

    class NotifyTestObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        int state;

        public int State
        {
            get { return state; }
            set
            {
                if ( state != value )
                {
                    state = value;
                    PropertyChanged?.Invoke ( this, new PropertyChangedEventArgs ( nameof ( State ) ) );
                }
            }
        }
    }

    [ Fact ]
    public void CollectionOfObservable ( )
    {
        var left  = (IReadOnlyCollection<string>?) null;
        var right = new System.Collections.ObjectModel.ObservableCollection<NotifyTestObject> ();

        Binder.Default.Bind ( ( ) => left == right.Where ( i => i.State != 0 ).Select ( i => i.State.ToString ( ) ) );

        Assert.NotNull ( left );
        Assert.Empty   ( left );

        right.Add ( new NotifyTestObject { State = 42 } );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right.Add ( new NotifyTestObject { State = 0 } );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right [ 0 ].State = 0;

        Assert.Empty ( left );

        right [ 1 ].State = 33;

        Assert.Single ( left );
        Assert.Equal  ( "33", left.First ( ) );
    }

    [ Fact ]
    public void ReadOnlyCollectionProperty ( )
    {
        var collection = new System.Collections.ObjectModel.ObservableCollection<string> ();

        var left  = new { Collection = collection };
        var right = new System.Collections.ObjectModel.ObservableCollection<TestObject> ();

        Binder.Default.Bind ( ( ) => left.Collection == right.Where ( i => i.State != 0 ).Select ( i => i.State.ToString ( ) ) );

        Assert.NotNull ( left.Collection );
        Assert.Empty   ( left.Collection );

        right.Add ( new TestObject { State = 42 } );

        Assert.Single ( left.Collection );
        Assert.Equal  ( "42", left.Collection.First ( ) );

        right.Add ( new TestObject { State = 0 } );

        Assert.Single ( left.Collection );
        Assert.Equal  ( "42", left.Collection.First ( ) );

        Assert.Same ( collection, left.Collection );

        collection = new System.Collections.ObjectModel.ObservableCollection<string> ();

        left = new { Collection = collection };

        Binder.Default.Invalidate ( ( ) => right );

        Assert.Single  ( left.Collection );
        Assert.Equal   ( "42", left.Collection.First ( ) );

        Assert.Same ( collection, left.Collection );
    }

    [ Fact ]
    public void ReverseReadOnlyCollectionProperty ( )
    {
        var collection = new System.Collections.ObjectModel.ObservableCollection<string> ();

        var left  = new { Collection = collection };
        var right = new System.Collections.ObjectModel.ObservableCollection<TestObject> ();

        Binder.Default.Bind ( ( ) => right.Where ( i => i.State != 0 ).Select ( i => i.State.ToString ( ) ) == left.Collection );

        Assert.NotNull ( left.Collection );
        Assert.Empty   ( left.Collection );

        right.Add ( new TestObject { State = 42 } );

        Assert.Single ( left.Collection );
        Assert.Equal  ( "42", left.Collection.First ( ) );

        right.Add ( new TestObject { State = 0 } );

        Assert.Single ( left.Collection );
        Assert.Equal  ( "42", left.Collection.First ( ) );

        Assert.Same ( collection, left.Collection );

        collection = new System.Collections.ObjectModel.ObservableCollection<string> ();

        left = new { Collection = collection };

        Binder.Default.Invalidate ( ( ) => right );

        Assert.Single  ( left.Collection );
        Assert.Equal   ( "42", left.Collection.First ( ) );

        Assert.Same ( collection, left.Collection );
    }

    private class CustomObservableCollection < T > : System.Collections.ObjectModel.ObservableCollection<T>
    {

    }

    [ Fact ]
    public void CustomCollection ( )
    {
        var left  = (CustomObservableCollection<string>?) null;
        var right = new System.Collections.ObjectModel.ObservableCollection<int> ();

        Binder.Default.Bind ( ( ) => left == right.Where ( i => i != 0 ).Select ( i => i.ToString ( ) ) );

        Assert.NotNull ( left );
        Assert.Empty   ( left );

        right.Add ( 42 );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right.Add ( 0 );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );
    }

    [ Fact ]
    public void Configure ( )
    {
        var left  = (IReadOnlyCollection<string>?) null;
        var right = new System.Collections.ObjectModel.ObservableCollection<NotifyTestObject> ();

        Binder.Default.Bind ( ( ) => left == right.Where ( i => i.State != 0 ).Configure ( o => o.ToString ( ) ).Select ( i => i.State.ToString ( ) ) );

        Assert.NotNull ( left );
        Assert.Empty   ( left );

        right.Add ( new NotifyTestObject { State = 42 } );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right.Add ( new NotifyTestObject { State = 0 } );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right [ 0 ].State = 0;

        Assert.Empty ( left );

        right [ 1 ].State = 33;

        Assert.Single ( left );
        Assert.Equal  ( "33", left.First ( ) );
    }

          Task < int > SafeMethodAsync   ( CancellationToken cancellationToken ) => Task.Run ( ( ) => 42 );
    async Task < int > UnsafeMethodAsync ( CancellationToken cancellationToken ) => 42;

    [ Fact ]
    public async Task Await ( )
    {
        using var cancel = new CancellationTokenSource ( );

        var left = "";

        Binder.Default.Bind ( ( ) => left == SafeMethodAsync ( cancel.Token ).Result.ToString ( ).ToString ( ) );

        await Task.Delay ( 250 );

        Assert.Equal ( "42", left );

        left = "";

        Binder.Default.Bind ( ( ) => left == UnsafeMethodAsync ( cancel.Token ).Result.ToString ( ).ToString ( ) );

        Assert.Equal ( "42", left );
    }

    public class Button
    {
        public event EventHandler? Click;

        public void RaiseClick ( )
        {
            Click?.Invoke ( this, EventArgs.Empty );
        }
    }

    [ Fact ]
    public void Event ( )
    {
        var button = new Button ( );
        var left   = "";

        Binder.Default.Bind ( ( ) => button.Event ( nameof ( Button.Click ), ( ) => left == button.ToString ( ) ) );

        Assert.Equal ( "", left );

        button.RaiseClick ( );

        Assert.Equal ( button.ToString ( ), left );

        left = "";

        Binder.Default.Bind ( ( ) => button.Event < EventArgs > ( nameof ( Button.Click ), e => left == button.ToString ( ) ) );

        Assert.Equal ( "", left );

        button.RaiseClick ( );

        Assert.Equal ( button.ToString ( ), left );

        left = "";

        Binder.Default.Bind ( ( ) => button.Clicked ( e => left == button.ToString ( ) ) );

        Assert.Equal ( "", left );

        button.RaiseClick ( );

        Assert.Equal ( button.ToString ( ), left );
    }
}

// TODO: Detect .Event ( ) usage and generate source
//       Generate warning for usage in generator
//       Refactor in analyzer (or in source generator if possible)
public static class CustomBindableEvent
{
    [ BindableEvent ( nameof ( BindingTests.Button.Click ) ) ]
    public static bool Clicked ( this BindingTests.Button button, Func < bool > binding ) => throw new NotImplementedException ( );

    [ BindableEvent ( nameof ( BindingTests.Button.Click ) ) ]
    public static bool Clicked ( this BindingTests.Button button, Func < EventArgs, bool > binding ) => throw new NotImplementedException ( );
}