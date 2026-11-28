using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal enum GlassCoverage { Outside, Partial, Covered }

    internal static class GlassFootprint
    {
        internal static GlassCoverage Classify(Dictionary<IntVec3, IntVec3> windows, CellRect bounds,
            float x0, float z0, float x1, float z1, out IntVec3 offset)
        {
            offset = IntVec3.Invalid;
            if (!float.IsFinite(x0) || !float.IsFinite(z0) || !float.IsFinite(x1) || !float.IsFinite(z1))
                return GlassCoverage.Partial;
            int left = Mathf.FloorToInt(x0), bottom = Mathf.FloorToInt(z0);
            int right = Mathf.CeilToInt(x1) - 1, top = Mathf.CeilToInt(z1) - 1;
            if (right < bounds.minX || left > bounds.maxX || top < bounds.minZ || bottom > bounds.maxZ)
                return GlassCoverage.Outside;
            bool complete = left >= bounds.minX && right <= bounds.maxX && bottom >= bounds.minZ && top <= bounds.maxZ;
            for (int z = Math.Max(bottom, bounds.minZ); z <= Math.Min(top, bounds.maxZ); z++)
                for (int x = Math.Max(left, bounds.minX); x <= Math.Min(right, bounds.maxX); x++)
                {
                    if (!windows.TryGetValue(new IntVec3(x, 0, z), out var upper))
                    {
                        complete = false;
                        continue;
                    }
                    var translation = new IntVec3(upper.x - x, 0, upper.z - z);
                    if (offset != IntVec3.Invalid && offset != translation)
                        complete = false;
                    offset = translation;
                    if (!complete)
                        return GlassCoverage.Partial;
                }
            return offset == IntVec3.Invalid ? GlassCoverage.Outside : GlassCoverage.Covered;
        }

        // Keep fine native height differences within the interval before upper objects start.
        internal const float DepthScale = 0.016f;
        internal const float GlassTintOffset = 0.300f;
    }
}
