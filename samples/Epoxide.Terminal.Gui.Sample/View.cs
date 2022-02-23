using System.Data;

using NuGet.Protocol.Core.Types;

using Epoxide;

public class View : Window
{
    public View ( ViewModel model ) : this ( )
    {
        // TODO: Fix implicit cast support
        // Epoxide.Binder.Default.Bind ( model, model =>
        //     QueryField.Text == model.Query &&
        //     Results   .Rows == (object) model.Results.Select ( CreateRow )
        // );
    }

    public TextField QueryField  { get; }
    public DataTable Results     { get; }
    public TableView ResultsView { get; }

    private static DataTable CreateTable ( )
    {
        var table = new DataTable ( );

        table.Columns.Add ( nameof ( IPackageSearchMetadata.Title       ), typeof ( string ) );
        table.Columns.Add ( nameof ( IPackageSearchMetadata.Description ), typeof ( string ) );

        return table;
    }

    private DataRow CreateRow ( IPackageSearchMetadata metadata )
    {
        var row = Results.NewRow ( );

        row [ nameof ( IPackageSearchMetadata.Title )       ] = metadata.Title;
        row [ nameof ( IPackageSearchMetadata.Description ) ] = metadata.Description;

        return row;
    }

    private static void Quit ( )
    {
        if ( MessageBox.Query ( 50, 7, "Quit Sample", "Are you sure you want to quit?", "Yes", "No" ) == 0 )
            Application.Top.Running = false;
    }

    private View ( )
    {
        var menu = new MenuBar ( new [ ]
        {
            new MenuBarItem ( "_File", new [ ]
            {
                new MenuItem ( "_Quit", "", Quit )
            } )
        } );

        var queryLabel = new Label ( "Search for packages: " )
        {
            X = 3, Y = 2
        };

        QueryField = new TextField ( "" )
        {
            X     = Pos.Right ( queryLabel ),
            Y     = Pos.Top   ( queryLabel ),
            Width = Dim.Fill  ( )
        };

        Results = CreateTable ( );

        ResultsView = new TableView ( Results )
        {
            Y      = Pos.Bottom ( queryLabel ),
            Width  = Dim.Fill   ( ),
            Height = Dim.Fill   ( )
        };

        Add ( menu, queryLabel, QueryField, ResultsView );
    }
}