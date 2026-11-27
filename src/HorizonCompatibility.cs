using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class HorizonCompatibility
    {
        internal const string Package = "jongsh.endlesshorizon";
        private const string Owner = "vragerr.aasb2extendedcompatibility";
        private static readonly ConditionalWeakTable<Map, Region> regions = new ConditionalWeakTable<Map, Region>();
        private static Func<Map, string, Predicate<IntVec3>, IReadOnlyList<Vector2>, bool> setRegion;
        private static Func<Map, int> currentBand, surfaceBand, ringWidth, renderBand;
        private static System.WeakReference<Map> curtainMap;
        private static int curtainFrame = -1, curtainBand;
        private static bool curtainCovered;
        private static CellRect activeUpper;
        private static int activeRing;
        [ThreadStatic] private static bool movingCurtain;
        private static Func<Map, bool> isActive, isBuilt;
        private static Func<Map, int, CellRect> rectangle;
        private static Assembly assembly;
        private static Func<Map, CellRect, bool> drawExternal;
        private static bool upperReady, curtainReady, buildCounterReady;
        [ThreadStatic] private static bool drawingUpper;
        internal static bool Ready { get; private set; }

        private static string NativeBuildStatus(Map map)
        {
            var registry = assembly.GetType("EndlessHorizon.HorizonRegistry");
            var stateType = assembly.GetType("EndlessHorizon.HorizonState");
            var diagnostics = assembly.GetType("EndlessHorizon.HorizonDiagnostics");
            if (registry == null || stateType == null || diagnostics == null)
                return "unavailable";
            var peek = AccessTools.DeclaredMethod(registry, "Peek", new[] { typeof(Map) });
            var format = AccessTools.DeclaredMethod(diagnostics, "BuiltLine", new[] { stateType });
            if (peek?.ReturnType != stateType || !peek.IsStatic || format?.ReturnType != typeof(string) || !format.IsStatic)
                return "unavailable";
            var state = peek.Invoke(null, new object[] { map });
            return state == null ? "no cached state" : (string)format.Invoke(null, new[] { state });
        }

        private sealed class Region
        {
            internal CellRect Bounds;
            internal int Ring;
            internal bool Registered;
            internal int BuildStarts;
        }

        static HorizonCompatibility()
        {
            if (!CompatibilityStatus.HorizonAtStartup ||
                !ModsConfig.IsActive(Package) || !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".horizon");
            try
            {
                var space = AccessTools.TypeByName("EndlessHorizon.Api.HorizonSpace");
                var info = AccessTools.TypeByName("EndlessHorizon.Api.HorizonInfo");
                var clippers = AccessTools.TypeByName("EndlessHorizon.Patch_DrawClippers");
                var bands = AccessTools.TypeByName("AsAboveSoBelow.ABBands");
                var view = AccessTools.TypeByName("AsAboveSoBelow.ABBandView");
                assembly = space?.Assembly;
                if (assembly == null || info?.Assembly != assembly || clippers?.Assembly != assembly)
                    throw new InvalidOperationException("Missing Endless Horizon region API.");
                var map = new[] { typeof(Map) };
                setRegion = Bind<Func<Map, string, Predicate<IntVec3>, IReadOnlyList<Vector2>, bool>>(space, "SetRegion",
                    typeof(bool), typeof(Map), typeof(string), typeof(Predicate<IntVec3>), typeof(IReadOnlyList<Vector2>));
                ringWidth = Bind<Func<Map, int>>(info, "RingWidth", typeof(int), map);
                isActive = Bind<Func<Map, bool>>(info, "IsActive", typeof(bool), map);
                isBuilt = Bind<Func<Map, bool>>(info, "IsBuilt", typeof(bool), map);
                currentBand = Bind<Func<Map, int>>(view, "CurrentBand", typeof(int), map);
                surfaceBand = Bind<Func<Map, int>>(bands, "SurfaceBand", typeof(int), map);
                rectangle = Bind<Func<Map, int, CellRect>>(bands, "RectOfBand", typeof(CellRect), typeof(Map), typeof(int));
                var draw = Method(clippers, "Prefix", typeof(bool), map);
                var external = Method(clippers, "DrawExternally", typeof(bool), typeof(Map), typeof(CellRect));
                harmony.Patch(draw, prefix: new HarmonyMethod(typeof(HorizonCompatibility), nameof(BeforeClippers)));
                harmony.Patch(external, prefix: new HarmonyMethod(typeof(HorizonCompatibility), nameof(BeforeExternal)));
                if (!IntegrationStatus.Registered(draw, harmony.Id) || !IntegrationStatus.Registered(external, harmony.Id))
                    throw new InvalidOperationException("Horizon entry points did not register.");
                Ready = true;
                drawExternal = external.CreateDelegate<Func<Map, CellRect, bool>>();
                upperReady = HorizonUpperDraw.Install(assembly);
                InstallCurtain(bands.Assembly, view);
                InstallBuildCounter(assembly);
                HorizonSurfaceSampling.Install(assembly);
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                Ready = false;
                IntegrationStatus.NoteFailure("Endless Horizon", "entry points", error);
                Log.Warning("[AASB2 Extended] Endless Horizon region disabled: " + error.Message);
            }
        }

        private static void InstallCurtain(Assembly aasb, Type view)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".horizon-curtain");
            try
            {
                var type = aasb.GetType("AsAboveSoBelow.ABBandCurtain");
                var draw = Method(type, "Draw", typeof(void), typeof(Map));
                renderBand = Bind<Func<Map, int>>(view, "RenderBand", typeof(int), typeof(Map));
                var body = PatchProcessor.GetOriginalInstructions(draw);
                foreach (string name in new[] { "DrawQuad", "DrawExteriorStamp" })
                {
                    var part = AccessTools.DeclaredMethod(type, name);
                    if (part == null || body.FindAll(op => op.Calls(part)).Count != 2)
                        throw new InvalidOperationException("AASB2 exterior curtain contract differs: " + name);
                }
                var viewport = AccessTools.DeclaredPropertyGetter(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect));
                if (viewport?.ReturnType != typeof(CellRect) || viewport.IsStatic)
                    throw new InvalidOperationException("Camera viewport getter contract differs.");
                var standin = new HarmonyMethod(typeof(HorizonCompatibility), nameof(UnclippedView));
                harmony.CreateReversePatcher(viewport, standin).Patch(HarmonyReversePatchType.Original);
                var patch = new HarmonyMethod(typeof(HorizonCompatibility), nameof(BeforeCurtain));
                var quad = AccessTools.DeclaredMethod(type, "DrawQuad",
                    new[] { typeof(Material), typeof(float), typeof(float), typeof(float), typeof(float) });
                var stamp = AccessTools.DeclaredMethod(type, "DrawExteriorStamp",
                    new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                if (quad?.ReturnType != typeof(void) || stamp?.ReturnType != typeof(void))
                    throw new InvalidOperationException("AASB2 curtain projection contract differs.");
                harmony.Patch(draw, prefix: patch,
                    finalizer: new HarmonyMethod(typeof(HorizonCompatibility), nameof(ResetCurtain)));
                harmony.Patch(quad, prefix: new HarmonyMethod(typeof(HorizonCompatibility), nameof(MoveQuad)));
                harmony.Patch(stamp, prefix: new HarmonyMethod(typeof(HorizonCompatibility), nameof(MoveStamp)));
                if (!IntegrationStatus.Registered(draw, harmony.Id, patch.method) ||
                    !IntegrationStatus.Registered(quad, harmony.Id) || !IntegrationStatus.Registered(stamp, harmony.Id))
                    throw new InvalidOperationException("Horizon curtain guard did not register.");
                curtainReady = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Endless Horizon curtain", "ABBandCurtain.Draw", error);
                Log.Warning("[AASB2 Extended] Horizon curtain adapter unavailable: " + error.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CellRect UnclippedView(CameraDriver driver) =>
            throw new NotSupportedException("Unpatched camera viewport.");

        private static void BeforeCurtain(Map __0, out bool __state)
        {
            __state = movingCurtain;
            movingCurtain = false;
            if (__0 == null || __0 != Find.CurrentMap || !BandCompatibility.IsBanded(__0) || !isActive(__0))
                return;

            int band = currentBand(__0);
            if (band != renderBand(__0) || LevelAtBand(__0, band) < 0 || Find.CameraDriver == null)
                return;
            if (!CurtainAttempted(__0, band))
            {
                DrawOnce(__0, band, UnclippedView(Find.CameraDriver));
            }
            if (!curtainCovered)
                return;
            activeUpper = rectangle(__0, band);
            activeRing = ringWidth(__0);
            movingCurtain = activeRing > 0;
        }

        private static void ResetCurtain(bool __state) => movingCurtain = __state;
        private static void MoveQuad(ref float __3, ref float __4) => ExtendCurtain(ref __3, ref __4);
        private static void MoveStamp(ref float __2, ref float __3) => ExtendCurtain(ref __2, ref __3);

        private static void ExtendCurtain(ref float bottom, ref float top)
        {
            if (!movingCurtain)
                return;
            HorizonSurfaceRegion.MoveCurtainToRingEdge(activeUpper, activeRing, ref bottom, ref top);
        }

        private static bool CurtainAttempted(Map map, int band) =>
            curtainFrame == Time.frameCount && band == curtainBand &&
            curtainMap != null && curtainMap.TryGetTarget(out var drawn) && drawn == map;

        private static void InstallBuildCounter(Assembly source)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".horizon-monitor");
            try
            {
                var state = source.GetType("EndlessHorizon.HorizonState");
                var map = state == null ? null : AccessTools.DeclaredField(state, "map");
                var build = state == null ? null : AccessTools.DeclaredMethod(state, "BuildSteps", Type.EmptyTypes);
                if (map?.FieldType != typeof(Map) || build?.ReturnType != typeof(IEnumerable<bool>))
                    throw new InvalidOperationException("BuildSteps monitoring contract unavailable.");
                var hook = new HarmonyMethod(typeof(HorizonCompatibility), nameof(CountBuild));
                harmony.Patch(build, prefix: hook);
                buildCounterReady = IntegrationStatus.Registered(build, harmony.Id, hook.method);
            }
            catch (Exception error)
            {
                IntegrationStatus.NoteFailure("Endless Horizon build counter", "BuildSteps", error);
            }
        }

        private static void CountBuild(Map ___map)
        {
            if (BandCompatibility.IsBanded(___map))
                regions.GetOrCreateValue(___map).BuildStarts++;
        }

        private static MethodInfo Method(Type type, string name, Type returns, params Type[] args)
        {
            var method = type == null ? null : AccessTools.DeclaredMethod(type, name, args);
            if (method?.ReturnType != returns || !method.IsStatic)
                throw new InvalidOperationException("Incompatible method: " + name);
            return method;
        }

        private static T Bind<T>(Type type, string name, Type returns, params Type[] args) where T : Delegate =>
            Method(type, name, returns, args).CreateDelegate<T>();

        private static bool AllowHorizon(Map map)
        {
            if (!Ready || !BandCompatibility.IsBanded(map) || !isActive(map))
                return true;
            int surface = surfaceBand(map);
            var bounds = rectangle(map, surface);
            int ring = ringWidth(map);
            if (bounds.Width <= 0 || bounds.Height <= 0 || ring < 1)
                return false;
            var region = regions.GetOrCreateValue(map);
            if (region.Bounds.Equals(bounds) && region.Ring == ring)
                return region.Registered;
            region.Bounds = bounds;
            region.Ring = ring;
            region.Registered = setRegion(map, Owner, bounds.Contains, HorizonSurfaceRegion.DrawArea(bounds, ring));
            if (region.Registered)
                HorizonSurfaceSampling.Update(map, bounds);
            else
                Log.Warning("[AASB2 Extended] Endless Horizon rejected the AASB2 surface region.");
            return region.Registered;
        }

        private static int LevelAtBand(Map map, int band)
        {
            var bounds = rectangle(map, band);
            return BandCompatibility.Level(map, new IntVec3(bounds.minX, 0, bounds.minZ));
        }

        private static bool BeforeClippers(Map __0, ref bool __result)
        {
            if (!Ready || !BandCompatibility.IsBanded(__0) || !isActive(__0))
                return true;
            int band = currentBand(__0);
            if (band == surfaceBand(__0) && !curtainReady && AllowHorizon(__0))
                return true;
            __result = !DrawOnce(__0, band, CameraView(Find.CameraDriver));
            return false;
        }

        private static bool BeforeExternal(Map __0, CellRect __1, ref bool __result)
        {
            if (drawingUpper || !Ready || !BandCompatibility.IsBanded(__0) || !isActive(__0))
                return true;
            int band = currentBand(__0);
            if (band == surfaceBand(__0) && !curtainReady && AllowHorizon(__0))
                return true;
            __result = DrawOnce(__0, band, curtainReady && Find.CameraDriver != null ? CameraView(Find.CameraDriver) : __1);
            return false;
        }

        private static bool DrawOnce(Map map, int band, CellRect view)
        {
            if (CurtainAttempted(map, band))
                return curtainCovered;
            curtainCovered = DrawBand(map, band, view);
            if (curtainMap == null)
                curtainMap = new System.WeakReference<Map>(map);
            else
                curtainMap.SetTarget(map);
            curtainFrame = Time.frameCount;
            curtainBand = band;
            return curtainCovered;
        }

        private static CellRect CameraView(CameraDriver driver) =>
            driver == null ? default : curtainReady ? UnclippedView(driver) : driver.CurrentViewRect;

        private static bool DrawBand(Map map, int band, CellRect view)
        {
            int level = LevelAtBand(map, band);
            if (level < 0 || level > 0 && !upperReady || view.Width < 1 || view.Height < 1 || !AllowHorizon(map))
                return false;
            var surface = rectangle(map, surfaceBand(map));
            var visible = rectangle(map, band);
            if (visible.Width != surface.Width || visible.Height != surface.Height)
                return false;
            int x = visible.minX - surface.minX, z = visible.minZ - surface.minZ;
            var sourceView = HorizonSurfaceRegion.SourceView(view, x, z);
            drawingUpper = true;
            try
            {
                bool drawn = level == 0 ? drawExternal(map, sourceView)
                    : HorizonUpperDraw.AtOffset(x, z, level, () => drawExternal(map, sourceView));
                return drawn && isBuilt(map);
            }
            finally { drawingUpper = false; }
        }

        [DebugAction("AASB2 Extended", "Inspect horizon state", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        internal static void Inspect()
        {
            var map = Find.CurrentMap;
            if (map == null || !ModsConfig.IsActive(Package))
                return;
            if (!Ready || !BandCompatibility.IsBanded(map))
            {
                DiagnosticReports.Write("horizon", $"[AASB2 Extended] Horizon {Package} active={ModsConfig.IsActive(Package)} ready={Ready} physical={map.Size} bands={BandCompatibility.IsBanded(map)}");
                return;
            }
            int surface = surfaceBand(map);
            var bounds = rectangle(map, surface);
            int band = currentBand(map);
            var view = rectangle(map, band);
            int level = BandCompatibility.Level(map, new IntVec3(view.minX, 0, view.minZ));
            regions.TryGetValue(map, out var region);
            string buildStatus;
            try
            {
                buildStatus = NativeBuildStatus(map);
            }
            catch (TargetInvocationException error)
            {
                buildStatus = "diagnostics unavailable: " + error.GetBaseException().Message;
            }
            string installedVersion = ModLister.GetModWithIdentifier(Package, false)?.ModVersion ?? "-";
            DiagnosticReports.Write("horizon", $"[AASB2 Extended] Horizon {Package} version={installedVersion} assembly={assembly.GetName().Version} active={isActive(map)} " +
                $"physical={map.Size} surface={bounds.Width}x{bounds.Height} origin=({bounds.minX},{bounds.minZ}) " +
                $"band={band} level={level} type={(level < 0 ? "underground" : level == 0 ? "surface" : "upper")} " +
                $"ring={ringWidth(map)} regionRegistered={region?.Registered == true} cacheBuilt={isBuilt(map)} buildStarts={(buildCounterReady ? region?.BuildStarts ?? 0 : -1)} " +
                $"views={(region?.Registered == true ? 1 : 0)} renderOffset=({(level > 0 ? view.minX - bounds.minX : 0)},{(level > 0 ? view.minZ - bounds.minZ : 0)}) upperReady={upperReady} curtainPass={CurtainAttempted(map, band) && curtainCovered} " +
                $"draw={(band == surface && region?.Registered == true ? "surface" : level > 0 && upperReady ? "translated ring" : "stock clippers")} " +
                $"waves={ModsConfig.IsActive("patrickschwarzer.waves")} " +
                $"livingWaters={ModsConfig.IsActive("patrickschwarzer.rivers")} " +
                $"quietWonders={ModsConfig.IsActive("choco.quietwonders")} build={buildStatus}");
        }
    }
}
