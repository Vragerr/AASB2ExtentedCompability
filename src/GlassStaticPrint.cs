using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class GlassStaticPrint
    {
        internal readonly struct Vertex
        {
            internal readonly Vector3 Position, Uv;
            internal readonly Color32 Color;

            internal Vertex(Vector3 position, Vector3 uv, Color32 color)
            {
                Position = position;
                Uv = uv;
                Color = color;
            }

            internal static Vertex Lerp(Vertex a, Vertex b, float t) => new Vertex(
                Vector3.LerpUnclamped(a.Position, b.Position, t),
                Vector3.LerpUnclamped(a.Uv, b.Uv, t), Color32.Lerp(a.Color, b.Color, t));
        }

        internal static void ClipTriangle(LayerSubMesh mesh, Vertex a, Vertex b, Vertex c,
            Dictionary<IntVec3, IntVec3> windows, List<Vertex> input, List<Vertex> output, float altitude)
        {
            int minX = Mathf.FloorToInt(Mathf.Min(a.Position.x, Mathf.Min(b.Position.x, c.Position.x)));
            int minZ = Mathf.FloorToInt(Mathf.Min(a.Position.z, Mathf.Min(b.Position.z, c.Position.z)));
            int maxX = Mathf.CeilToInt(Mathf.Max(a.Position.x, Mathf.Max(b.Position.x, c.Position.x)));
            int maxZ = Mathf.CeilToInt(Mathf.Max(a.Position.z, Mathf.Max(b.Position.z, c.Position.z)));
            for (int z = minZ; z < maxZ; z++)
                for (int x = minX; x < maxX; x++)
                {
                    var lower = new IntVec3(x, 0, z);
                    if (!windows.TryGetValue(lower, out var upper))
                        continue;
                    input.Clear();
                    input.Add(a);
                    input.Add(b);
                    input.Add(c);
                    Clip(input, output, x, true, true);
                    Clip(output, input, x + 1, true, false);
                    Clip(input, output, z, false, true);
                    Clip(output, input, z + 1, false, false);
                    if (input.Count < 3)
                        continue;
                    int start = mesh.verts.Count;
                    foreach (var vertex in input)
                    {
                        var p = vertex.Position;
                        p.x += upper.x - x;
                        p.z += upper.z - z;
                        p.y = altitude + Mathf.Clamp(p.y * 0.005f, 0f, 0.003f);
                        mesh.verts.Add(p);
                        mesh.uvs.Add(vertex.Uv);
                        mesh.colors.Add(vertex.Color);
                    }
                    for (int i = 1; i + 1 < input.Count; i++)
                    {
                        mesh.tris.Add(start);
                        mesh.tris.Add(start + i);
                        mesh.tris.Add(start + i + 1);
                    }
                }
        }

        private static void Clip(List<Vertex> input, List<Vertex> output, float edge, bool xAxis, bool minimum)
        {
            output.Clear();
            if (input.Count == 0)
                return;
            var previous = input[input.Count - 1];
            float previousDistance = Distance(previous);
            foreach (var current in input)
            {
                float distance = Distance(current);
                if ((previousDistance >= 0f) != (distance >= 0f))
                    output.Add(Vertex.Lerp(previous, current, previousDistance / (previousDistance - distance)));
                if (distance >= 0f)
                    output.Add(current);
                previous = current;
                previousDistance = distance;
            }

            float Distance(Vertex vertex)
            {
                float offset = (xAxis ? vertex.Position.x : vertex.Position.z) - edge;
                return minimum ? offset : -offset;
            }
        }
    }
}
