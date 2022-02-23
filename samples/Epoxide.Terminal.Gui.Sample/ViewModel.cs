using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

using Epoxide;

// TODO: Implement INotifyPropertyChanged
public class ViewModel
{
    public ViewModel ( )
    {
        // TODO: Add support for tasks without .Result
        // Epoxide.Binder.Default.Bind ( this, vm =>
        //     vm.Results == SearchAsync ( vm.Query, CancellationToken.None )
        // );
    }

    public string Query { get; set; } = string.Empty;

    public IReadOnlyCollection < IPackageSearchMetadata > Results { get; private set; }

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