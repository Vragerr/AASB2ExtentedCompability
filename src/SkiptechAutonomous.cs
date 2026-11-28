using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class SkiptechAutonomous
    {
        internal static bool Ready { get; }
        internal static bool MountedReady { get; }
        internal const string Package = "migcorp.skiptech";
        private static Type componentType, doorType;
        private static FieldInfo doors, manager, skipCost, worthItFactor, options;
        private static MethodInfo canEnter, canExit, enterNow, exitNow, notifyArrived;
        private static object entryRole, exitRole;
        private static Action<JobDriver> skipPhysical;
        private static Action<Pawn> clearStairs;
        private static ConstructorInfo access;
        private static JobDef skipJob;
        private delegate bool NativeTransit(Map map, IntVec3 start, IntVec3 goal, Pawn pawn,
            out Building_Door entry, out Building_Door exit, LocalTargetInfo destination);
        private static NativeTransit nativeTransit;
        private static readonly ConditionalWeakTable<Map, MapComponent> hosts;
        private static readonly ConditionalWeakTable<MapComponent, DoorIndex> index;
        private static readonly ConditionalWeakTable<Pawn, Trip> trips;
        [ThreadStatic] private static bool planning;
        [ThreadStatic] private static List<Door> routeDoors;
        private static bool giddyActive, mixedRoutes;
        private static FieldInfo transitPawn, transitDestination, transitMode;
        private static FastInvokeHandler checkNativeAccess;
        private static readonly ConditionalWeakTable<Pawn, NavigationTrace> traces;

        private sealed class NavigationTrace
        {
            internal readonly List<string> Lines = new();
        }

        private readonly record struct SaveState(bool Changed, LocalTargetInfo Target, PathEndMode Mode);

        private sealed class DoorIndex
        {
            internal ThingComp[] Doors;
            internal Dictionary<int, List<ThingComp>> ByFloor;
        }

        private static readonly ConditionalWeakTable<Reachability, NavigationRevision> revisions;
        private static ThingDef cargoDrone;
        private sealed class NavigationRevision
        {
            internal int Value;
            internal readonly ConditionalWeakTable<Pawn, WalkMemo> Walks = new();
        }

        private sealed class WalkMemo
        {
            internal int Tick = -1, Revision;
            internal readonly Dictionary<WalkKey, float> Costs = new();
        }

        private readonly record struct WalkKey(IntVec3 Start, IntVec3 End, Thing Thing, PathEndMode Mode,
            TraverseParms Traversal, bool Stairs = false);

        private sealed class Trip
        {
            internal Job Original;
            internal LocalTargetInfo Destination;
            internal PathEndMode Mode;
            internal int ToilIndex, JobLoadId;
            internal Hop Next;
            internal bool Charging, StairApproach;
            internal int NextPoll;
            internal IntVec3 Approach;
            internal bool IsCurrent(Pawn pawn) => Original == pawn.CurJob && Original.loadID == JobLoadId &&
                (pawn.jobs.curDriver?.CurToilIndex ?? -1) == ToilIndex;
            internal readonly HashSet<(Building Entry, Building Exit)> Tried = new();
        }

        private readonly record struct Door(Building Building, int Band, bool Enter, bool Exit);
        private sealed record Hop(Building Entry, Building Exit);
        private readonly record struct Route(Hop First, float Cost);

        static SkiptechAutonomous()
        {
            if (!ModsConfig.IsActive(Package) || !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            hosts = new();
            index = new();
            trips = new();
            traces = new();
            revisions = new();
            giddyActive = ModsConfig.IsActive(GiddyUpSkipdoors.Package);
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".skiptech-autonomous");
            try
            {
                const string ns = "MigCorp.Skiptech.SkipNet.";
                componentType = AccessTools.TypeByName(ns + "MapComponent_SkipNet");
                doorType = componentType?.Assembly.GetType(ns + "Comps.CompSkipdoor");
                var context = componentType?.Assembly.GetType(ns + "SkipNetAccessContext");
                var driver = componentType?.Assembly.GetType(ns + "JobDriver_SkipLocally");
                var mod = componentType?.Assembly.GetType("MigCorp.Skiptech.MigcorpSkiptechMod");
                skipJob = DefDatabase<JobDef>.GetNamedSilentFail("MigCorp_SkipLocally");
                if (ModsConfig.IsActive("vanillaquestsexpanded.dronefactory"))
                    cargoDrone = DefDatabase<ThingDef>.GetNamedSilentFail("VQE_HaulerDrone");
                if (componentType == null || doorType == null || context == null || driver == null || skipJob?.driverClass != driver ||
                    !typeof(ThingComp).IsAssignableFrom(doorType))
                    throw new InvalidOperationException("Skiptech component or local job unavailable.");

                doors = AccessTools.DeclaredField(componentType, "skipdoors");
                manager = AccessTools.DeclaredField(componentType, "manager");
                options = mod == null ? null : AccessTools.DeclaredField(mod, "Settings");
                skipCost = options == null ? null : AccessTools.DeclaredField(options.FieldType, "skipCost");
                worthItFactor = options == null ? null : AccessTools.DeclaredField(options.FieldType, "worthItFactor");
                var canUse = manager == null ? null : AccessTools.DeclaredMethod(manager.FieldType, "PawnCanUseSkipNet", new[] { typeof(Pawn) });
                access = AccessTools.DeclaredConstructor(context, new[] { typeof(Pawn), typeof(bool) });
                canEnter = AccessTools.DeclaredMethod(doorType, "IsEnterableBy", new[] { context.MakeByRefType() });
                canExit = AccessTools.DeclaredMethod(doorType, "IsExitableBy", new[] { context.MakeByRefType() });
                var reach = AccessTools.DeclaredMethod(typeof(Reachability), nameof(Reachability.CanReach),
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });
                var invalidateWalk = AccessTools.DeclaredMethod(typeof(Reachability), "ClearCache", Type.EmptyTypes);
                var start = AccessTools.DeclaredMethod(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath),
                    new[] { typeof(LocalTargetInfo), typeof(PathEndMode) });
                var arrived = AccessTools.DeclaredMethod(typeof(Pawn_PathFollower), "PatherArrived", Type.EmptyTypes);
                var failed = AccessTools.DeclaredMethod(typeof(Pawn_PathFollower), "PatherFailed", Type.EmptyTypes);
                var expose = AccessTools.DeclaredMethod(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.ExposeData), Type.EmptyTypes);
                var capture = manager == null ? null : AccessTools.DeclaredMethod(manager.FieldType, "TryCapturePathRequest",
                    new[] { typeof(Pawn), typeof(PathRequest), typeof(PathRequest).MakeByRefType() });
                var closest = AccessTools.DeclaredMethod(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable),
                    new[] { typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(IEnumerable<Thing>), typeof(int), typeof(int),
                        typeof(bool), typeof(RegionType), typeof(bool), typeof(bool) });
                var physical = AccessTools.DeclaredMethod(driver, "DoSkip", Type.EmptyTypes);
                var tick = AccessTools.DeclaredMethod(typeof(Pawn_PathFollower), "PatherTick", Type.EmptyTypes);
                var delay = new[] { typeof(Pawn), typeof(int).MakeByRefType() };
                enterNow = AccessTools.DeclaredMethod(doorType, "IsEnterableNowBy", delay);
                exitNow = AccessTools.DeclaredMethod(doorType, "IsExitableNowBy", delay);
                var role = componentType.Assembly.GetType("MigCorp.Skiptech.Utils.SkipdoorType");
                notifyArrived = role == null ? null : AccessTools.DeclaredMethod(doorType, "Notify_PawnArrived", new[] { typeof(Pawn), role });
                if (tick?.ReturnType != typeof(void) || enterNow?.ReturnType != typeof(bool) ||
                    exitNow?.ReturnType != typeof(bool) || notifyArrived?.ReturnType != typeof(void) ||
                    !role.IsEnum || !Enum.IsDefined(role, 1) || !Enum.IsDefined(role, 2))
                    throw new InvalidOperationException("Skiptech timing or arrival contract changed.");
                entryRole = Enum.ToObject(role, 1);
                exitRole = Enum.ToObject(role, 2);
                var commit = AccessTools.DeclaredMethod(driver, "CanStillSkip", Type.EmptyTypes);
                var register = AccessTools.DeclaredMethod(componentType, "RegisterSkipdoor", new[] { doorType });
                var unregister = AccessTools.DeclaredMethod(componentType, "UnregisterSkipdoor", new[] { doorType });
                var wormhole = AccessTools.TypeByName("AsAboveSoBelow.ABWormhole");
                var transit = wormhole == null ? null : AccessTools.DeclaredMethod(wormhole, "TryGetTransit",
                    new[] { typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(Pawn),
                        typeof(Building_Door).MakeByRefType(), typeof(Building_Door).MakeByRefType(), typeof(LocalTargetInfo) });
                var segmenter = wormhole?.Assembly.GetType("AsAboveSoBelow.ABWormholePather");
                var clear = segmenter == null ? null : AccessTools.DeclaredMethod(segmenter, "Clear", new[] { typeof(Pawn) });
                var teleport = AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.Notify_Teleported),
                    new[] { typeof(bool), typeof(bool) });
                var physicalBody = physical == null ? null : PatchProcessor.GetOriginalInstructions(physical);
                int notify = physicalBody?.FindIndex(op => op.Calls(teleport)) ?? -1;
                if (clear?.ReturnType != typeof(void) || !clear.IsStatic || physical?.ReturnType != typeof(void) ||
                    notify < 2 || physicalBody[notify - 2].opcode != System.Reflection.Emit.OpCodes.Ldc_I4_0 ||
                    physicalBody[notify - 1].opcode != System.Reflection.Emit.OpCodes.Ldc_I4_1 ||
                    AccessTools.Field(typeof(Pawn_PathFollower), "destination")?.FieldType != typeof(LocalTargetInfo) ||
                    AccessTools.Field(typeof(Pawn_PathFollower), "peMode")?.FieldType != typeof(PathEndMode))
                    throw new InvalidOperationException("Skiptech physical transition or path save contract changed.");
                if (reach?.ReturnType != typeof(bool) || AccessTools.Field(typeof(Reachability), "map")?.FieldType != typeof(Map) ||
                    start?.ReturnType != typeof(void) || arrived?.ReturnType != typeof(void) ||
                    failed?.ReturnType != typeof(void) || expose?.ReturnType != typeof(void) ||
                    capture?.ReturnType != typeof(bool) || invalidateWalk?.ReturnType != typeof(void) || closest?.ReturnType != typeof(Thing) ||
                    register?.ReturnType != typeof(void) || unregister?.ReturnType != typeof(void) ||
                    transit?.ReturnType != typeof(bool) || !transit.IsStatic ||
                    !typeof(IEnumerable).IsAssignableFrom(doors?.FieldType) || canUse?.ReturnType != typeof(bool) ||
                    access == null || canEnter?.ReturnType != typeof(bool) || canExit?.ReturnType != typeof(bool) ||
                    options?.IsStatic != true || skipCost?.FieldType != typeof(float))
                    throw new InvalidOperationException("Skiptech navigation contract changed.");

                checkNativeAccess = HarmonyLib.MethodInvoker.GetHandler(canUse);
                nativeTransit = transit.CreateDelegate<NativeTransit>();
                clearStairs = clear.CreateDelegate<Action<Pawn>>();
                var invokePhysical = HarmonyLib.MethodInvoker.GetHandler(physical);
                skipPhysical = driverInstance => invokePhysical(driverInstance);
                harmony.Patch(reach, postfix: Hook(nameof(ExtendReach), Priority.Last));
                harmony.Patch(start, prefix: Hook(nameof(StartPath), Priority.First));
                harmony.Patch(arrived, prefix: Hook(nameof(ArriveAtDoor), Priority.First));
                harmony.Patch(failed, prefix: Hook(nameof(ApproachFailed), Priority.First));
                harmony.Patch(tick, prefix: Hook(nameof(TickCharge), Priority.First));
                harmony.Patch(expose, prefix: Hook(nameof(BeforePathSave)), finalizer: Hook(nameof(AfterPathSave)));
                harmony.Patch(capture, prefix: Hook(nameof(KeepApproachLocal), Priority.First));
                harmony.Patch(invalidateWalk, postfix: Hook(nameof(InvalidateWalk)));
                harmony.Patch(closest, postfix: Hook(nameof(FindCrossFloorWork), Priority.Last));

                harmony.Patch(register, postfix: Hook(nameof(InvalidateDoors)));
                harmony.Patch(unregister, postfix: Hook(nameof(InvalidateDoors)));
                foreach (var method in new[] { reach, start, arrived, failed, tick, expose, capture, closest, invalidateWalk, register, unregister })
                    if (!IntegrationStatus.Registered(method, harmony.Id))
                        throw new InvalidOperationException("Skiptech navigation patch did not register: " + method.Name);
                if (giddyActive && GiddyUpSkipdoors.Ready && commit?.ReturnType == typeof(bool))
                {
                    try
                    {
                        harmony.Patch(commit, postfix: Hook(nameof(CheckMountedNativeAccess)));
                        MountedReady = IntegrationStatus.Registered(commit, harmony.Id);
                    }
                    catch (Exception error)
                    {
                        harmony.Unpatch(commit, HarmonyPatchType.Postfix, harmony.Id);
                        IntegrationStatus.NoteFailure("Mounted Skiptech", "native access at commit", error);
                    }
                }

                var collect = AccessTools.DeclaredMethod(segmenter, "CollectForSave", new[] { typeof(Map) });
                var record = collect != null && collect.ReturnType.IsGenericType && typeof(IList).IsAssignableFrom(collect.ReturnType)
                    ? collect.ReturnType.GetGenericArguments()[0] : null;
                if (record?.IsClass == true && collect.IsStatic)
                {
                    transitPawn = AccessTools.DeclaredField(record, "pawn");
                    transitDestination = AccessTools.DeclaredField(record, "realDest");
                    transitMode = AccessTools.DeclaredField(record, "realPeMode");
                    if (transitPawn?.FieldType == typeof(Pawn) && transitDestination?.FieldType == typeof(LocalTargetInfo) &&
                        transitMode?.FieldType == typeof(PathEndMode))
                    {
                        try
                        {
                            harmony.Patch(collect, postfix: Hook(nameof(RestoreTaskTargetInStairSave)));
                            mixedRoutes = IntegrationStatus.Registered(collect, harmony.Id);
                        }
                        catch (Exception error)
                        {
                            harmony.Unpatch(collect, HarmonyPatchType.Postfix, harmony.Id);
                            IntegrationStatus.NoteFailure("Skiptech mixed routes", "AASB2 stair save records", error);
                        }
                    }
                }
                Ready = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Skiptech autonomous", "Reachability / path waypoints / native transition", error);
                Log.Warning("[AASB2 Extended] Skiptech navigation unavailable: " + error.Message);
            }
        }

        private static HarmonyMethod Hook(string name, int priority = Priority.Normal) =>
            new HarmonyMethod(typeof(SkiptechAutonomous), name) { priority = priority };

        private static void InvalidateDoors(MapComponent __instance) => index.Remove(__instance);

        private static void InvalidateWalk(Reachability __instance)
        {
            if (revisions.TryGetValue(__instance, out var revision))
                System.Threading.Interlocked.Increment(ref revision.Value);
        }

        private static MapComponent HostFor(Map map)
        {
            if (hosts.TryGetValue(map, out var host))
                return host;
            host = map.GetComponent(componentType);
            if (host != null)
                hosts.Add(map, host);
            return host;
        }

        private static DoorIndex DoorsFor(Map map, MapComponent component)
        {
            if (!index.TryGetValue(component, out var registered))
            {
                registered = BuildIndex(map, component);
                index.Add(component, registered);
            }
            return registered;
        }

        private static bool CanLeaveFloor(Map map, IntVec3 root, int floor, TraverseParms parms, DoorIndex registered)
        {
            var pawn = parms.pawn;
            if (registered.ByFloor.Count < 2 || !NativeAccess(pawn))
                return false;
            registered.ByFloor.TryGetValue(floor, out var departures);
            var context = new[] { access.Invoke(new object[] { pawn, false }) };
            bool previous = planning;
            planning = true;
            try
            {
                if (departures != null)
                    foreach (var comp in departures)
                        if (comp.parent is Building door && UsableCell(map, door, pawn, parms.maxDanger) &&
                            (bool)canEnter.Invoke(comp, context) &&
                            (root == door.Position || map.reachability.CanReach(root, door, PathEndMode.OnCell, parms)))
                            return true;
                if (mixedRoutes && (!giddyActive || GiddyUpSkipdoors.MountOf(pawn) == null))
                    foreach (var pair in registered.ByFloor)
                        if (pair.Key != floor)
                            foreach (var comp in pair.Value)
                                if (comp.parent is Building door && UsableCell(map, door, pawn, parms.maxDanger) &&
                                    (bool)canEnter.Invoke(comp, context) &&
                                    float.IsFinite(StairLegCost(map, root, door.Position, PathEndMode.OnCell, parms)))
                                    return true;
                return false;
            }
            finally { planning = previous; }
        }

        private static bool EligiblePawn(Pawn pawn, Map map) =>
            CompatibilityStatus.FloorSettings?.EnableSkiptech != false && pawn?.Spawned == true &&
            !pawn.Downed && pawn.Map == map && pawn.CurJob?.def != skipJob &&
            BandCompatibility.IsBanded(map) && !LeadsAnimals(pawn) &&
            (!giddyActive || GiddyUpSkipdoors.Ready &&
                (MountedReady && CompatibilityStatus.FloorSettings?.ProtectMountedSkipdoors != false ||
                    GiddyUpSkipdoors.MountOf(pawn, respectSetting: false) == null));

        private static bool Eligible(Pawn pawn, Map map, IntVec3 start, LocalTargetInfo goal) =>
            map != null && start.InBounds(map) && goal.IsValid && goal.Cell.InBounds(map) &&
            (!goal.HasThing || goal.Thing.Spawned && goal.Thing.Map == map) &&
            BandCompatibility.IsBanded(map) && !BandCompatibility.SameFloor(map, start, goal.Cell) &&
            EligiblePawn(pawn, map) &&
            (cargoDrone == null || pawn.def != cargoDrone ||
                !goal.Cell.IsForbidden(pawn) && (!goal.HasThing || !goal.Thing.IsForbidden(pawn)));

        private static void ExtendReach(Map ___map, IntVec3 __0, LocalTargetInfo __1, PathEndMode __2,
            TraverseParms __3, ref bool __result)
        {
            if (__result || planning || !UnityData.IsInMainThread ||
                __3.mode != TraverseMode.ByPawn || !Eligible(__3.pawn, ___map, __0, __1))
                return;
            __result = (!giddyActive || GiddyUpSkipdoors.MountOf(__3.pawn) == null) &&
                DirectlyReachable(___map, __0, __1, __2, __3) ||
                Plan(___map, __0, __1, __2, __3, null).HasValue;
        }

        private static bool DirectlyReachable(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms)
        {
            var pawn = parms.pawn;
            var component = HostFor(map);
            if (component == null || !BandCompatibility.TryLocate(map, start, out _, out int startBand) ||
                !BandCompatibility.TryLocate(map, goal.Cell, out _, out int goalBand) || startBand == goalBand)
                return false;
            var topology = DoorsFor(map, component);
            if (!topology.ByFloor.TryGetValue(startBand, out var entries) ||
                !topology.ByFloor.TryGetValue(goalBand, out var exits) || !NativeAccess(pawn) || !TryReadJumpCost(out _))
                return false;

            var context = new[] { access.Invoke(new object[] { pawn, false }) };
            planning = true;
            try
            {
                bool foundEntry = false;
                foreach (var comp in entries)
                    if (comp.parent is Building entry && UsableCell(map, entry, pawn, parms.maxDanger) &&
                        (bool)canEnter.Invoke(comp, context) && WalkableLeg(map, start, entry, PathEndMode.OnCell, parms))
                    {
                        foundEntry = true;
                        break;
                    }
                if (!foundEntry)
                    return false;
                foreach (var comp in exits)
                    if (comp.parent is Building exit && UsableCell(map, exit, pawn, parms.maxDanger) &&
                        ExitClear(exit, pawn) && (bool)canExit.Invoke(comp, context) &&
                        WalkableLeg(map, exit.Position, goal, mode, parms))
                        return true;
                return false;
            }
            finally { planning = false; }
        }

        private static float LegCost(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms) =>
            start == goal.Cell && mode == PathEndMode.OnCell || map.reachability.CanReach(start, goal, mode, parms)
                ? ExactWalkCost(map, start, goal, mode, parms) : float.PositiveInfinity;

        private static bool WalkableLeg(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms) =>
            float.IsFinite(LegCost(map, start, goal, mode, parms));

        private static void FindCrossFloorWork(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode,
            TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool lookInHaulSources,
            ref Thing __result)
        {
            if (__result != null || planning || !UnityData.IsInMainThread || lookInHaulSources ||
                traverseParams.mode != TraverseMode.ByPawn || traversableRegionTypes != RegionType.Set_Passable ||
                (searchRegionsMax >= 0 && !forceAllowGlobalSearch) ||
                !EligiblePawn(traverseParams.pawn, map) || !root.InBounds(map))
                return;
            var component = HostFor(map);
            if (component == null)
                return;
            var candidates = customGlobalSearchSet ?? (thingReq.IsUndefined ? null : map.listerThings.ThingsMatching(thingReq));
            if (candidates == null || candidates is ICollection<Thing> { Count: 0 } ||
                !BandCompatibility.TryLocate(map, root, out _, out int startBand) ||
                !CanLeaveFloor(map, root, startBand, traverseParams, DoorsFor(map, component)))
                return;
            var pawn = traverseParams.pawn;
            bool checkCargoAccess = cargoDrone != null && pawn.def == cargoDrone;
            __result = GenClosest.ClosestThing_Global_Reachable(root, map, candidates, peMode, traverseParams, maxDistance,
                thing => thing.Spawned && thing.Map == map &&
                    BandCompatibility.IsOnOtherFloor(map, startBand, thing.Position) &&
                    (!checkCargoAccess || !thing.IsForbidden(pawn) && !thing.Position.IsForbidden(pawn)) &&
                    (validator?.Invoke(thing) ?? true), null, false);
        }

        private static bool StartPath(Pawn ___pawn, ref LocalTargetInfo __0, ref PathEndMode __1)
        {
            if (planning || ___pawn == null)
                return true;
            var pawn = ___pawn;
            var job = pawn.CurJob;
            var map = pawn.Map;
            trips.TryGetValue(pawn, out var trip);
            if (trip?.Next != null && trip.StairApproach && trip.IsCurrent(pawn) &&
                !__0.HasThing && __0.Cell == trip.Approach && __1 == PathEndMode.OnCell)
                return true;
            if (trip != null && (!trip.IsCurrent(pawn) || trip.Destination != __0 || trip.Mode != __1))
            {
                trips.Remove(pawn);
                trip = null;
            }
            if (job == null || !Eligible(pawn, map, pawn.Position, __0))
            {
                trips.Remove(pawn);
                return true;
            }
            if (trip?.Next != null)
            {
                if (trip.Charging)
                    return false;
                __0 = trip.Approach;
                __1 = PathEndMode.OnCell;
                return true;
            }
            var parms = TraverseParms.For(pawn, pawn.NormalMaxDanger());
            var route = Plan(map, pawn.Position, __0, __1, parms, trip);
            if (!route.HasValue)
            {
                if (traces.TryGetValue(pawn, out _)) Note(pawn, "No executable Skipdoor route to " + __0);
                trips.Remove(pawn);
                return true;
            }
            float native = NativeCost(map, pawn.Position, __0, __1, parms);
            float cost = route.Value.Cost;
            if (traces.TryGetValue(pawn, out _)) Note(pawn, $"Toil {pawn.jobs.curDriver?.CurToilIndex}: {__0}; stairs {native:0.##}; Skipdoors {cost:0.##}");
            if (native <= 0f || cost >= native * WorthItFactor())
            {
                trips.Remove(pawn);
                Note(pawn, "Kept the native route");
                return true;
            }

            trip ??= trips.GetOrCreateValue(pawn);
            trip.Original = job;
            trip.JobLoadId = job.loadID;
            trip.ToilIndex = pawn.jobs.curDriver?.CurToilIndex ?? -1;
            trip.Destination = __0;
            trip.Mode = __1;
            trip.Next = route.Value.First;
            trip.Charging = false;
            trip.Approach = trip.Next.Entry.Position;
            trip.StairApproach = !BandCompatibility.SameFloor(map, pawn.Position, trip.Approach);
            clearStairs(pawn);
            __0 = trip.Approach;
            __1 = PathEndMode.OnCell;
            if (traces.TryGetValue(pawn, out _)) Note(pawn, $"Approach {trip.Approach} for exit {trip.Next.Exit.Position}");
            return true;
        }

        private static bool KeepApproachLocal(Pawn __0, PathRequest __1, ref PathRequest __2, ref bool __result)
        {
            if (__0 == null || __1 == null || !trips.TryGetValue(__0, out var trip) || trip.Next == null ||
                !trip.IsCurrent(__0) || __1.Target.Cell != trip.Approach)
                return true;
            __2 = null;
            __result = false;
            return false;
        }

        private static bool ArriveAtDoor(Pawn_PathFollower __instance, Pawn ___pawn, ref bool ___moving) =>
            FinishApproach(__instance, ___pawn, true, ref ___moving);

        private static bool ApproachFailed(Pawn_PathFollower __instance, Pawn ___pawn, ref bool ___moving) =>
            FinishApproach(__instance, ___pawn, false, ref ___moving);

        private static bool TickCharge(Pawn_PathFollower __instance, Pawn ___pawn, ref bool ___moving)
        {
            if (!trips.TryGetValue(___pawn, out var trip) || !trip.Charging)
                return true;
            if (!___moving || !trip.IsCurrent(___pawn))
            {
                trips.Remove(___pawn);
                __instance.StopDead();
                return true;
            }
            if (GenTicks.TicksGame >= trip.NextPoll)
                FinishApproach(__instance, ___pawn, true, ref ___moving);
            return false;
        }

        private static bool FinishApproach(Pawn_PathFollower path, Pawn pawn, bool arrived, ref bool moving)
        {
            if (!trips.TryGetValue(pawn, out var trip) || trip.Next == null || path.Destination.Cell != trip.Approach)
                return true;
            if (!trip.IsCurrent(pawn))
            {
                trips.Remove(pawn);
                path.StopDead();
                return false;
            }
            var hop = trip.Next;
            var map = pawn.Map;
            var danger = pawn.NormalMaxDanger();
            bool canCross = false;
            var mount = giddyActive ? GiddyUpSkipdoors.MountOf(pawn, respectSetting: false) : null;
            if (arrived && pawn.Position == trip.Approach &&
                Eligible(pawn, map, pawn.Position, trip.Destination) && NativeAccess(pawn) &&
                UsableCell(map, hop.Entry, pawn, danger) && UsableCell(map, hop.Exit, pawn, danger) && ExitClear(hop.Exit, pawn) &&
                (mount == null || MountCanUse(pawn, mount, hop.Entry, hop.Exit)))
            {
                var context = new[] { access.Invoke(new object[] { pawn, false }) };
                var entry = PortalComp(hop.Entry);
                var exit = PortalComp(hop.Exit);
                if (entry != null && exit != null && (bool)canEnter.Invoke(entry, context) && (bool)canExit.Invoke(exit, context))
                {
                    if (!trip.Charging)
                    {
                        notifyArrived.Invoke(entry, new[] { pawn, entryRole });
                        notifyArrived.Invoke(exit, new[] { pawn, exitRole });
                    }
                    var enterArgs = new object[] { pawn, 0 };
                    var exitArgs = new object[] { pawn, 0 };
                    bool entryReady = (bool)enterNow.Invoke(entry, enterArgs);
                    bool exitReady = (bool)exitNow.Invoke(exit, exitArgs);
                    canCross = entryReady && exitReady;
                    int delay = Math.Max((int)enterArgs[1], (int)exitArgs[1]);
                    if (!canCross && delay > 0 && delay < int.MaxValue && trip.IsCurrent(pawn))
                    {
                        // The driver still owns a moving path. Only this waypoint waits.
                        trip.Charging = true;
                        trip.NextPoll = (int)Math.Min(int.MaxValue, (long)GenTicks.TicksGame + delay);
                        moving = true;
                        if (traces.TryGetValue(pawn, out _)) Note(pawn, $"Door charge: {delay} ticks");
                        return false;
                    }
                }
            }

            trip.Next = null;
            trip.Charging = false;
            path.StopDead();
            trip.Tried.Add((hop.Entry, hop.Exit));
            if (canCross && trip.IsCurrent(pawn))
            {
                // Run only the native physical step on a detached driver.
                var physicalJob = new Job(skipJob, hop.Entry, hop.Exit);
                skipPhysical(physicalJob.MakeDriver(pawn));
                if (mount != null && pawn.Position == hop.Exit.Position)
                    GiddyUpSkipdoors.CompleteCrossing(pawn, mount);
                if (traces.TryGetValue(pawn, out _)) Note(pawn, $"Crossed {trip.Approach} → {pawn.Position}");
            }
            else
                Note(pawn, "Door access changed or the approach failed; replanning");

            if (trip.IsCurrent(pawn))
                path.StartPath(trip.Destination, trip.Mode);
            else
                trips.Remove(pawn);
            return false;
        }

        private static ThingComp PortalComp(Building door)
        {
            foreach (var comp in door.AllComps)
                if (doorType.IsInstanceOfType(comp))
                    return comp;
            return null;
        }

        private static void RestoreTaskTargetInStairSave(IList __result)
        {
            if (__result == null)
                return;
            foreach (var record in __result)
                if (transitPawn.GetValue(record) is Pawn pawn && trips.TryGetValue(pawn, out var trip) &&
                    trip.Next != null && trip.StairApproach && trip.IsCurrent(pawn) &&
                    transitDestination.GetValue(record) is LocalTargetInfo target &&
                    !target.HasThing && target.Cell == trip.Approach)
                {
                    // The native transit record is a snapshot. The live phase still aims
                    // for the portal, while a reload resumes planning for the real toil.
                    transitDestination.SetValue(record, trip.Destination);
                    transitMode.SetValue(record, trip.Mode);
                }
        }

        private static void BeforePathSave(Pawn ___pawn, bool ___moving, ref LocalTargetInfo ___destination,
            ref PathEndMode ___peMode, out SaveState __state)
        {
            __state = default;
            if (Scribe.mode != LoadSaveMode.Saving || !___moving || !trips.TryGetValue(___pawn, out var trip) ||
                trip.Next == null || !trip.IsCurrent(___pawn) || (!trip.StairApproach && ___destination.Cell != trip.Approach))
                return;
            __state = new SaveState(true, ___destination, ___peMode);
            ___destination = trip.Destination;
            ___peMode = trip.Mode;
        }

        private static void AfterPathSave(ref LocalTargetInfo ___destination, ref PathEndMode ___peMode, SaveState __state)
        {
            if (!__state.Changed)
                return;
            ___destination = __state.Target;
            ___peMode = __state.Mode;
        }

        private static bool TryReadJumpCost(out float cost)
        {
            cost = 0;
            if (options.GetValue(null) is not object settings ||
                skipCost.GetValue(settings) is not float configured || !float.IsFinite(configured))
                return false;
            cost = Math.Max(1f, configured);
            return true;
        }

        private static float WorthItFactor()
        {
            var settings = options.GetValue(null);
            return settings != null && worthItFactor?.GetValue(settings) is float factor &&
                float.IsFinite(factor) && factor > 0 ? factor : 1f;
        }

        private static float NativeCost(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms)
        {
            bool alreadyPlanning = planning;
            planning = true;
            try
            {
                if (!map.reachability.CanReach(start, goal, mode, parms))
                    return float.PositiveInfinity;
                var memo = MemoFor(map, parms.pawn);
                var key = new WalkKey(start, goal.Cell, goal.Thing, mode, parms, Stairs: true);
                if (memo.Costs.TryGetValue(key, out float cached))
                    return cached;
                float cost = EstimateStairChain(map, start, goal, mode, parms);
                if (memo.Costs.Count < 512)
                    memo.Costs.Add(key, cost);
                return cost;
            }
            finally { planning = alreadyPlanning; }
        }

        private static float StairLegCost(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms)
        {
            float cost = NativeCost(map, start, goal, mode, parms);
            return cost > 0f ? cost : float.PositiveInfinity;
        }

        private static float EstimateStairChain(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms)
        {
            // Use the stair chain selected by AASB2, not the packed-map distance.
            float cost = 0;
            var visited = new HashSet<IntVec3>();
            var original = start;
            while (!BandCompatibility.SameFloor(map, start, goal.Cell))
            {
                if (!visited.Add(start) || !nativeTransit(map, start, goal.Cell, parms.pawn, out var entry, out var exit, goal) ||
                    entry == null || exit == null)
                    return 0;
                if (!BandCompatibility.SameFloor(map, start, entry.Position))
                    return 0;
                float leg = ExactWalkCost(map, start, entry, PathEndMode.OnCell, parms);
                if (!float.IsFinite(leg))
                    return 0f;
                // AASB2 computes crossing time at commit. Charge at least one tick per stair.
                cost += leg + 1f;
                start = exit.Position;
            }
            float last = ExactWalkCost(map, start, goal, mode, parms);
            return start == original || !float.IsFinite(last) ? 0f : cost + last;
        }

        private static WalkMemo MemoFor(Map map, Pawn pawn)
        {
            var topology = revisions.GetOrCreateValue(map.reachability);
            var memo = topology.Walks.GetOrCreateValue(pawn);
            int tick = GenTicks.TicksGame, revision = System.Threading.Volatile.Read(ref topology.Value);
            if (memo.Tick != tick || memo.Revision != revision)
            {
                memo.Costs.Clear();
                memo.Tick = tick;
                memo.Revision = revision;
            }
            return memo;
        }

        private static float ExactWalkCost(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms)
        {
            if (mode == PathEndMode.OnCell && start == goal.Cell)
                return 0f;
            var memo = MemoFor(map, parms.pawn);
            var key = new WalkKey(start, goal.Cell, goal.Thing, mode, parms);
            if (memo.Costs.TryGetValue(key, out float recorded))
                return recorded;
            using var path = map.pathFinder.FindPathNow(start, goal, parms, null, mode, null);
            float cost = path?.Found == true ? path.TotalCost : float.PositiveInfinity;
            if (memo.Costs.Count < 512)
                memo.Costs.Add(key, cost);
            return cost;
        }

        private static Route? Plan(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms, Trip attempted)
        {
            var pawn = parms.pawn;
            var component = HostFor(map);
            if (component == null)
                return null;
            var topology = DoorsFor(map, component);
            if (topology.ByFloor.Count < 2 ||
                !BandCompatibility.TryLocate(map, start, out _, out int startBand) ||
                (!mixedRoutes && !topology.ByFloor.ContainsKey(startBand)) ||
                !BandCompatibility.TryLocate(map, goal.Cell, out _, out int goalBand) ||
                manager.GetValue(component) is not object native ||
                !(bool)checkNativeAccess(native, pawn) ||
                !TryReadJumpCost(out float jump))
                return null;

            var context = new[] { access.Invoke(new object[] { pawn, false }) };
            bool trace = traces.TryGetValue(pawn, out _);
            var mount = giddyActive ? GiddyUpSkipdoors.MountOf(pawn) : null;
            if (mount != null && (!mount.Spawned || mount.Map != map || mount.Downed ||
                !GiddyUpSkipdoors.IsFollowing(pawn, mount) || !NativeAccess(mount)))
                return null;
            var mountAccess = mount == null ? null : new[] { access.Invoke(new object[] { mount, false }) };
            var candidates = routeDoors ??= new List<Door>(topology.Doors.Length);
            planning = true;
            try
            {
                foreach (var comp in topology.Doors)
                {
                    if (comp.parent is not Building door || !UsableCell(map, door, pawn, parms.maxDanger) ||
                        !BandCompatibility.TryLocate(map, door.Position, out _, out int band))
                    {
                        if (trace) Note(pawn, $"Ignored door {comp.parent.Position}: unavailable, forbidden, reserved, dangerous or outside a floor");
                        continue;
                    }
                    bool entry = (bool)canEnter.Invoke(comp, context);
                    bool exit = (bool)canExit.Invoke(comp, context) && ExitClear(door, pawn);
                    if (mount != null)
                    {
                        entry &= band == startBand && Authorize(door, mount, canEnter, mountAccess);
                        exit &= band == goalBand && Authorize(door, mount, canExit, mountAccess);
                    }
                    if (trace) Note(pawn, $"Considered {door.Position}; entry allowed={entry}, exit allowed and clear={exit}");
                    if (entry || exit)
                        candidates.Add(new Door(door, band, entry, exit));
                }
                if (candidates.Count < 2)
                    return null;
                // If every boarding door is on this floor and every exit is off
                // it, a second portal hop is impossible. All pairs have the same jump
                // cost, so find the best two walking legs independently.
                bool oneDepartureFloor = attempted == null || attempted.Tried.Count == 0;
                foreach (var door in candidates)
                    if (door.Enter && door.Band != startBand || door.Exit && door.Band == startBand)
                    {
                        oneDepartureFloor = false;
                        break;
                    }
                if (oneDepartureFloor)
                    return BestSingleHop(map, start, goal, mode, parms, goalBand, candidates, jump);
                bool mixed = mixedRoutes && mount == null;
                var route = SkipRouteSearch.Find(candidates.Count, i => candidates[i].Band,
                    i => candidates[i].Enter, i => candidates[i].Exit,
                    (from, to) =>
                    {
                        var cell = from < 0 ? start : candidates[from].Building.Position;
                        var door = candidates[to];
                        int band = from < 0 ? startBand : candidates[from].Band;
                        return band == door.Band
                            ? LegCost(map, cell, door.Building, PathEndMode.OnCell, parms)
                            : mixed ? StairLegCost(map, cell, door.Building.Position, PathEndMode.OnCell, parms)
                                    : float.PositiveInfinity;
                    },
                    from =>
                    {
                        var cell = candidates[from].Building.Position;
                        if (candidates[from].Band != goalBand)
                        {
                            float stairCost = NativeCost(map, cell, goal, mode, parms);
                            return stairCost > 0 ? stairCost : float.PositiveInfinity;
                        }
                        return map.reachability.CanReach(cell, goal, mode, parms)
                            ? ExactWalkCost(map, cell, goal, mode, parms) : float.PositiveInfinity;
                    },
                    (from, to) => attempted?.Tried.Contains((candidates[from].Building, candidates[to].Building)) != true,
                    jump, allowStairs: mixed);
                if (route is not SkipRouteSearch.Route selected || selected.Steps.Count == 0)
                    return null;
                if (trace)
                    foreach (var step in selected.Steps)
                        Note(pawn, $"Candidate {candidates[step.Entry].Building.Position} → {candidates[step.Exit].Building.Position}");
                var first = selected.Steps[0];
                return new Route(new Hop(candidates[first.Entry].Building, candidates[first.Exit].Building), selected.Cost);
            }
            finally
            {
                candidates.Clear();
                planning = false;
            }
        }

        private static Route? BestSingleHop(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode,
            TraverseParms parms, int goalBand, List<Door> candidates, float jump)
        {
            Building first = null, last = null;
            float approach = float.PositiveInfinity, finish = float.PositiveInfinity;
            foreach (var door in candidates)
            {
                if (door.Enter)
                {
                    float cost = LegCost(map, start, door.Building, PathEndMode.OnCell, parms);
                    if (cost < approach)
                    {
                        approach = cost;
                        first = door.Building;
                    }
                }
                if (!door.Exit)
                    continue;
                float suffix = door.Band == goalBand
                    ? LegCost(map, door.Building.Position, goal, mode, parms)
                    : NativeCost(map, door.Building.Position, goal, mode, parms);
                if (suffix < finish && (door.Band == goalBand || suffix > 0))
                {
                    finish = suffix;
                    last = door.Building;
                }
            }
            return first == null || last == null ? null : new Route(new Hop(first, last), approach + jump + finish);
        }

        private static DoorIndex BuildIndex(Map map, MapComponent component)
        {
            var result = new List<ThingComp>();
            if (doors.GetValue(component) is IEnumerable registered)
                foreach (var item in registered)
                    if (item is ThingComp comp && doorType.IsInstanceOfType(comp))
                        result.Add(comp);
            result.Sort((first, second) => first.parent.thingIDNumber.CompareTo(second.parent.thingIDNumber));
            var byFloor = new Dictionary<int, List<ThingComp>>();
            foreach (var comp in result)
            {
                if (!BandCompatibility.TryLocate(map, comp.parent.Position, out _, out int floor))
                    continue;
                if (!byFloor.TryGetValue(floor, out var bandDoors))
                    byFloor.Add(floor, bandDoors = new List<ThingComp>());
                bandDoors.Add(comp);
            }
            return new DoorIndex { Doors = result.ToArray(), ByFloor = byFloor };
        }

        private static bool UsableCell(Map map, Building door, Pawn pawn, Danger danger) =>
            door.Spawned && door.Map == map && !door.IsForbidden(pawn) && !door.Position.IsForbidden(pawn) &&
            door.Position.GetDangerFor(pawn, map) <= danger && pawn.CanReserve(door);

        private static bool ExitClear(Thing door, Pawn pawn) =>
            door.Position.Standable(door.Map) &&
            (door.Position.GetFirstPawn(door.Map) is not Pawn occupant || occupant == pawn);

        private static void CheckMountedNativeAccess(Pawn ___pawn, Job ___job, ref bool __result)
        {
            if (!__result || CompatibilityStatus.FloorSettings?.EnableSkiptech == false ||
                ___pawn == null || ___job == null || !BandCompatibility.IsBanded(___pawn.Map))
                return;
            if (___job.GetTarget(TargetIndex.A).Thing is Building entry &&
                ___job.GetTarget(TargetIndex.B).Thing is Building exit &&
                !BandCompatibility.SameFloor(___pawn.Map, entry.Position, exit.Position))
            {
                var mount = GiddyUpSkipdoors.MountOf(___pawn);
                __result = mount == null || MountCanUse(___pawn, mount, entry, exit);
            }
        }

        // Only the native seam moves ropees. The local skip job moves its actor alone.
        private static bool LeadsAnimals(Pawn pawn) => pawn?.roping?.Ropees?.Count > 0;

        private static bool MountCanUse(Pawn rider, Pawn mount, Building entry, Building exit)
        {
            if (CompatibilityStatus.FloorSettings?.ProtectMountedSkipdoors == false ||
                !mount.Spawned || mount.Map != rider.Map || mount.Downed || !NativeAccess(mount) ||
                !GiddyUpSkipdoors.IsFollowing(rider, mount) || !BandCompatibility.SameFloor(rider.Map, rider.Position, mount.Position) ||
                Math.Abs(mount.Position.x - rider.Position.x) > 1 || Math.Abs(mount.Position.z - rider.Position.z) > 1 ||
                !exit.Position.Standable(mount.Map))
                return false;
            var context = new[] { access.Invoke(new object[] { mount, false }) };
            return Authorize(entry, mount, canEnter, context) && Authorize(exit, mount, canExit, context);
        }

        private static bool NativeAccess(Pawn pawn)
        {
            var component = HostFor(pawn.Map);
            return component != null && manager.GetValue(component) is object native &&
                (bool)checkNativeAccess(native, pawn);
        }

        private static void Note(Pawn pawn, string line)
        {
            if (!traces.TryGetValue(pawn, out var trace))
                return;
            if (trace.Lines.Count < 80)
                trace.Lines.Add(line);
        }

        internal static void ToggleNavigationTrace(Pawn pawn)
        {
            if (!Ready || pawn?.Spawned != true)
                return;
            if (!traces.TryGetValue(pawn, out var trace))
            {
                traces.GetOrCreateValue(pawn);
                Messages.Message("AASBExt.Diagnostics.NavigationTraceStarted".Translate(), MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            traces.Remove(pawn);
            var job = pawn.CurJob;
            var target = trips.TryGetValue(pawn, out var trip) ? trip.Destination : pawn.pather.Destination;
            var output = new StringBuilder("Skiptech navigation")
                .AppendLine().Append("Pawn: ").Append(pawn.def.defName).Append(" / ").Append(pawn.kindDef?.defName)
                .AppendLine().Append("Job: ").Append(job?.def.defName).Append(" driver=").Append(pawn.jobs.curDriver?.GetType().FullName)
                .Append(" toil=").Append(pawn.jobs.curDriver?.CurToilIndex)
                .AppendLine().Append("Carried: ").Append(pawn.carryTracker?.CarriedThing?.ThingID)
                .Append(" inventory stacks=").Append(pawn.inventory?.innerContainer.Count)
                .AppendLine().Append("Cell: ").Append(pawn.Position).Append(" target: ").Append(target)
                .AppendLine().AppendLine();
            foreach (string line in trace.Lines)
                output.AppendLine(line);
            DiagnosticReports.Write("skiptech-navigation", output.ToString());
        }

        private static bool Authorize(Building door, Pawn pawn, MethodInfo check, object[] context)
        {
            if (!door.Spawned || door.Map != pawn.Map || door.IsForbidden(pawn) || door.Position.IsForbidden(pawn))
                return false;
            var comp = PortalComp(door);
            return comp != null && (bool)check.Invoke(comp, context);
        }
    }
}
