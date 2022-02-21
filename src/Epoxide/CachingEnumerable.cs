using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Epoxide;

public sealed class CachingEnumerable < T > : IEnumerable < T >, IDisposable
{
    private readonly List        < T >  cache;
    private readonly IEnumerable < T >  enumerable;
    private          IEnumerator < T >? enumerator;
    private          bool               enumerated;

    // TODO: Initialize capacity based on IIListProvider < TElement >.GetCount
    public CachingEnumerable ( IEnumerable < T > enumerable )               : this ( new List < T > ( ),          enumerable ) { }
    public CachingEnumerable ( IEnumerable < T > enumerable, int capacity ) : this ( new List < T > ( capacity ), enumerable ) { }

    private CachingEnumerable ( List < T > cache, IEnumerable < T > enumerable )
    {
        this.cache      = cache;
        this.enumerable = enumerable ?? throw new ArgumentNullException ( nameof ( enumerable ) );
    }

    public IEnumerator < T > GetEnumerator ( )
    {
        var index = 0;

        while ( true )
        {
            if ( TryGetItem ( index, out var item ) )
            {
                yield return item;
                index++;
            }
            else
                yield break;
        }
    }

    private bool TryGetItem ( int index, [ MaybeNullWhen ( false ) ] out T item )
    {
        if ( index < cache.Count )
        {
            item = cache [ index ];
            return true;
        }

        lock ( cache )
        {
            if ( index < cache.Count )
            {
                item = cache [ index ];
                return true;
            }

            if ( enumerator == null && ! enumerated )
                enumerator = enumerable.GetEnumerator ( );

            if ( enumerated )
            {
                item = default;
                return false;
            }

            if ( enumerator!.MoveNext ( ) )
            {
                cache.Add ( item = enumerator.Current );
                return true;
            }

            enumerator.Dispose ( );
            enumerator = null;
            enumerated = true;

            item = default;
            return false;
        }
    }

    public void Dispose ( )
    {
        if ( enumerator != null )
        {
            enumerator.Dispose ( );
            enumerator = null;
        }
    }

    IEnumerator IEnumerable.GetEnumerator ( ) => GetEnumerator ( );
}