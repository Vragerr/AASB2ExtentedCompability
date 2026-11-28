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

        private const float ObjectOffset = 0.024f;
        private const float SurfaceOverlayOffset = 0.0015f;
        private const float TopSnowOffset = 0.310f;
        private static readonly System.Reflection.FieldInfo layerMeshes = AccessTools.Field(typeof(MapDrawLayer), "subMeshes");
        internal static readonly System.Type[] SurfaceLayers =
        {
            typeof(SectionLayer_Terrain), typeof(SectionLayer_Snow), typeof(SectionLayer_Sand), typeof(SectionLayer_PollutionCloud)
        };
        private readonly Dictionary<Section, Dictionary<IntVec3, IntVec3>> surfaceWindows = new();
        private readonly Dictionary<IntVec3, IntVec3> windows = new Dictionary<IntVec3, IntVec3>();
        private CellRect apertureBounds;
        private readonly HashSet<IntVec3> dynamicCells = new HashSet<IntVec3>();
        private readonly HashSet<Thing> dynamicDrawn = new HashSet<Thing>();
        private readonly List<IntVec3> printCells = new();
        private readonly Dictionary<Material, int> nativeMaterialOrder = new();
        private readonly List<int> orderedMaterialSlots = new();
        private readonly List<LayerSubMesh> orderedMaterials = new();
        private readonly Dictionary<IntVec3, IntVec3> snowMask = new();
        private readonly Dictionary<IntVec3, IntVec3> topSnowMask = new();
        private readonly HashSet<IntVec3> terrainCoverage = new();
        private bool monitorsTopSnow;
        private readonly HashSet<Thing> printed = new HashSet<Thing>();
        private readonly Dictionary<LayerSubMesh, MeshCounts> previousMeshCounts = new();
        private readonly List<GlassStaticPrint.Vertex> triangles = new();
        private readonly List<GlassStaticPrint.Vertex> clipA = new List<GlassStaticPrint.Vertex>(8);
        private readonly List<GlassStaticPrint.Vertex> clipB = new List<GlassStaticPrint.Vertex>(8);


        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            windows.Clear();
            dynamicCells.Clear();
            var map = Map;
            var visual = map.GetComponent<GlassVisualMap>();
            if (visual != null)
            {
                foreach (var source in surfaceWindows.Keys)
                    visual.ForgetSurface(source, this);
                if (monitorsTopSnow)
                    visual.ForgetSurface(section, this);
            }
            monitorsTopSnow = false;
            surfaceWindows.Clear();
            topSnowMask.Clear();
            if (visual?.Enabled != true || !BandCompatibility.IsBanded(map))
                return;

            // This is a projected floor mesh above the solid terrain. It does not alter TerrainGrid.
            var terrainAltitude = AltitudeLayer.Terrain.AltitudeFor();
            var altitude = terrainAltitude + ObjectOffset;
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
                var source = map.mapDrawer.SectionAt(lower);
                if (source != null)
                {
                    if (!surfaceWindows.TryGetValue(source, out var mask))
                    {
                        surfaceWindows.Add(source, mask = new Dictionary<IntVec3, IntVec3>());
                        visual.RegisterSurface(source, this);
                    }
                    mask[lower] = upper;
                }
                if (map.snowGrid.GetDepth(upper) > 0f)
                    topSnowMask[upper] = upper;
                visual.Record(upper, terrain.defName + "/" + material.name);
            }
            if (windows.Count != 0)
            {
                int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
                foreach (var cell in windows.Keys)
                {
                    x0 = System.Math.Min(x0, cell.x);
                    z0 = System.Math.Min(z0, cell.z);
                    x1 = System.Math.Max(x1, cell.x);
                    z1 = System.Math.Max(z1, cell.z);
                }
                apertureBounds = new CellRect(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
                visual.RegisterSurface(section, this);
                monitorsTopSnow = true;
                CopySurfaces(map, altitude, terrainAltitude);
                PrintBelow(map, altitude);
                RestoreNativeMaterialOrder();
                foreach (var entry in windows)
                {
                    var cell = entry.Value;
                    bool ownDeck = map.terrainGrid.TerrainAt(cell)?.defName == "AASBExt_ExperimentalGlassFloor";
                    var material = ownDeck ? GlassMaterials.Deck : GlassMaterials.Skylight;
                    if (material != null)
                        AddCell(GetSubMesh(material), cell, IntVec3.Zero, terrainAltitude + GlassFootprint.GlassTintOffset);
                }
                if (topSnowMask.Count != 0)
                    CopyLayer(section.GetLayer(typeof(SectionLayer_Snow)), topSnowMask,
                        terrainAltitude + TopSnowOffset - terrainAltitude * GlassFootprint.DepthScale);
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
            buffers.Begin(windows, apertureBounds, AltitudeLayer.Terrain.AltitudeFor() + ObjectOffset);
            dynamicDrawn.Clear();
            foreach (var cell in dynamicCells)
            {
                if (Map.fogGrid.IsFogged(cell))
                    continue;
                foreach (var thing in Map.thingGrid.ThingsListAtFast(cell))
                {
                    if (thing is not Pawn && !FactoryGlass.Includes(thing) &&
                        thing.def.drawerType != DrawerType.RealtimeOnly && !GlassViewStorage.HasNativeDraw(thing))
                        continue;
                    if (!Map.fogGrid.IsFogged(thing.Position) && !GlassViewStorage.IsStored(thing) && dynamicDrawn.Add(thing))
                        GlassDynamic.Draw(buffers, thing);
                }
            }
            dynamicDrawn.Clear();
        }

        private void CopySurfaces(Map map, float altitude, float terrainAltitude)
        {
            terrainCoverage.Clear();
            foreach (var pair in surfaceWindows)
                foreach (var type in SurfaceLayers)
                {
                    var mask = pair.Value;
                    if (type == typeof(SectionLayer_Snow))
                    {
                        // Snow geometry at a cell can receive opacity from its neighbors.
                        // Only physical snow in this lower layer belongs to the aperture.
                        snowMask.Clear();
                        foreach (var opening in mask)
                            if (map.snowGrid.GetDepth(opening.Key) > 0f)
                                snowMask.Add(opening.Key, opening.Value);
                        mask = snowMask;
                    }
                    bool ground = type == typeof(SectionLayer_Terrain);
                    CopyLayer(pair.Key.GetLayer(type), mask, ground ? altitude : altitude + SurfaceOverlayOffset,
                        ground ? terrainCoverage : null);
                }

            foreach (var opening in windows)
            {
                if (terrainCoverage.Contains(opening.Key))
                    continue;
                var lower = opening.Key;
                var grid = map.terrainGrid;
                var material = grid.GetMaterial(grid.TerrainAt(lower), lower.IsPolluted(map), grid.ColorAt(lower));
                if (material != null)
                    AddCell(GetSubMesh(material), opening.Value, lower, altitude + terrainAltitude * GlassFootprint.DepthScale);
            }
        }

        private void CopyLayer(MapDrawLayer layer, Dictionary<IntVec3, IntVec3> mask, float altitude,
            HashSet<IntVec3> coverage = null)
        {
            if (mask.Count == 0 || layer == null || !layer.Visible ||
                layerMeshes?.GetValue(layer) is not List<LayerSubMesh> sources)
                return;
            foreach (var source in sources)
            {
                if (source.disabled || source.material == null || source.verts.Count == 0)
                    continue;
                var target = GetSubMesh(source.material);
                var tracked = coverage != null && source.material != MatBases.ShadowMask ? coverage : null;
                for (int i = 0; i + 2 < source.tris.Count; i += 3)
                    GlassStaticPrint.ClipTriangle(target, Read(source.tris[i]), Read(source.tris[i + 1]),
                        Read(source.tris[i + 2]), mask, clipA, clipB, altitude, tracked);

                GlassStaticPrint.Vertex Read(int index) => new GlassStaticPrint.Vertex(source.verts[index],
                    index < source.uvs.Count ? source.uvs[index] : default,
                    index < source.colors.Count ? source.colors[index] : new Color32(255, 255, 255, 255));
            }
        }

        private void PrintBelow(Map map, float altitude)
        {
            printCells.Clear();
            foreach (var lower in windows.Keys)
                foreach (var cell in CellRect.CenteredOn(lower, 2))
                    if (BandCompatibility.SameFloor(map, lower, cell) && dynamicCells.Add(cell))
                        printCells.Add(cell);

            // Match native section traversal for objects on the same altitude.
            // The order of the aperture cells must not decide material submission order.
            printCells.Sort((left, right) =>
            {
                int row = left.z.CompareTo(right.z);
                return row == 0 ? left.x.CompareTo(right.x) : row;
            });
            printed.Clear();
            foreach (var cell in printCells)
            {
                if (map.fogGrid.IsFogged(cell))
                    continue;
                foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
                {
                    if ((thing.def.category != ThingCategory.Plant && thing.def.category != ThingCategory.Item &&
                         thing.def.category != ThingCategory.Building && thing.def.category != ThingCategory.Filth) ||
                        (thing.def.drawerType == DrawerType.RealtimeOnly && thing.def.category != ThingCategory.Building) ||
                        (cell != thing.Position && dynamicCells.Contains(thing.Position)) ||
                        map.fogGrid.IsFogged(thing.Position) || !printed.Add(thing) || GlassViewStorage.IsStored(thing))
                        continue;
                    PrintClipped(thing, altitude);
                }
            }
            printed.Clear();
        }

        private void RestoreNativeMaterialOrder()
        {
            nativeMaterialOrder.Clear();
            orderedMaterialSlots.Clear();
            orderedMaterials.Clear();
            foreach (var source in surfaceWindows.Keys)
            {
                var layer = source.GetLayer(typeof(SectionLayer_ThingsGeneral));
                if (layer == null || layerMeshes?.GetValue(layer) is not List<LayerSubMesh> meshes)
                    continue;
                foreach (var mesh in meshes)
                    if (!mesh.disabled && mesh.verts.Count > 0 && mesh.material != null)
                        nativeMaterialOrder.TryAdd(mesh.material, nativeMaterialOrder.Count);
            }
            for (int i = 0; i < subMeshes.Count; i++)
            {
                var mesh = subMeshes[i];
                if (mesh.verts.Count == 0 || !nativeMaterialOrder.ContainsKey(mesh.material))
                    continue;
                orderedMaterialSlots.Add(i);
                orderedMaterials.Add(mesh);
            }
            orderedMaterials.Sort((left, right) =>
                nativeMaterialOrder[left.material].CompareTo(nativeMaterialOrder[right.material]));
            for (int i = 0; i < orderedMaterials.Count; i++)
                subMeshes[orderedMaterialSlots[i]] = orderedMaterials[i];
        }

        private void PrintClipped(Thing thing, float altitude)
        {
            previousMeshCounts.Clear();
            foreach (var mesh in subMeshes)
                previousMeshCounts.Add(mesh, new MeshCounts(mesh));
            try
            {
                if (thing.def.drawerType == DrawerType.RealtimeOnly)
                {
                    if (!GlassDynamic.Ready)
                        thing.Graphic?.Print(this, thing, 0f);
                }
                else
                    PipeProjection.Print(thing, this);
            }
            catch (System.Exception error)
            {
                foreach (var mesh in subMeshes)
                {
                    previousMeshCounts.TryGetValue(mesh, out var counts);
                    counts.Restore(mesh);
                }
                Log.WarningOnce("[AASB2 Extended] Glass projection skipped " + thing.def.defName +
                    ": " + error.Message, thing.def.defName.GetHashCode());
                return;
            }

            foreach (var mesh in subMeshes)
            {
                previousMeshCounts.TryGetValue(mesh, out var counts);
                if (mesh.verts.Count == counts.Vertices)
                    continue;
                triangles.Clear();
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
