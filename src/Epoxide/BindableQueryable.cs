using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq.Expressions;

using Epoxide.Linq;

namespace Epoxide;

public static class BindableEnumerable
{
    // TODO: Add configurable IBinder method
    public static IBindableQueryable<TElement> AsBindable<TElement>(this IEnumerable<TElement> source)
    {
        if (source == null)
            throw Error.ArgumentNull(nameof(source));

        if(source is IBindableQueryable<TElement> queryable)
            return queryable;

        return new BindableQuery<TElement>(Binder.Default, source);
    }

    // public static IBindableQueryable AsBindable(this IEnumerable source)
    // {
    //     if (source == null)
    //         throw Error.ArgumentNull(nameof(source));
    //
    //     if (source is IBindableQueryable queryable)
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

public static class BindableQueryable
{
    public static IBindableQueryable<TElement> Invalidates<TElement>(this IBindableQueryable<TElement> source, Expression expression)
    {
        if (source == null)
            throw Error.ArgumentNull(nameof(source));

        source.Executed += Source_Executed;

        return source;

        void Source_Executed ( object sender, BindableQueryExecutedEventArgs e )
        {
            var source = ( (BindableQuery) sender );

            source.Executed -= Source_Executed;

            if ( source.Enumerable is INotifyCollectionChanged ncc )
            {
                // TODO: Dispose properly in binder
                ncc.CollectionChanged += (o, e) => source.Binder.Invalidate ( expression );
            }
        }
    }

    public static IReadOnlyList<TElement> ToList <TElement>(this IQueryable<TElement> source)
    {
        return source.ToList<ObservableCollection<TElement>, TElement>();
    }
}