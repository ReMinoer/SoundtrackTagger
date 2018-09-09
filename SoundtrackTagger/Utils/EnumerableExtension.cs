using System;
using System.Collections.Generic;

namespace SoundtrackTagger.Utils
{
    static public class EnumerableExtension
    {
        static public TSource MaxBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> selector)
            where TKey : IComparable<TKey>
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (selector == null)
                throw new ArgumentNullException(nameof(selector));

            using (IEnumerator<TSource> sourceIterator = source.GetEnumerator())
            {
                if (!sourceIterator.MoveNext())
                    throw new InvalidOperationException("Sequence contains no elements");

                TSource max = sourceIterator.Current;
                TKey maxKey = selector(max);

                while (sourceIterator.MoveNext())
                {
                    TSource candidate = sourceIterator.Current;
                    TKey candidateKey = selector(candidate);
                    if (candidateKey.CompareTo(maxKey) > 0)
                    {
                        max = candidate;
                        maxKey = candidateKey;
                    }
                }

                return max;
            }
        }
    }
}