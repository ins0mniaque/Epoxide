using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq.Expressions;

namespace Epoxide;

public static class BindableEnumerable
{
    public static IQueryable<TElement> AsBindable<TElement>(this IEnumerable<TElement> source)
    {
        var q = source.AsQueryable (  );
        var provider = new BindableQueryProvider(source, q.Provider);
        return provider.CreateQuery<TElement>(q.Expression);
    }

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

        if ( source is BindableQuery<TElement> magic )
        {
            if (( (BindableQueryProvider) magic.Provider ).Source is INotifyCollectionChanged ncc )
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

public class BindableQueryProvider : IQueryProvider
{
    private readonly IQueryProvider provider;
    private readonly IEnumerable source;

    public BindableQueryProvider(IEnumerable source, IQueryProvider provider)
    {
        this.provider = provider;
        this.source = source;
    }

    public IEnumerable Source => source;

    public IEnumerator<TElement> ExecuteQuery<TElement>(Expression expression)
    {
        IQueryable<TElement> query = provider.CreateQuery<TElement>(expression);
        IEnumerator<TElement> enumerator = query.GetEnumerator();
        return enumerator;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        IQueryable<TElement> queryable = provider.CreateQuery<TElement>(expression);
        return new BindableQuery<TElement>(queryable, this);
    }

    public IQueryable CreateQuery(Expression expression)
    {
        IQueryable queryable = provider.CreateQuery(expression);
        Type elementType = queryable.ElementType;
        Type queryType = typeof(BindableQuery<>).MakeGenericType(elementType);
        return (IQueryable)Activator.CreateInstance(queryType, queryable, this);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        return provider.Execute<TResult>(expression);
    }

    public object Execute(Expression expression)
    {
        return provider.Execute(expression);
    }
}

public class BindableQuery<TElement> : IQueryable<TElement>
{
    private readonly IQueryable queryable;
    private readonly BindableQueryProvider provider;

    public BindableQuery(IQueryable queryable, BindableQueryProvider provider)
    {
        this.queryable = queryable;
        this.provider = provider;
    }

    public IEnumerator<TElement> GetEnumerator()
    {
        Expression expression = queryable.Expression;
        return provider.ExecuteQuery<TElement>(expression);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public Type ElementType
    {
        get { return typeof(TElement); }
    }

    public Expression Expression
    {
        get { return queryable.Expression; }
    }

    public IQueryProvider Provider
    {
        get { return provider; }
    }
}