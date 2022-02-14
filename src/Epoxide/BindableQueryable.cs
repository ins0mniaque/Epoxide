using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq.Expressions;

using Epoxide.Linq;

namespace Epoxide;

public static class BindableEnumerable
{
    public static IQueryable<TElement> AsBindable<TElement>(this IEnumerable<TElement> source)
    {
        if (source == null)
            throw Error.ArgumentNull(nameof(source));

        if(source is IQueryable<TElement> queryable && queryable.Provider is BindableQuery)
            return queryable;

        return new BindableQuery<TElement>(source);
    }

    // public static IQueryable AsBindable(this IEnumerable source)
    // {
    //     if (source == null)
    //         throw Error.ArgumentNull(nameof(source));
    //
    //     if (source is IQueryable queryable && queryable.Provider is BindableQuery)
    //         return queryable;
    //
    //     Type? enumType = TypeHelper.FindGenericType(typeof(IEnumerable<>), source.GetType());
    //     if (enumType == null)
    //         throw Error.ArgumentNotIEnumerableGeneric(nameof(source));
    //
    //     return BindableQuery.Create(enumType.GenericTypeArguments[0], source);
    // }

    public static TCollection ToList <TCollection, TElement>(this IEnumerable<TElement> source)
        where TCollection : ICollection<TElement>, new ( )
    {
        var output = new TCollection ( );
        foreach ( var item in source )
            output.Add ( item );

        return output;
    }
}

public static class BindableQueryable
{
    public static IReadOnlyList<TElement> ToList <TElement>(this IQueryable<TElement> source)
    {
        return ToList<ObservableCollection<TElement>, TElement>(source);
    }

    public static TCollection ToList <TCollection, TElement>(this IQueryable<TElement> source)
        where TCollection : ICollection<TElement>, new ( )
    {
        var output = source.AsEnumerable ( ).ToList <TCollection, TElement> ( );

        if ( source is BindableQuery<TElement> bindable )
        {
            var root = bindable.Expression;
            while ( root is MethodCallExpression m )
                root = m.Object ?? m.Arguments [ 0 ];

            if ( root is ConstantExpression c && c.Value is BindableQuery q && q.Enumerable is INotifyCollectionChanged ncc )
            {
                ncc.CollectionChanged += (o, e) =>
                {
                    output.Clear ( );
                    foreach ( var item in source )
                        output.Add ( item );
                };
            }
        }

        return output;
    }
}