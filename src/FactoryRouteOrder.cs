using System;
using System.Collections.Generic;

namespace AASB2ExtendedCompatibility
{
    internal enum FactoryRoutingMode { Fixed, Priority, RoundRobin, FirstAvailable }

    internal static class FactoryRouteOrder
    {
        internal static TOutput Select<TEntry, TOutput, TState>(FactoryRoutingMode mode, List<TEntry> configured,
            int lastOutput, TState state, Func<TEntry, int> id, Func<TState, TEntry, TOutput> eligible,
            IEnumerable<TOutput> stableOutputs, Func<TState, TOutput, bool> available)
            where TOutput : class
        {
            if (mode == FactoryRoutingMode.FirstAvailable)
            {
                foreach (var output in stableOutputs)
                    if (available(state, output))
                        return output;
                return null;
            }

            int limit = mode == FactoryRoutingMode.Fixed ? Math.Min(1, configured.Count) : configured.Count;
            int start = 0;
            if (mode == FactoryRoutingMode.RoundRobin)
                for (int index = 0; index < limit; index++)
                    if (id(configured[index]) == lastOutput)
                    {
                        start = index + 1;
                        break;
                    }
            for (int offset = 0; offset < limit; offset++)
            {
                var output = eligible(state, configured[(start + offset) % limit]);
                if (output != null)
                    return output;
            }
            return null;
        }
    }
}
