namespace Epoxide;

public interface IListWithRangeSupport < T > : IList < T >
{
    /// <summary>
    /// Adds the elements of the specified collection to the end of the <see cref="IListWithRangeSupport{T}" />.
    /// </summary>
	/// <param name="collection">
    /// The collection whose elements should be added to the end of the <see cref="IListWithRangeSupport{T}" />.
    /// The collection itself cannot be <see langword="null" />, but it can contain elements that are <see langword="null" />,
    /// if type <paramref name="T" /> is a reference type.
    /// </param>
	/// <exception cref="ArgumentNullException"><paramref name="collection" /> is <see langword="null" />.</exception>
	void AddRange ( IEnumerable < T > collection );

    /// <summary>
    /// Inserts the elements of a collection into the <see cref="IListWithRangeSupport{T}" /> at the specified index.
    /// </summary>
	/// <param name="index">The zero-based index at which the new elements should be inserted.</param>
	/// <param name="collection">
    /// The collection whose elements should be inserted into the <see cref="IListWithRangeSupport{T}" />.
    /// The collection itself cannot be <see langword="null" />, but it can contain elements that are <see langword="null" />,
    /// if type <paramref name="T" /> is a reference type.
    /// </param>
	/// <exception cref="ArgumentNullException"><paramref name="collection" /> is <see langword="null" />.</exception>
	/// <exception cref="ArgumentOutOfRangeException">
	/// <paramref name="index" /> is less than 0.  
	/// -or-  
	/// <paramref name="index" /> is greater than <see cref="ICollection{T}.Count" />.
    /// </exception>
	void InsertRange ( int index, IEnumerable < T > collection );

    /// <summary>
    /// Removes a range of elements from the <see cref="IListWithRangeSupport{T}" />.
    /// </summary>
	/// <param name="index">The zero-based starting index of the range of elements to remove.</param>
	/// <param name="count">The number of elements to remove.</param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// <paramref name="index" /> is less than 0.  
	/// -or-  
	/// <paramref name="count" /> is less than 0.
    /// </exception>
	/// <exception cref="ArgumentException">
    /// <paramref name="index" /> and <paramref name="count" /> do not denote a valid range of elements
    /// in the <see cref="IListWithRangeSupport{T}" />.
    /// </exception>
	void RemoveRange ( int index, int count );
}