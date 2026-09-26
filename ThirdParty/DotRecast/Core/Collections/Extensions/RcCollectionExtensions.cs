// BACKPORTED for net472/C# 7.3 — see ThirdParty/DotRecast/NOTICE.md
// Reduced copy of DotRecast.Core/Collections/Extensions/RcCollectionExtensions.cs.
// Only InsertSort is referenced by the vendored subset; the (unused) ForEach/Shuffle/AddRange
// members were dropped to avoid vendoring RcImmutableArray and System.Memory-based Span overloads.
using System;

namespace DotRecast.Core.Collections.Extensions
{
    public static class RcCollectionExtensions
    {
        /// Sorts the given data in-place using insertion sort.
        ///
        /// @param	data		The data to sort
        /// @param	dataLength	The number of elements in @p data
        public static void InsertSort(this int[] data)
        {
            for (int valueIndex = 1; valueIndex < data.Length; valueIndex++)
            {
                int value = data[valueIndex];
                int insertionIndex;
                for (insertionIndex = valueIndex - 1; insertionIndex >= 0 && data[insertionIndex] > value; insertionIndex--)
                {
                    // Shift over values
                    data[insertionIndex + 1] = data[insertionIndex];
                }

                // Insert the value in sorted order.
                data[insertionIndex + 1] = value;
            }
        }
    }
}
