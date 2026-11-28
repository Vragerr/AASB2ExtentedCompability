using System;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class LocalCompatibilityDiagnostics
    {
        [DebugAction("AASB2 Extended", "Inspect projected object", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectObject()
        {
            var map = Find.CurrentMap;
            var cell = UI.MouseCell();
            if (map == null || !cell.InBounds(map))
                return;
            var output = new StringBuilder("[AASB2 Extended] Object graphics at ").Append(cell);
            output.Append(" glass rendering=").Append(CompatibilityStatus.FloorSettings?.EnableVisual);
            AppendGraphicsCell(output, map, cell);
            if (BandCompatibility.TryAdjacent(map, cell, false, out var lower) && lower.InBounds(map))
            {
                output.AppendLine().Append("Lower native cell:");
                AppendGraphicsCell(output, map, lower);
            }
            foreach (var neighbor in GenAdj.CardinalDirections)
            {
                var adjacent = cell + neighbor;
                if (adjacent.InBounds(map) && BandCompatibility.SameFloor(map, cell, adjacent))
                    AppendGraphicsCell(output, map, adjacent);
            }
            foreach (var thing in cell.GetThingList(map))
            {
                var type = thing.GetType();
                output.AppendLine().Append(thing.def.defName).Append(" class=").Append(type.FullName)
                    .Append(" drawer=").Append(thing.def.drawerType).Append(" graphic=").Append(thing.Graphic?.GetType().Name)
                    .Append(" DrawAt=").Append(AccessTools.Method(type, "DrawAt",
                        new[] { typeof(UnityEngine.Vector3), typeof(bool) })?.DeclaringType?.FullName)
                    .Append(" Print=").Append(AccessTools.Method(type, nameof(Thing.Print),
                        new[] { typeof(SectionLayer) })?.DeclaringType?.FullName);
                if (thing is ThingWithComps holder)
                    foreach (var component in holder.AllComps)
                        output.Append(" ").Append(component.GetType().FullName);
            }
            DiagnosticReports.Write("projected-object", output.ToString());
        }

        private static void AppendGraphicsCell(StringBuilder output, Map map, IntVec3 cell)
        {
            var terrain = map.terrainGrid;
            var edifice = cell.GetEdifice(map);
            var section = map.mapDrawer.SectionAt(cell);
            var nativeType = AccessTools.TypeByName("AsAboveSoBelow.SectionLayer_ABBelowV2");
            output.AppendLine().Append(cell).Append(" floor=").Append(BandCompatibility.Level(map, cell))
                .Append(" terrain=").Append(terrain.TerrainAt(cell)?.defName).Append(" base=").Append(terrain.BaseTerrainAt(cell)?.defName)
                .Append(" roof=").Append(map.roofGrid.RoofAt(cell)?.defName)
                .Append(" edifice=").Append(edifice?.def.defName).Append(" natural rock=").Append(edifice?.def.building?.isNaturalRock)
                .Append(" physical fill=").Append(edifice?.def.Fillage)
                .Append(" link flags=").Append(edifice?.def.graphicData?.linkFlags)
                .Append(" AASB2 below layer=").Append(nativeType != null && section?.GetLayer(nativeType) != null)
                .Append(" glass layer=").Append(section?.GetLayer(typeof(SectionLayer_GlassView)) != null)
                .Append(" glass cell=").Append(BandCompatibility.TryGlass(map, cell, out _, out var reason))
                .Append(" (").Append(reason).Append(')');
        }

        [DebugAction("AASB2 Extended", "Inspect logical surface", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        internal static void InspectSurface()
        {
            var map = Find.CurrentMap;
            if (!BandCompatibility.TrySurface(map, out var surface))
                return;
            var output = new StringBuilder("[AASB2 Extended] Logical surface")
                .Append(" seed=").Append(Find.World.info.seedString).Append(" tile=").Append(map.Tile)
                .Append(" physical=").Append(map.Size).Append(" rect=").Append(surface);
            if (map.Tile.Valid && map.Tile.Tile?.Mutators != null)
                foreach (var mutator in map.Tile.Tile.Mutators)
                    output.Append(" mutator=").Append(mutator.defName);
            AppendEdge(output, map, "north", surface.minX, surface.maxZ, surface.Width, 1, 0);
            AppendEdge(output, map, "south", surface.minX, surface.minZ, surface.Width, 1, 0);
            AppendEdge(output, map, "west", surface.minX, surface.minZ, surface.Height, 0, 1);
            AppendEdge(output, map, "east", surface.maxX, surface.minZ, surface.Height, 0, 1);
            DiagnosticReports.Write("geology", output.ToString());
        }

        private static void AppendEdge(StringBuilder output, Map map, string name, int x, int z, int length, int dx, int dz)
        {
            output.AppendLine().Append(name);
            for (int i = 0; i < 9; i++)
            {
                int step = i * (length - 1) / 8;
                var cell = new IntVec3(x + dx * step, 0, z + dz * step);
                output.Append(" ").Append(map.terrainGrid.TerrainAt(cell)?.defName);
                var rock = cell.GetEdifice(map);
                var roof = map.roofGrid.RoofAt(cell);
                if (roof != null)
                    output.Append("/roof=").Append(roof.defName);
                if (rock?.def.building?.isNaturalRock == true)
                    output.Append("/rock=").Append(rock.def.defName);
            }
        }

        [DebugAction("AASB2 Extended", "Inspect Skiptech door cell", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectSkiptechCell()
        {
            SaveSkipdoor(Find.CurrentMap, UI.MouseCell());
        }

        internal static void SaveSkipdoor(Map map, IntVec3 cell)
        {
            if (!ModsConfig.IsActive("migcorp.skiptech") || !BandCompatibility.TryLocate(map, cell, out var local, out int band))
                return;
            var output = new StringBuilder("[AASB2 Extended] Skiptech cell ").Append(cell)
                .Append(" local=").Append(local).Append(" band=").Append(band);
            var region = map.regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(cell);
            output.Append(" region=").Append(region?.id).Append(" valid=").Append(region?.valid)
                .Append(" door=").Append(region?.IsDoorway);
            foreach (var thing in cell.GetThingList(map))
                if (thing is ThingWithComps holder)
                    foreach (var comp in holder.AllComps)
                        if (comp.GetType().FullName == "MigCorp.Skiptech.SkipNet.Comps.CompSkipdoor")
                            output.Append(" skipdoor=").Append(thing.ThingID)
                                .Append(" assembly=").Append(comp.GetType().Assembly.GetName().Version);
            DiagnosticReports.Write("skipdoor", output.ToString());
        }
    }
}
