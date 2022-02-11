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
    public void Magic ( )
    {
        var right = "";

        DefaultBinder.Bind ( ( ) => Method ( ).ToString ( ) == right );

        Assert.Equal ( right, "33" );

        DefaultBinder.Bind ( ( ) => Method ( ).GetType (  ).Name.ToString ( ) == right );

        Assert.Equal ( right, "Int32" );
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
}