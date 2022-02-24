using System.Collections.ObjectModel;
using System.ComponentModel;

using Epoxide;

using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

// TODO: Add view model for IPackageSearchMetadata
public class ViewModel : BindableObject
{
    public ViewModel ( )
    {
        // TODO: Add support for tasks without .Result
        Epoxide.Binder.Default.Bind ( this, vm =>
            vm.Results == SearchAsync ( vm.Query, CancellationToken.None ).Result
        );
    }

    private class Property : PropertyChangedEventArgsFactory
    {
        public static PropertyChangedEventArgs Query { get; } = Create ( );
    }

    private string query = string.Empty;
    public  string Query
    {
        get => query;
        set => Set ( ref query, value ?? string.Empty, Property.Query );
    }

    // TODO: Use own collection
    public IReadOnlyCollection < IPackageSearchMetadata > Results { get; } = new ObservableCollection < IPackageSearchMetadata > ( );

    private static async Task < IEnumerable < IPackageSearchMetadata > > SearchAsync ( string? query, CancellationToken cancellationToken )
    {
        if ( string.IsNullOrWhiteSpace ( query ) )
            return Enumerable.Empty < IPackageSearchMetadata > ( );

        var repository   = Repository.Factory.GetCoreV3 ( "https://api.nuget.org/v3/index.json" );
        var resource     = await repository.GetResourceAsync < PackageSearchResource > ( );
        var searchFilter = new SearchFilter ( includePrerelease: true );

        return await resource.SearchAsync ( query,
                                            searchFilter,
                                            skip: 0,
                                            take: 20,
                                            NullLogger.Instance,
                                            cancellationToken );
    }
}