using System.Collections.Generic;

namespace AASB2ExtendedCompatibility
{
    internal sealed class GlassOpeningIndex
    {
        private readonly Dictionary<int, int> lowerByUpper = new();
        private readonly Dictionary<int, int> upperByLower = new();

        internal bool Empty => lowerByUpper.Count == 0;

        internal bool TryLower(int upper, out int lower) => lowerByUpper.TryGetValue(upper, out lower);
        internal bool TryUpper(int lower, out int upper) => upperByLower.TryGetValue(lower, out upper);

        internal void Set(int upper, int lower)
        {
            lowerByUpper[upper] = lower;
            upperByLower[lower] = upper;
        }

        internal void Remove(int upper)
        {
            if (lowerByUpper.Remove(upper, out int lower))
                upperByLower.Remove(lower);
        }

        internal void Clear()
        {
            lowerByUpper.Clear();
            upperByLower.Clear();
        }
    }
}
