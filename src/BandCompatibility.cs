using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class BandCompatibility
    {
        private delegate bool Partners(Map map, IntVec3 cell, out IntVec3 up, out IntVec3 down);
        private static readonly Partners partner;
        private static readonly Func<Map, IntVec3, bool> gutter;
        private static readonly Func<Map, IntVec3, int> band;
        private static readonly Func<Map, int, CellRect> rectangle;
        private static readonly Func<Map, bool> banded;
        private static readonly Func<Map, int> surfaceBand;
        private static readonly Func<Map, IntVec3, int> level;
        private static readonly MethodInfo inBounds = AccessTools.DeclaredMethod(typeof(GenGrid), "InBounds", new[] { typeof(IntVec3), typeof(Map) });
        private static MethodInfo skyAt;
        private static Type skylights;
        internal static bool SkylightsReady => skyAt != null;
        internal static bool VisualReady { get; private set; }
        internal static bool PortsReady => partner != null && band != null && gutter != null &&
            rectangle != null && banded != null && level != null;

        internal static bool IsBanded(Map map) => map != null && banded?.Invoke(map) == true;
        internal static int Level(Map map, IntVec3 cell) => level?.Invoke(map, cell) ?? 0;

        internal static bool TrySurface(Map map, out CellRect bounds)
        {
            bounds = default;
            if (!IsBanded(map) || surfaceBand == null || rectangle == null)
                return false;
            bounds = rectangle(map, surfaceBand(map));
            return bounds.Width > 0 && bounds.Height > 0;
        }

        internal static bool TryLocate(Map map, IntVec3 cell, out IntVec2 local, out int floor)
        {
            local = default;
            floor = 0;
            if (!PortsReady || !IsBanded(map) || !cell.InBounds(map) || gutter(map, cell))
                return false;
            floor = band(map, cell);
            var bounds = rectangle(map, floor);
            if (!bounds.Contains(cell))
                return false;
            local = new IntVec2(cell.x - bounds.minX, cell.z - bounds.minZ);
            return true;
        }

        static BandCompatibility()
        {
            var bands = AccessTools.TypeByName("AsAboveSoBelow.ABBands");
            var column = AccessTools.TypeByName("AsAboveSoBelow.ABColumnLink");
            if (bands == null || column == null)
                return;
            var cellArgs = new[] { typeof(Map), typeof(IntVec3) };
            var byRef = typeof(IntVec3).MakeByRefType();
            partner = Find<Partners>(column, "TryPartnerCells", typeof(bool),
                typeof(Map), typeof(IntVec3), byRef, byRef);
            band = Find<Func<Map, IntVec3, int>>(bands, "BandOf", typeof(int), cellArgs);
            gutter = Find<Func<Map, IntVec3, bool>>(bands, "InGutter", typeof(bool), cellArgs);
            rectangle = Find<Func<Map, int, CellRect>>(bands, "RectOfBand", typeof(CellRect), typeof(Map), typeof(int));
            banded = Find<Func<Map, bool>>(bands, "Banded", typeof(bool), typeof(Map));
            surfaceBand = Find<Func<Map, int>>(bands, "SurfaceBand", typeof(int), typeof(Map));
            level = Find<Func<Map, IntVec3, int>>(bands, "LevelOf", typeof(int), cellArgs);

            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".bands");
            VisualReady = partner != null && band != null && gutter != null;
            if (VisualReady)
                InstallGlassInvalidation(new Harmony(CompatibilityStatus.HarmonyId + ".glass-dirty"));
            if (VisualReady && ModsConfig.IsActive("dubwise.dubsskylights"))
                InstallSkylights(new Harmony(CompatibilityStatus.HarmonyId + ".skylights"));

            if (band == null || gutter == null || rectangle == null || inBounds == null ||
                !ModsConfig.IsActive("vanillaexpanded.vfefactory"))
                return;
            var factory = AccessTools.TypeByName("VanillaFurnitureExpandedFactory.Building_UndergroundConveyorBase");
            if (factory == null)
                return;
            var search = AccessTools.DeclaredMethod(factory, "FindLinkTarget",
                new[] { typeof(IntVec3), typeof(Rot4), typeof(Rot4), typeof(float), typeof(Map), typeof(ThingDef) });
            if (search?.ReturnType == factory && search.IsStatic)
                TryPatch(harmony, search, transpiler: nameof(ConveyorSearch));
        }

        private static T Find<T>(Type type, string name, Type returns, params Type[] args) where T : Delegate
        {
            var method = AccessTools.DeclaredMethod(type, name, args);
            return method?.ReturnType == returns && method.IsStatic ? method.CreateDelegate<T>() : null;
        }

        private static bool TryPatch(Harmony harmony, MethodInfo target, string transpiler = null, string postfix = null)
        {
            if (target == null)
                return false;
            try
            {
                harmony.Patch(target,
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(BandCompatibility), postfix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(BandCompatibility), transpiler));
                return true;
            }
            catch (Exception error)
            {
                Log.Warning("[AASB2 Extended] Compatibility patch disabled for " + target.Name + ": " + error.Message);
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> ConveyorSearch(IEnumerable<CodeInstruction> instructions)
        {
            var body = new List<CodeInstruction>(instructions);
            if (body.FindAll(op => op.Calls(inBounds)).Count != 1)
                throw new InvalidOperationException("Unexpected Factory cell-search contract.");
            var replacement = AccessTools.DeclaredMethod(typeof(BandCompatibility), nameof(InSearchBand));
            foreach (var op in body)
            {
                if (op.Calls(inBounds))
                {
                    var loadOrigin = new CodeInstruction(OpCodes.Ldarg_0);
                    loadOrigin.labels.AddRange(op.labels);
                    loadOrigin.blocks.AddRange(op.blocks);
                    op.labels.Clear();
                    op.blocks.Clear();
                    yield return loadOrigin;
                    op.operand = replacement;
                }
                yield return op;
            }
        }

        private static bool InSearchBand(IntVec3 candidate, Map map, IntVec3 origin)
        {
            if (!candidate.InBounds(map))
                return false;
            int floor = band(map, origin);
            return !gutter(map, origin) && !gutter(map, candidate) &&
                floor == band(map, candidate) && rectangle(map, floor).Contains(candidate);
        }

        internal static bool TryAdjacent(Map map, IntVec3 cell, bool upward, out IntVec3 adjacent)
        {
            adjacent = IntVec3.Invalid;
            if (map == null || !cell.InBounds(map) || partner == null || gutter == null || gutter(map, cell) ||
                !partner(map, cell, out var up, out var down))
                return false;
            adjacent = upward ? up : down;
            return adjacent.IsValid;
        }

        internal static IEnumerable<IntVec3> ShaftCells(Map map, IntVec3 origin)
        {
            var seen = new HashSet<IntVec3> { origin };
            foreach (bool upward in new[] { false, true })
            {
                var cell = origin;
                while (TryAdjacent(map, cell, upward, out var next) &&
                    TryLocate(map, next, out _, out _) && seen.Add(next))
                {
                    yield return next;
                    cell = next;
                }
            }
        }

        internal static bool SameFloor(Map map, IntVec3 origin, IntVec3 other) =>
            band != null && gutter != null && other.InBounds(map) && !gutter(map, other) &&
            band(map, origin) == band(map, other);

        internal static bool IsOnOtherFloor(Map map, int originFloor, IntVec3 other) =>
            band != null && gutter != null && other.InBounds(map) && !gutter(map, other) &&
            band(map, other) != originFloor;

        private const string GlassDeck = "AASBExt_ExperimentalGlassFloor";
        private const string RoofSurface = "AB_RoofSurface";

        internal static bool TryAperture(Map map, IntVec3 upper, out IntVec3 lower)
        {
            lower = IntVec3.Invalid;
            if (!upper.InBounds(map))
                return false;
            var grid = map.terrainGrid;
            string terrain = grid.TerrainAt(upper)?.defName;
            if (terrain != GlassDeck && (terrain != RoofSurface || !SkylightsReadyEnabled) ||
                !TryAdjacent(map, upper, false, out lower))
                return false;

            return terrain == GlassDeck
                ? grid.UnderTerrainAt(upper)?.defName == RoofSurface || grid.BaseTerrainAt(upper)?.defName == RoofSurface
                : HasSkylight(map, lower);
        }

        internal static bool SkylightsReadyEnabled => skyAt != null &&
            CompatibilityStatus.FloorSettings?.EnableDubsSkylights != false;

        internal static bool LightPassable(Map map, IntVec3 lower, IntVec3 upper) =>
            map.roofGrid.RoofAt(lower)?.isThickRoof != true && map.edificeGrid[lower]?.def.blockLight != true &&
            map.edificeGrid[upper]?.def.blockLight != true;

        internal static bool TryLightOpening(Map map, IntVec3 lower, out IntVec3 upper)
        {
            upper = IntVec3.Invalid;
            if (!lower.InBounds(map) || !GlassVisualMap.TryUpperIndex(map, map.cellIndices.CellToIndex(lower), out int index))
                return false;
            upper = map.cellIndices.IndexToCell(index);
            return LightPassable(map, lower, upper);
        }

        internal static bool TryGlass(Map map, IntVec3 upper, out IntVec3 lower, out string reason)
        {
            lower = IntVec3.Invalid;
            if (!upper.InBounds(map) || !GlassVisualMap.TryLowerIndex(map, map.cellIndices.CellToIndex(upper), out int index))
            {
                reason = "no transparent opening";
                return false;
            }
            lower = map.cellIndices.IndexToCell(index);
            return ViewPassable(map, lower, out reason);
        }

        internal static bool ViewPassable(Map map, IntVec3 lower, out string reason)
        {
            if (map.fogGrid.IsFogged(lower))
            {
                reason = "lower cell is fogged";
                return false;
            }
            var below = map.terrainGrid.TerrainAt(lower);
            if (below == null || below.dontRender)
            {
                reason = "lower terrain has no surface material";
                return false;
            }
            reason = "visible";
            return true;
        }

        private static Func<MapDrawLayer, Map> projectedLayerMap;

        private static void InstallGlassInvalidation(Harmony harmony)
        {
            var mapField = AccessTools.DeclaredField(typeof(MapDrawer), "map");
            var dirty = AccessTools.DeclaredMethod(typeof(MapDrawer), "MapMeshDirty",
                new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) });
            if (mapField?.FieldType != typeof(Map) || dirty?.ReturnType != typeof(void) ||
                !TryPatch(harmony, dirty, postfix: nameof(InvalidateProjectedThings)))
                Log.Warning("[AASB2 Extended] Glass mesh changes require a full section refresh.");
            var getMap = AccessTools.PropertyGetter(typeof(MapDrawLayer), "Map");
            if (getMap?.ReturnType == typeof(Map) && AccessTools.Field(typeof(SectionLayer), "section")?.FieldType == typeof(Section))
            {
                projectedLayerMap = getMap.CreateDelegate<Func<MapDrawLayer, Map>>();
                foreach (var type in SectionLayer_GlassView.SurfaceLayers)
                    TryPatch(harmony, AccessTools.DeclaredMethod(type, nameof(MapDrawLayer.Regenerate), Type.EmptyTypes),
                        postfix: nameof(RefreshProjectedSurface));
            }
            var nativeBands = AccessTools.TypeByName("AsAboveSoBelow.ABBands");
            var nativeBandMap = nativeBands?.Assembly.GetType("AsAboveSoBelow.ABBandMap");
            if (nativeBandMap != null)
                TryPatch(harmony, AccessTools.DeclaredMethod(nativeBands, "RebindAfterLoad",
                    new[] { typeof(Map), nativeBandMap }), postfix: nameof(GlassBandsRebound));
        }

        private static void RefreshProjectedSurface(SectionLayer __instance, Section ___section)
        {
            var map = projectedLayerMap(__instance);
            if (GlassVisualMap.MayShowOpenings(map))
                map.GetComponent<GlassVisualMap>()?.DirtySurfaceProjections(___section);
        }

        private static void GlassBandsRebound(Map __0) => GlassVisualMap.ResetIndex(__0);

        private static void InvalidateProjectedThings(Map ___map, IntVec3 __0, ulong __1)
        {
            ulong relevant = MapMeshFlagDefOf.Things | MapMeshFlagDefOf.Buildings | MapMeshFlagDefOf.FogOfWar | MapMeshFlagDefOf.GroundGlow;
            // Terrain changes can seal an opening that was previously clear.
            bool terrainChanged = (__1 & (ulong)MapMeshFlagDefOf.Terrain) != 0;
            if (terrainChanged && !dirtyingProjection)
                GlassVisualMap.UpdateOpening(___map, __0);
            if (!dirtyingProjection && (terrainChanged || (__1 & (ulong)MapMeshFlagDefOf.GroundGlow) != 0))
                GlassLightTransfer.InvalidateBelow(___map, __0, terrainChanged);
            if (((__1 & relevant) == 0 && !terrainChanged) || !GlassVisualMap.MayShowOpenings(___map) ||
                !TryAdjacent(___map, __0, true, out _))
                return;
            if (terrainChanged || (__1 & (ulong)MapMeshFlagDefOf.GroundGlow) != 0)
                DirtyAbove(___map, __0);
            if ((__1 & (ulong)(MapMeshFlagDefOf.Things | MapMeshFlagDefOf.Buildings | MapMeshFlagDefOf.FogOfWar)) == 0)
                return;
            foreach (var cell in CellRect.CenteredOn(__0, 2))
                if (SameFloor(___map, __0, cell))
                    DirtyAbove(___map, cell);
        }

        [ThreadStatic] private static bool dirtyingProjection;

        internal static void DirtyAbove(Map map, IntVec3 lower)
        {
            if (dirtyingProjection || map?.GetComponent<GlassVisualMap>()?.Enabled != true ||
                !TryAdjacent(map, lower, true, out var upper))
                return;
            string surface = map.terrainGrid.TerrainAt(upper)?.defName;
            if (surface != GlassDeck && (surface != RoofSurface || skylights == null))
                return;
            dirtyingProjection = true;
            try { map.mapDrawer.MapMeshDirty(upper, MapMeshFlagDefOf.Terrain | MapMeshFlagDefOf.GroundGlow, true, false); }
            finally { dirtyingProjection = false; }
        }

        [DebugAction("AASB2 Extended", "Inspect glass rendering at cell",
            actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectGlass()
        {
            SaveGlassReport(Verse.Find.CurrentMap, UI.MouseCell());
        }

        internal static void SaveGlassReport(Map map, IntVec3 upper)
        {
            if (map == null || !upper.InBounds(map))
                return;
            var grid = map.terrainGrid;
            var visual = map.GetComponent<GlassVisualMap>();
            bool allowed = TryGlass(map, upper, out var lower, out var reason);
            if (!lower.IsValid)
                TryAdjacent(map, upper, false, out lower);
            bool hasLower = lower.IsValid && lower.InBounds(map);
            DiagnosticReports.Write("glass-cell", $"[AASB2 Extended] Glass at {upper} level={Level(map, upper)} " +
                $"terrain={grid.TerrainAt(upper)?.defName} top={grid.TopTerrainAt(upper)?.defName} " +
                $"under={grid.UnderTerrainAt(upper)?.defName} base={grid.BaseTerrainAt(upper)?.defName} lower={lower} " +
                $"roof={(hasLower ? map.roofGrid.RoofAt(lower)?.defName : "-")} " +
                $"ground={(hasLower ? grid.TerrainAt(lower)?.defName : "-")} " +
                $"snowUpper={map.snowGrid.GetDepth(upper):0.###} snowLower={(hasLower ? map.snowGrid.GetDepth(lower) : 0f):0.###} " +
                $"skylight={hasLower && HasSkylight(map, lower)} fog={hasLower && map.fogGrid.IsFogged(lower)} " +
                $"enabled={visual?.Enabled} layer={VisualReady} allowed={allowed} reason={reason} " +
                $"lastMesh={visual?.MaterialAt(upper) ?? "-"}");
        }

        private static void InstallSkylights(Harmony harmony)
        {
            skylights = AccessTools.TypeByName("Dubs_Skylight.MapComp_Skylights");
            var building = AccessTools.TypeByName("Dubs_Skylight.Building_skyLight");
            if (skylights == null || building == null)
                return;
            skyAt = AccessTools.DeclaredMethod(skylights, "SkyAt", new[] { typeof(IntVec3) });
            if (skyAt?.ReturnType != typeof(bool))
            {
                skyAt = null;
                return;
            }
            var refresh = AccessTools.DeclaredMethod(skylights, "RegenGrid", Type.EmptyTypes);
            if (refresh?.ReturnType != typeof(void))
            {
                skyAt = null;
                return;
            }
            if (!TryPatch(harmony, refresh, postfix: nameof(SkylightGridRefreshed)))
            {
                skyAt = null;
                return;
            }
            // Hook grid changes around the native building lifecycle, including removal.
            var spawn = AccessTools.DeclaredMethod(building, "SpawnSetup", new[] { typeof(Map), typeof(bool) });
            if (!TryPatch(harmony, spawn, postfix: nameof(SkylightSpawned)) || !IntegrationStatus.Registered(spawn, harmony.Id))
            {
                skyAt = null;
                return;
            }
            var despawn = AccessTools.DeclaredMethod(building, "DeSpawn", new[] { typeof(DestroyMode) });
            if (despawn == null)
            {
                skyAt = null;
                return;
            }
            try
            {
                harmony.Patch(despawn,
                    prefix: new HarmonyMethod(typeof(BandCompatibility), nameof(BeforeSkylightRemoval)),
                    postfix: new HarmonyMethod(typeof(BandCompatibility), nameof(SkylightRemoved)));
                if (!IntegrationStatus.Registered(despawn, harmony.Id))
                    throw new InvalidOperationException("Skylight removal hooks not registered.");
            }
            catch (Exception error)
            {
                skyAt = null;
                Log.Warning("[AASB2 Extended] Skylight rendering disabled: " + error.Message);
            }
        }

        private static void SkylightGridRefreshed(Map ___map) => GlassVisualMap.ResetIndex(___map);

        private static bool HasSkylight(Map map, IntVec3 cell) =>
            CompatibilityStatus.FloorSettings?.EnableDubsSkylights != false &&
            skyAt != null && map.GetComponent(skylights) is MapComponent component &&
            (bool)skyAt.Invoke(component, new object[] { cell });

        private static void SkylightSpawned(Thing __instance, Map __0)
        {
            foreach (var cell in __instance.OccupiedRect())
            {
                if (TryAdjacent(__0, cell, true, out var upper))
                    GlassVisualMap.UpdateOpening(__0, upper);
                DirtyAbove(__0, cell);
            }
        }

        private static void BeforeSkylightRemoval(Thing __instance, out (Map map, CellRect rect) __state) =>
            __state = (__instance.Map, __instance.OccupiedRect());

        private static void SkylightRemoved((Map map, CellRect rect) __state)
        {
            foreach (var cell in __state.rect)
            {
                if (TryAdjacent(__state.map, cell, true, out var upper))
                    GlassVisualMap.UpdateOpening(__state.map, upper);
                DirtyAbove(__state.map, cell);
            }
        }
    }

    public sealed class GlassVisualMap : MapComponent
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, GlassVisualMap> indexes = new();
        private readonly GlassOpeningIndex openings = new();
        private bool? enabled;
        private bool initialized, indexed, skylightsEnabled;
        private readonly Dictionary<IntVec3, string> materials = new();
        private readonly Dictionary<Section, HashSet<SectionLayer_GlassView>> surfaceProjections = new();

        internal void RegisterSurface(Section source, SectionLayer_GlassView projection)
        {
            if (!surfaceProjections.TryGetValue(source, out var projections))
                surfaceProjections.Add(source, projections = new());
            projections.Add(projection);
        }

        internal void ForgetSurface(Section source, SectionLayer_GlassView projection)
        {
            if (surfaceProjections.TryGetValue(source, out var projections) && projections.Remove(projection) &&
                projections.Count == 0)
                surfaceProjections.Remove(source);
        }

        internal void DirtySurfaceProjections(Section source)
        {
            if (surfaceProjections.TryGetValue(source, out var projections))
                foreach (var projection in projections)
                    projection.Dirty = true;
        }

        internal string MaterialAt(IntVec3 cell) => materials.TryGetValue(cell, out var material) ? material : null;

        internal void Record(IntVec3 cell, string material)
        {
            if (material == null)
                materials.Remove(cell);
            else
                materials[cell] = material;
        }

        internal bool Enabled => enabled ??= BandCompatibility.VisualReady && CompatibilityStatus.FloorSettings?.EnableVisual == true;

        public GlassVisualMap(Map map) : base(map) => indexes.AddOrUpdate(map, this);

        private bool EnsureIndex()
        {
            bool sky = BandCompatibility.SkylightsReadyEnabled;
            if (indexed && skylightsEnabled == sky)
                return true;
            if (!initialized)
                return false;
            skylightsEnabled = sky;
            openings.Clear();
            if (BandCompatibility.IsBanded(map))
                foreach (var cell in map.AllCells)
                    if (BandCompatibility.TryAperture(map, cell, out var lower))
                        openings.Set(map.cellIndices.CellToIndex(cell), map.cellIndices.CellToIndex(lower));
            indexed = true;
            return true;
        }

        internal static bool TryLowerIndex(Map map, int upper, out int lower, bool view = false)
        {
            lower = -1;
            if (map == null || !indexes.TryGetValue(map, out var entry) || view && !entry.Enabled ||
                (uint)upper >= (uint)map.cellIndices.NumGridCells)
                return false;
            if (entry.EnsureIndex())
                return entry.openings.TryLower(upper, out lower);
            if (entry.initialized || !BandCompatibility.TryAperture(map, map.cellIndices.IndexToCell(upper), out var cell))
                return false;
            lower = map.cellIndices.CellToIndex(cell);
            return true;
        }

        internal static bool TryUpperIndex(Map map, int lower, out int upper)
        {
            upper = -1;
            if (map == null || !indexes.TryGetValue(map, out var entry) ||
                (uint)lower >= (uint)map.cellIndices.NumGridCells)
                return false;
            if (entry.EnsureIndex())
                return entry.openings.TryUpper(lower, out upper);
            var cell = map.cellIndices.IndexToCell(lower);
            if (entry.initialized || !BandCompatibility.TryAdjacent(map, cell, true, out var above) ||
                !BandCompatibility.TryAperture(map, above, out var below) || below != cell)
                return false;
            upper = map.cellIndices.CellToIndex(above);
            return true;
        }

        internal static bool MayShowOpenings(Map map) =>
            map != null && indexes.TryGetValue(map, out var entry) && entry.Enabled &&
            (!entry.indexed || !entry.openings.Empty || entry.skylightsEnabled != BandCompatibility.SkylightsReadyEnabled);

        internal static void ResetIndex(Map map)
        {
            if (map != null && indexes.TryGetValue(map, out var entry))
                entry.indexed = false;
        }

        internal static void UpdateOpening(Map map, IntVec3 upper)
        {
            if (map == null || !upper.InBounds(map) || !indexes.TryGetValue(map, out var entry) || !entry.indexed)
                return;
            int index = map.cellIndices.CellToIndex(upper);
            entry.openings.Remove(index);
            if (BandCompatibility.TryAperture(map, upper, out var lower))
                entry.openings.Set(index, map.cellIndices.CellToIndex(lower));
        }

        public override void FinalizeInit()
        {
            initialized = true;
            indexed = false;
            map.events.TerrainChanged -= TerrainChanged;
            map.events.CellFogChanged -= DirtyFogAbove;
            map.events.RoofChanged -= RoofChanged;
            map.events.TerrainChanged += TerrainChanged;
            map.events.CellFogChanged += DirtyFogAbove;
            map.events.RoofChanged += RoofChanged;
            if (Enabled || GlassLightTransfer.Enabled)
                EnsureIndex();
        }

        private void TerrainChanged(IntVec3 cell)
        {
            UpdateOpening(map, cell);
            BandCompatibility.DirtyAbove(map, cell);
        }

        private void RoofChanged(IntVec3 cell)
        {
            BandCompatibility.DirtyAbove(map, cell);
            if (TryLowerIndex(map, map.cellIndices.CellToIndex(cell), out _))
                GlassLightTransfer.InvalidateBelow(map, cell, true);
            if (BandCompatibility.TryAdjacent(map, cell, true, out var upper))
                GlassLightTransfer.InvalidateBelow(map, upper, true);
        }

        private void DirtyFogAbove(IntVec3 cell, bool fogged) => BandCompatibility.DirtyAbove(map, cell);
    }
}
