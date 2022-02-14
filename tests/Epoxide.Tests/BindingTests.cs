namespace Epoxide.Tests;

public class BindingTests
{
    [ Fact ]
    public void LocalLeftInit ( )
    {
        var left = "";
        var right = "hello";

        DefaultBinder.Bind ( ( ) => left == right );

        Assert.Equal ( left, right );
        Assert.Equal ( left, "hello" );
    }

    [ Fact ]
    public void LocalRightInit ( )
    {
        var left = "hello";
        var right = "";

        DefaultBinder.Bind ( ( ) => left == right );

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

        DefaultBinder.Bind ( ( ) => left == right );

        Assert.Equivalent ( left, right );
        Assert.NotNull ( left );
    }

    [ Fact ]
    public void LocalRightObjectInit ( )
    {
        TestObject left = new TestObject ( );
        TestObject right = null;

        DefaultBinder.Bind ( ( ) => left == right );

        Assert.Equivalent ( left, right );
        Assert.Null ( left );
    }

    [ Fact ]
    public void LocalAndPropInit ( )
    {
        var left = 69;
        TestObject right = new TestObject {State = 42,};

        DefaultBinder.Bind ( ( ) => left == right.State );

        Assert.Equal ( left, right.State );
        Assert.Equal ( left, 42 );
    }

    [ Fact ]
    public void PropAndLocalInit ( )
    {
        TestObject left = new TestObject {State = 42,};
        var right = 1001;

        DefaultBinder.Bind ( ( ) => left.State == right );

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

        DefaultBinder.Bind ( ( ) => left == Method ( ) );

        Assert.Equal ( left, 33 );
    }

    [ Fact ]
    public void MethodAndLocalInit ( )
    {
        var right = 42;

        DefaultBinder.Bind ( ( ) => Method ( ) == right );

        Assert.Equal ( right, 33 );
    }

    [ Fact ]
    public void AutoConvert ( )
    {
        var right = 0.0;

        DefaultBinder.Bind ( ( ) => Method ( ) == right );

        Assert.Equal ( 33.0, right );
    }

    [ Fact ]
    public void Null ( )
    {
        var left  = (object?) null;
        var right = "value";

        DefaultBinder.Bind ( ( ) => ( left.ToString ( ) ?? null ) == right );

        Assert.Equal ( right, null );

        right = "value";

        DefaultBinder.Bind ( ( ) => left.ToString ( ) == right );

        Assert.Equal ( right, "value" );
    }

    [ Fact ]
    public void Aggregate ( )
    {
        var left  = -1;
        var right = new System.Collections.ObjectModel.ObservableCollection<int> ();

        DefaultBinder.Bind ( ( ) => left == right.Where ( i => i != 42 ).Select ( i => i + 50 ).Sum ( ) );

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

        DefaultBinder.Bind ( ( ) => left == right.Where ( i => i != 0 ).Select ( i => i.ToString ( ) ).ToList ( ) );

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
    public void ReadOnlyCollectionProperty ( )
    {
        var collection = new System.Collections.ObjectModel.ObservableCollection<string> ();

        var left  = new { Collection = collection };
        var right = new System.Collections.ObjectModel.ObservableCollection<TestObject> ();

        DefaultBinder.Bind ( ( ) => left.Collection == right.Where ( i => i.State != 0 ).Select ( i => i.State.ToString ( ) ) );

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
        
        DefaultBinder.Invalidate ( ( ) => right );

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

        DefaultBinder.Bind ( ( ) => left == right.Where ( i => i != 0 ).Select ( i => i.ToString ( ) ) );

        Assert.NotNull ( left );
        Assert.Empty   ( left );

        right.Add ( 42 );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );

        right.Add ( 0 );

        Assert.Single ( left );
        Assert.Equal  ( "42", left.First ( ) );
    }
}