using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class SectionLayer_GlassView : SectionLayer
    {
        public SectionLayer_GlassView(Section section) : base(section)
        {
            relevantChangeTypes = MapMeshFlagDefOf.Terrain | MapMeshFlagDefOf.Things;
        }

        private static readonly System.Reflection.FieldInfo layerMeshes = AccessTools.Field(typeof(MapDrawLayer), "subMeshes");
        private readonly Dictionary<SectionLayer_Terrain, Dictionary<IntVec3, IntVec3>> terrainWindows = new();
        private readonly Dictionary<IntVec3, IntVec3> windows = new Dictionary<IntVec3, IntVec3>();
        private readonly HashSet<IntVec3> dynamicCells = new HashSet<IntVec3>();
        private readonly HashSet<Thing> dynamicDrawn = new HashSet<Thing>();
        private readonly HashSet<Thing> printed = new HashSet<Thing>();
        private readonly List<GlassStaticPrint.Vertex> clipA = new List<GlassStaticPrint.Vertex>(8);
        private readonly List<GlassStaticPrint.Vertex> clipB = new List<GlassStaticPrint.Vertex>(8);


        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            windows.Clear();
            terrainWindows.Clear();
            dynamicCells.Clear();
            var map = Map;
            var visual = map.GetComponent<GlassVisualMap>();
            if (visual?.Enabled != true || !BandCompatibility.IsBanded(map))
                return;

            // This is a projected floor mesh above the solid terrain. It does not alter TerrainGrid.
            float altitude = AltitudeLayer.Terrain.AltitudeFor() + 0.02f;
            foreach (var upper in section.CellRect)
            {
                if (!BandCompatibility.TryGlass(map, upper, out var lower, out _))
                {
                    visual.Record(upper, null);
                    continue;
                }

                var terrain = map.terrainGrid.TerrainAt(lower);
                var material = map.terrainGrid.GetMaterial(terrain, lower.IsPolluted(map), map.terrainGrid.ColorAt(lower));
                if (material == null)
                {
                    visual.Record(upper, null);
                    continue;
                }
                windows[lower] = upper;
                var sourceLayer = map.mapDrawer.SectionAt(lower)?.GetLayer(typeof(SectionLayer_Terrain)) as SectionLayer_Terrain;
                var sourceMeshes = sourceLayer == null ? null : layerMeshes?.GetValue(sourceLayer) as List<LayerSubMesh>;
                if (sourceMeshes == null || !sourceMeshes.Exists(mesh => mesh.verts.Count > 0 && !mesh.disabled))
                    AddCell(GetSubMesh(material), upper, lower, altitude);
                else
                {
                    if (!terrainWindows.TryGetValue(sourceLayer, out var mask))
                        terrainWindows.Add(sourceLayer, mask = new Dictionary<IntVec3, IntVec3>());
                    mask[lower] = upper;
                }
                visual.Record(upper, terrain.defName + "/" + material.name);
            }
            if (windows.Count != 0)
            {
                CopyTerrain(altitude);
                PrintBelow(map, altitude + 0.004f);
                foreach (var entry in windows)
                {
                    var cell = entry.Value;
                    bool ownDeck = map.terrainGrid.TerrainAt(cell)?.defName == "AASBExt_ExperimentalGlassFloor";
                    var material = ownDeck ? GlassMaterials.Deck : GlassMaterials.Skylight;
                    if (material != null)
                        AddCell(GetSubMesh(material), cell, IntVec3.Zero, altitude + 0.012f);
                }
            }
            FinalizeMesh(MeshParts.All);
        }

        public override void DrawLayer()
        {
            base.DrawLayer();
            if (!Visible || windows.Count == 0 || !GlassDynamic.Ready ||
                Map.GetComponent<GlassVisualMap>()?.Enabled != true ||
                !section.CellRect.Overlaps(Find.CameraDriver.CurrentViewRect))
                return;
            var buffers = Map.GetComponent<GlassDrawBuffers>();
            buffers.Begin(windows, AltitudeLayer.Terrain.AltitudeFor() + 0.024f);
            dynamicDrawn.Clear();
            foreach (var cell in dynamicCells)
            {
                if (Map.fogGrid.IsFogged(cell))
                    continue;
                foreach (var thing in Map.thingGrid.ThingsListAtFast(cell))
                {
                    if (thing is not Pawn && !FactoryGlass.Includes(thing) &&
                        thing.def.drawerType != DrawerType.RealtimeOnly)
                        continue;
                    if (!Map.fogGrid.IsFogged(thing.Position) && dynamicDrawn.Add(thing))
                        GlassDynamic.Draw(buffers, thing);
                }
            }
            dynamicDrawn.Clear();
        }

        private void CopyTerrain(float altitude)
        {
            foreach (var pair in terrainWindows)
            {
                var sources = (List<LayerSubMesh>)layerMeshes.GetValue(pair.Key);
                foreach (var source in sources)
                {
                    if (source.disabled || source.material == null || source.verts.Count == 0)
                        continue;
                    var target = GetSubMesh(source.material);
                    for (int i = 0; i + 2 < source.tris.Count; i += 3)
                        GlassStaticPrint.ClipTriangle(target, Read(source.tris[i]), Read(source.tris[i + 1]),
                            Read(source.tris[i + 2]), pair.Value, clipA, clipB, altitude);

                    GlassStaticPrint.Vertex Read(int index) => new GlassStaticPrint.Vertex(source.verts[index],
                        index < source.uvs.Count ? source.uvs[index] : default,
                        index < source.colors.Count ? source.colors[index] : new Color32(255, 255, 255, 255));
                }
            }
        }

        private void PrintBelow(Map map, float altitude)
        {
            printed.Clear();
            foreach (var lower in windows.Keys)
            {
                foreach (var cell in CellRect.CenteredOn(lower, 2))
                {
                    if (!BandCompatibility.SameFloor(map, lower, cell) || !dynamicCells.Add(cell) || map.fogGrid.IsFogged(cell))
                        continue;
                    foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
                    {
                        if ((thing.def.category != ThingCategory.Plant && thing.def.category != ThingCategory.Item &&
                             thing.def.category != ThingCategory.Building) ||
                            (thing.def.drawerType == DrawerType.RealtimeOnly && thing.def.category != ThingCategory.Building) ||
                            map.fogGrid.IsFogged(thing.Position) || !printed.Add(thing))
                            continue;
                        PrintClipped(thing, altitude);
                    }
                }
            }
            printed.Clear();
        }

        private void PrintClipped(Thing thing, float altitude)
        {
            var before = new Dictionary<LayerSubMesh, MeshCounts>();
            foreach (var mesh in subMeshes)
                before.Add(mesh, new MeshCounts(mesh));
            try
            {
                if (thing.def.drawerType == DrawerType.RealtimeOnly)
                {
                    if (!GlassDynamic.Ready)
                        thing.Graphic?.Print(this, thing, 0f);
                }
                else
                    thing.Print(this);
            }
            catch (System.Exception error)
            {
                foreach (var mesh in subMeshes)
                {
                    before.TryGetValue(mesh, out var counts);
                    counts.Restore(mesh);
                }
                Log.WarningOnce("[AASB2 Extended] Glass projection skipped " + thing.def.defName +
                    ": " + error.Message, thing.def.defName.GetHashCode());
                return;
            }

            foreach (var mesh in subMeshes)
            {
                before.TryGetValue(mesh, out var counts);
                if (mesh.verts.Count == counts.Vertices)
                    continue;
                var triangles = new List<GlassStaticPrint.Vertex>();
                if (mesh.uvs.Count == mesh.verts.Count &&
                    (mesh.colors.Count == mesh.verts.Count || mesh.colors.Count == counts.Colors))
                {
                    for (int i = counts.Triangles; i < mesh.tris.Count; i += 3)
                    {
                        int a = mesh.tris[i], b = mesh.tris[i + 1], c = mesh.tris[i + 2];
                        if (a < counts.Vertices || b < counts.Vertices || c < counts.Vertices)
                            continue;
                        triangles.Add(Read(a));
                        triangles.Add(Read(b));
                        triangles.Add(Read(c));
                    }
                }
                counts.Restore(mesh);
                for (int i = 0; i < triangles.Count; i += 3)
                    GlassStaticPrint.ClipTriangle(mesh, triangles[i], triangles[i + 1], triangles[i + 2],
                        windows, clipA, clipB, altitude);

                GlassStaticPrint.Vertex Read(int index) =>
                    new GlassStaticPrint.Vertex(mesh.verts[index], mesh.uvs[index],
                        index < mesh.colors.Count ? mesh.colors[index] : new Color32(255, 255, 255, 255));
            }
        }

        private readonly struct MeshCounts
        {
            internal readonly int Vertices, Triangles;
            private readonly int uvs;
            internal readonly int Colors;

            internal MeshCounts(LayerSubMesh mesh)
            {
                Vertices = mesh.verts.Count;
                Triangles = mesh.tris.Count;
                uvs = mesh.uvs.Count;
                Colors = mesh.colors.Count;
            }

            internal void Restore(LayerSubMesh mesh)
            {
                mesh.verts.RemoveRange(Vertices, mesh.verts.Count - Vertices);
                mesh.tris.RemoveRange(Triangles, mesh.tris.Count - Triangles);
                mesh.uvs.RemoveRange(uvs, mesh.uvs.Count - uvs);
                mesh.colors.RemoveRange(Colors, mesh.colors.Count - Colors);
            }
        }

        private static void AddCell(LayerSubMesh mesh, IntVec3 upper, IntVec3 lower, float altitude)
        {
            int start = mesh.verts.Count;
            mesh.verts.Add(new Vector3(upper.x, altitude, upper.z));
            mesh.verts.Add(new Vector3(upper.x, altitude, upper.z + 1));
            mesh.verts.Add(new Vector3(upper.x + 1, altitude, upper.z + 1));
            mesh.verts.Add(new Vector3(upper.x + 1, altitude, upper.z));
            mesh.uvs.Add(new Vector3(lower.x, lower.z, 0));
            mesh.uvs.Add(new Vector3(lower.x, lower.z + 1, 0));
            mesh.uvs.Add(new Vector3(lower.x + 1, lower.z + 1, 0));
            mesh.uvs.Add(new Vector3(lower.x + 1, lower.z, 0));
            for (int i = 0; i < 4; i++)
                mesh.colors.Add(new Color32(255, 255, 255, 255));
            mesh.tris.Add(start);
            mesh.tris.Add(start + 1);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start);
            mesh.tris.Add(start + 2);
            mesh.tris.Add(start + 3);
        }
    }
}
