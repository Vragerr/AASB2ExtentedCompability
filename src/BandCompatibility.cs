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

        private const string GlassDeck = "AASBExt_ExperimentalGlassFloor";
        private const string RoofSurface = "AB_RoofSurface";

        internal static bool TryLightOpening(Map map, IntVec3 lower, out IntVec3 upper)
        {
            upper = IntVec3.Invalid;
            if (!IsBanded(map) || !TryAdjacent(map, lower, true, out upper) || !upper.InBounds(map) ||
                map.roofGrid.RoofAt(lower)?.isThickRoof == true || map.edificeGrid[lower]?.def.blockLight == true ||
                map.edificeGrid[upper]?.def.blockLight == true)
                return false;
            var grid = map.terrainGrid;
            var surface = grid.TerrainAt(upper)?.defName;
            return surface == GlassDeck &&
                (grid.UnderTerrainAt(upper)?.defName == RoofSurface || grid.BaseTerrainAt(upper)?.defName == RoofSurface) ||
                surface == RoofSurface && HasSkylight(map, lower);
        }

        internal static bool TryGlass(Map map, IntVec3 upper, out IntVec3 lower, out string reason)
        {
            lower = IntVec3.Invalid;
            var grid = map.terrainGrid;
            var surface = grid.TerrainAt(upper)?.defName;
            if (surface != GlassDeck && (surface != RoofSurface || skyAt == null))
            {
                reason = "opaque upper terrain or no Dubs integration";
                return false;
            }
            if (!TryAdjacent(map, upper, false, out lower))
            {
                reason = "no lower playable cell";
                return false;
            }

            if (surface == GlassDeck)
            {
                if (grid.UnderTerrainAt(upper)?.defName != RoofSurface &&
                    grid.BaseTerrainAt(upper)?.defName != RoofSurface)
                {
                    reason = "glass deck lacks roof surface";
                    return false;
                }
            }
            else if (surface != RoofSurface || !HasSkylight(map, lower))
            {
                reason = "opaque upper terrain or no Dubs skylight";
                return false;
            }

            if (map.fogGrid.IsFogged(lower))
            {
                reason = "lower cell is fogged";
                return false;
            }
            var below = grid.TerrainAt(lower);
            if (below == null || below.dontRender)
            {
                reason = "lower terrain has no surface material";
                return false;
            }

            reason = "visible";
            return true;
        }

        private static void InstallGlassInvalidation(Harmony harmony)
        {
            var mapField = AccessTools.DeclaredField(typeof(MapDrawer), "map");
            var dirty = AccessTools.DeclaredMethod(typeof(MapDrawer), "MapMeshDirty",
                new[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) });
            if (mapField?.FieldType != typeof(Map) || dirty?.ReturnType != typeof(void) ||
                !TryPatch(harmony, dirty, postfix: nameof(InvalidateProjectedThings)))
                Log.Warning("[AASB2 Extended] Glass mesh changes require a full section refresh.");
        }

        private static void InvalidateProjectedThings(Map ___map, IntVec3 __0, ulong __1)
        {
            ulong relevant = MapMeshFlagDefOf.Things | MapMeshFlagDefOf.Buildings | MapMeshFlagDefOf.FogOfWar | MapMeshFlagDefOf.GroundGlow;
            // Terrain changes can seal an opening that was previously clear.
            bool terrainChanged = (__1 & (ulong)MapMeshFlagDefOf.Terrain) != 0;
            if (!dirtyingProjection && (terrainChanged || (__1 & (ulong)MapMeshFlagDefOf.GroundGlow) != 0))
                GlassLightTransfer.InvalidateBelow(___map, __0, terrainChanged);
            if (((__1 & relevant) == 0 && !terrainChanged) || ___map.GetComponent<GlassVisualMap>()?.Enabled != true ||
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

        private static bool HasSkylight(Map map, IntVec3 cell) =>
            CompatibilityStatus.FloorSettings?.EnableDubsSkylights != false &&
            skyAt != null && map.GetComponent(skylights) is MapComponent component &&
            (bool)skyAt.Invoke(component, new object[] { cell });

        private static void SkylightSpawned(Thing __instance, Map __0)
        {
            foreach (var cell in __instance.OccupiedRect())
                DirtyAbove(__0, cell);
        }

        private static void BeforeSkylightRemoval(Thing __instance, out (Map map, CellRect rect) __state) =>
            __state = (__instance.Map, __instance.OccupiedRect());

        private static void SkylightRemoved((Map map, CellRect rect) __state)
        {
            foreach (var cell in __state.rect)
                DirtyAbove(__state.map, cell);
        }
    }

    public sealed class GlassVisualMap : MapComponent
    {
        private bool? enabled;
        private readonly Dictionary<IntVec3, string> materials = new Dictionary<IntVec3, string>();
        internal string MaterialAt(IntVec3 cell) => materials.TryGetValue(cell, out var material) ? material : null;
        internal void Record(IntVec3 cell, string material)
        {
            if (material == null)
                materials.Remove(cell);
            else
                materials[cell] = material;
        }
        internal bool Enabled => enabled ??= BandCompatibility.VisualReady && CompatibilityStatus.FloorSettings?.EnableVisual == true;

        public GlassVisualMap(Map map) : base(map) { }

        public override void FinalizeInit()
        {
            if (!Enabled)
                return;
            map.events.TerrainChanged -= DirtyTerrainAbove;
            map.events.CellFogChanged -= DirtyFogAbove;
            map.events.RoofChanged -= DirtyTerrainAbove;
            map.events.TerrainChanged += DirtyTerrainAbove;
            map.events.CellFogChanged += DirtyFogAbove;
            map.events.RoofChanged += DirtyTerrainAbove;
        }

        private void DirtyTerrainAbove(IntVec3 cell) => BandCompatibility.DirtyAbove(map, cell);
        private void DirtyFogAbove(IntVec3 cell, bool fogged) => BandCompatibility.DirtyAbove(map, cell);
    }
}

