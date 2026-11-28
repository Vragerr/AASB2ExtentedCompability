using System;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class HorizonSurfaceRegion
    {
        internal static CellRect SourceView(CellRect view, int offsetX, int offsetZ) =>
            new CellRect(view.minX - offsetX, view.minZ - offsetZ, view.Width, view.Height);

        internal static void MoveCurtainToRingEdge(CellRect band, int ring, ref float bottom, ref float top)
        {
            if (top <= band.minZ)
                top = Math.Max(bottom, band.minZ - ring);
            else if (bottom >= band.maxZ + 1)
                bottom = Math.Min(top, band.maxZ + 1 + ring);
        }

        internal static bool TryEdgeSample(CellRect bounds, IntVec3 outside, out IntVec3 sample)
        {
            sample = IntVec3.Invalid;
            if (bounds.Width < 1 || bounds.Height < 1 || bounds.Contains(outside) ||
                !bounds.ExpandedBy(2).Contains(outside))
                return false;
            sample = new IntVec3(Math.Clamp(outside.x, bounds.minX, bounds.maxX), 0,
                Math.Clamp(outside.z, bounds.minZ, bounds.maxZ));
            return true;
        }

        internal static Vector2[] DrawArea(CellRect surface, int width)
        {
            var outer = surface.ExpandedBy(width);
            return new[]
            {
                new Vector2(outer.minX, outer.minZ),
                new Vector2(outer.maxX + 1, outer.minZ),
                new Vector2(outer.maxX + 1, outer.maxZ + 1),
                new Vector2(outer.minX, outer.maxZ + 1)
            };
        }
    }
}
