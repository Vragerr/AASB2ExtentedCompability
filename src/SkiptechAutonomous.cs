using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
        internal static bool NativeHaulingReady { get; private set; }
        internal const string Package = "migcorp.skiptech";
        private static Type componentType, doorType;
        private static FieldInfo doors, manager, skipCost, worthItFactor, options, spliceRequest, splicePawn;
        private static readonly ConditionalWeakTable<object, Hop> nativeSplices = new();
        private static MethodInfo canUse, canEnter, canExit;
        private static ConstructorInfo access;
        private static JobDef skipJob;
        private delegate bool NativeTransit(Map map, IntVec3 start, IntVec3 goal, Pawn pawn,
            out Building_Door entry, out Building_Door exit, LocalTargetInfo destination);
        private static NativeTransit nativeTransit;
        private static readonly ConditionalWeakTable<MapComponent, DoorIndex> index = new();
        private static readonly ConditionalWeakTable<Pawn, Trip> trips = new();
        [ThreadStatic] private static bool planning, replaying;

        private sealed class DoorIndex
        {
            internal ThingComp[] Doors;
        }

        private sealed class Trip
        {
            internal Job Original;
            internal LocalTargetInfo Destination;
            internal PathEndMode Mode;
            internal bool Pending;
            internal readonly HashSet<(Building Entry, Building Exit)> Tried = new();
        }

        private readonly record struct Door(Building Building, int Band, bool Enter, bool Exit);
        private sealed record Hop(Building Entry, Building Exit);
        private readonly record struct Route(List<Hop> Hops, float JumpCost)
        {
            internal Building Entry => Hops[0].Entry;
            internal Building Exit => Hops[0].Exit;
        }

        static SkiptechAutonomous()
        {
            if (!ModsConfig.IsActive(Package) || !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
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
                if (componentType == null || doorType == null || context == null || driver == null || skipJob?.driverClass != driver ||
                    !typeof(ThingComp).IsAssignableFrom(doorType))
                    throw new InvalidOperationException("Skiptech component or local job unavailable.");

                doors = AccessTools.DeclaredField(componentType, "skipdoors");
                manager = AccessTools.DeclaredField(componentType, "manager");
                options = mod == null ? null : AccessTools.DeclaredField(mod, "Settings");
                skipCost = options == null ? null : AccessTools.DeclaredField(options.FieldType, "skipCost");
                worthItFactor = options == null ? null : AccessTools.DeclaredField(options.FieldType, "worthItFactor");
                canUse = manager == null ? null : AccessTools.DeclaredMethod(manager.FieldType, "PawnCanUseSkipNet", new[] { typeof(Pawn) });
                access = AccessTools.DeclaredConstructor(context, new[] { typeof(Pawn), typeof(bool) });
                canEnter = AccessTools.DeclaredMethod(doorType, "IsEnterableBy", new[] { context.MakeByRefType() });
                canExit = AccessTools.DeclaredMethod(doorType, "IsExitableBy", new[] { context.MakeByRefType() });
                var reach = AccessTools.DeclaredMethod(typeof(Reachability), nameof(Reachability.CanReach),
                    new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });
                var closest = AccessTools.DeclaredMethod(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable),
                    new[] { typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(IEnumerable<Thing>), typeof(int), typeof(int),
                        typeof(bool), typeof(RegionType), typeof(bool), typeof(bool) });
                var start = AccessTools.DeclaredMethod(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath),
                    new[] { typeof(LocalTargetInfo), typeof(PathEndMode) });
                var tick = AccessTools.DeclaredMethod(typeof(Pawn_JobTracker), "JobTrackerTickInterval", new[] { typeof(int) });
                var commit = AccessTools.DeclaredMethod(driver, "CanStillSkip", Type.EmptyTypes);
                var register = AccessTools.DeclaredMethod(componentType, "RegisterSkipdoor", new[] { doorType });
                var unregister = AccessTools.DeclaredMethod(componentType, "UnregisterSkipdoor", new[] { doorType });
                var wormhole = AccessTools.TypeByName("AsAboveSoBelow.ABWormhole");
                var transit = wormhole == null ? null : AccessTools.DeclaredMethod(wormhole, "TryGetTransit",
                    new[] { typeof(Map), typeof(IntVec3), typeof(IntVec3), typeof(Pawn),
                        typeof(Building_Door).MakeByRefType(), typeof(Building_Door).MakeByRefType(), typeof(LocalTargetInfo) });
                if (reach?.ReturnType != typeof(bool) || AccessTools.Field(typeof(Reachability), "map")?.FieldType != typeof(Map) ||
                    closest?.ReturnType != typeof(Thing) || start?.ReturnType != typeof(void) || tick?.ReturnType != typeof(void) ||
                    commit?.ReturnType != typeof(bool) || register?.ReturnType != typeof(void) || unregister?.ReturnType != typeof(void) ||
                    transit?.ReturnType != typeof(bool) || !transit.IsStatic ||
                    !typeof(IEnumerable).IsAssignableFrom(doors?.FieldType) || canUse?.ReturnType != typeof(bool) ||
                    access == null || canEnter?.ReturnType != typeof(bool) || canExit?.ReturnType != typeof(bool) ||
                    options?.IsStatic != true || skipCost?.FieldType != typeof(float))
                    throw new InvalidOperationException("Skiptech navigation contract changed.");

                nativeTransit = transit.CreateDelegate<NativeTransit>();
                harmony.Patch(reach, postfix: Hook(nameof(ExtendReach), Priority.Last));
                harmony.Patch(closest, postfix: Hook(nameof(FindCrossFloorWork), Priority.Last));
                harmony.Patch(start, prefix: Hook(nameof(StartPath), Priority.First));
                harmony.Patch(tick, prefix: Hook(nameof(CommitPending)));
                harmony.Patch(commit, postfix: Hook(nameof(CheckAutonomousAccess)));
                harmony.Patch(register, postfix: Hook(nameof(InvalidateDoors)));
                harmony.Patch(unregister, postfix: Hook(nameof(InvalidateDoors)));
                foreach (var method in new[] { reach, closest, start, tick, commit, register, unregister })
                    if (!IntegrationStatus.Registered(method, harmony.Id))
                        throw new InvalidOperationException("Skiptech navigation patch did not register: " + method.Name);
                Ready = true;
                NativeHaulingReady = InstallNativeHauling(componentType.Assembly, ns);
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Skiptech autonomous", "Reachability / StartPath / native job", error);
                Log.Warning("[AASB2 Extended] Skiptech navigation unavailable: " + error.Message);
            }
        }

        private static bool InstallNativeHauling(Assembly assembly, string nameSpace)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".skiptech-hauling");
            try
            {
                var planType = assembly.GetType(nameSpace + "SkipNetPlan");
                var finder = assembly.GetType(nameSpace + "SkipNetCandidateFinder");
                var splicer = assembly.GetType(nameSpace + "SkipNetPathSplicer");
                // Older Skiptech versions can keep using the suspendable-job adapter.
                if (planType == null || finder == null || splicer == null)
                    return false;
                spliceRequest = AccessTools.DeclaredField(planType, "originalPathRequest");
                splicePawn = AccessTools.DeclaredField(planType, "pawn");
                var commit = AccessTools.DeclaredMethod(planType, "IsCandidatePairStillAccessible", Type.EmptyTypes);
                var pair = AccessTools.DeclaredMethod(finder, "TryFindSkipdoorPair",
                    new[] { planType, typeof(PawnPath), doorType.MakeByRefType(), doorType.MakeByRefType(), typeof(int).MakeByRefType() });
                var worth = AccessTools.DeclaredMethod(splicer, "IsWorthSplicing",
                    new[] { typeof(Pawn), planType, typeof(PawnPath), typeof(PawnPath) });
                if (spliceRequest?.FieldType.FullName != "Verse.PathRequest" ||
                    pair?.ReturnType != typeof(bool) || worth?.ReturnType != typeof(bool) ||
                    commit?.ReturnType != typeof(bool) || splicePawn?.FieldType != typeof(Pawn))
                    throw new InvalidOperationException("Skiptech path splice contract changed.");
                harmony.Patch(pair, prefix: Hook(nameof(SelectHaulingDoors)));
                harmony.Patch(worth, postfix: Hook(nameof(AllowHaulingSplice)));
                harmony.Patch(commit, postfix: Hook(nameof(RecheckHaulingSplice)));
                if (!IntegrationStatus.Registered(pair, harmony.Id) || !IntegrationStatus.Registered(worth, harmony.Id) ||
                    !IntegrationStatus.Registered(commit, harmony.Id))
                    throw new InvalidOperationException("Native carrying path hooks did not register.");
                return true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Skiptech hauling", "native path splice", error);
                Log.Warning("[AASB2 Extended] Skiptech hauling splice unavailable: " + error.GetBaseException().Message);
                return false;
            }
        }

        private static bool SelectHaulingDoors(object __0, ref object __2, ref object __3, ref int __4, ref bool __result)
        {
            if (spliceRequest.GetValue(__0) is not PathRequest request)
                return true;
            var pawn = request.TraverseParms.pawn;
            var map = pawn?.Map;
            if (planning || replaying || pawn?.CurJob == null ||
                (pawn.CurJob.def.suspendable && !LeadsAnimals(pawn)) ||
                !Eligible(pawn, map, request.Start, request.Target))
                return true;
            nativeSplices.Remove(__0);
            if (!PreferSkip(map, request.Start, request.Target, request.EndMode, request.TraverseParms, null, out var route) ||
                route.Hops.Count != 1)
                return true;

            var entry = route.Entry.AllComps.Find(comp => doorType.IsInstanceOfType(comp));
            var exit = route.Exit.AllComps.Find(comp => doorType.IsInstanceOfType(comp));
            if (entry == null || exit == null)
                return true;
            nativeSplices.Add(__0, route.Hops[0]);
            __2 = entry;
            __3 = exit;
            __4 = 1;
            __result = true;
            return false;
        }

        private static void AllowHaulingSplice(Pawn __0, object __1, PawnPath __2, PawnPath __3, ref bool __result)
        {
            if (!nativeSplices.TryGetValue(__1, out var hop))
                return;
            __result = __2?.Found == true && __3?.Found == true && ValidSplice(__0, hop);
        }

        private static void RecheckHaulingSplice(object __instance, ref bool __result)
        {
            if (__result && nativeSplices.TryGetValue(__instance, out var hop))
                __result = ValidSplice((Pawn)splicePawn.GetValue(__instance), hop);
        }

        private static bool ValidSplice(Pawn pawn, Hop hop) =>
            CompatibilityStatus.FloorSettings?.EnableSkiptech != false && pawn?.Spawned == true &&
            hop.Entry.Spawned && hop.Exit.Spawned && hop.Entry.Map == pawn.Map && hop.Exit.Map == pawn.Map &&
            NativeAccess(pawn) && ExitClear(hop.Exit, pawn) && MountCanUse(pawn, hop.Entry, hop.Exit);

        private static HarmonyMethod Hook(string name, int priority = Priority.Normal) =>
            new HarmonyMethod(typeof(SkiptechAutonomous), name) { priority = priority };

        private static void InvalidateDoors(MapComponent __instance) => index.Remove(__instance);

        private static bool Eligible(Pawn pawn, Map map, IntVec3 start, LocalTargetInfo goal)
        {
            return CompatibilityStatus.FloorSettings?.EnableSkiptech != false && pawn != null && pawn.Spawned &&
                (pawn.IsColonist || pawn.IsAnimal) && !pawn.Downed && pawn.Map == map &&
                pawn.CurJob?.def != skipJob && BandCompatibility.IsBanded(map) &&
                start.InBounds(map) && goal.IsValid && goal.Cell.InBounds(map) &&
                (!goal.HasThing || goal.Thing.Spawned && goal.Thing.Map == map) &&
                !BandCompatibility.SameFloor(map, start, goal.Cell);
        }

        private static void ExtendReach(Map ___map, IntVec3 __0, LocalTargetInfo __1, PathEndMode __2,
            TraverseParms __3, ref bool __result)
        {
            if (__result || planning || replaying || !UnityData.IsInMainThread ||
                __3.mode != TraverseMode.ByPawn || !Eligible(__3.pawn, ___map, __0, __1))
                return;
            __result = Plan(___map, __0, __1, __2, __3, null).HasValue;
        }

        private static void FindCrossFloorWork(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode,
            TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMax, bool forceAllowGlobalSearch, RegionType traversableRegionTypes, bool lookInHaulSources,
            ref Thing __result)
        {
            var pawn = traverseParams.pawn;
            if (__result != null || planning || replaying || !UnityData.IsInMainThread ||
                CompatibilityStatus.FloorSettings?.EnableSkiptech == false ||
                pawn == null || !pawn.Spawned || !(pawn.IsColonist || pawn.IsAnimal) || pawn.Downed ||
                pawn.Map != map || pawn.CurJob?.def == skipJob || traverseParams.mode != TraverseMode.ByPawn ||
                !BandCompatibility.IsBanded(map) || !root.InBounds(map) ||
                (searchRegionsMax >= 0 && !forceAllowGlobalSearch) || traversableRegionTypes != RegionType.Set_Passable)
                return;

            var component = map.GetComponent(componentType);
            if (component == null || index.GetValue(component, BuildIndex).Doors.Length < 2)
                return;
            var candidates = customGlobalSearchSet;
            if (candidates == null)
            {
                if (thingReq.IsUndefined)
                    return;
                candidates = map.listerThings.ThingsMatching(thingReq);
            }

            __result = GenClosest.ClosestThing_Global_Reachable(root, map, candidates, peMode, traverseParams,
                maxDistance, thing => Eligible(pawn, map, root, thing) && (validator?.Invoke(thing) ?? true),
                null, lookInHaulSources);
        }

        private static bool StartPath(Pawn_PathFollower __instance, Pawn ___pawn, LocalTargetInfo __0, PathEndMode __1)
        {
            var job = ___pawn?.CurJob;
            var map = ___pawn?.Map;
            if (planning || replaying || job?.def.suspendable != true || LeadsAnimals(___pawn) ||
                !Eligible(___pawn, map, ___pawn.Position, __0))
                return true;
            var trip = trips.GetOrCreateValue(___pawn);
            if (trip.Original != job || trip.Destination != __0)
            {
                trip.Tried.Clear();
                trip.Pending = false;
            }
            if (trip.Pending && trip.Original == job && trip.Destination == __0)
                return false;
            var parms = TraverseParms.For(___pawn, ___pawn.NormalMaxDanger());
            if (!PreferSkip(map, ___pawn.Position, __0, __1, parms, trip, out _))
                return true;

            trip.Original = job;
            trip.Destination = __0;
            trip.Mode = __1;
            trip.Pending = true;
            __instance.StopDead();
            return false;
        }

        private static bool PreferSkip(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode,
            TraverseParms parms, Trip trip, out Route route)
        {
            route = default;
            var candidate = Plan(map, start, goal, mode, parms, trip);
            if (!candidate.HasValue)
                return false;
            float nativeCost = NativeCost(map, start, goal, mode, parms);
            if (nativeCost <= 0f)
                return false;
            float exact = ExactSkipCost(map, start, goal, mode, parms, candidate.Value);
            if (!float.IsFinite(exact) || exact >= nativeCost * WorthItFactor())
                return false;
            route = candidate.Value;
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
            planning = true;
            try
            {
                if (!map.reachability.CanReach(start, goal, mode, parms))
                    return float.PositiveInfinity;
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
                    // AASB2 computes crossing animation at commit. Use walking alone as a conservative bound.
                    cost += leg;
                    start = exit.Position;
                }
                float last = ExactWalkCost(map, start, goal, mode, parms);
                return start == original || !float.IsFinite(last) ? 0f : cost + last;
            }
            finally { planning = false; }
        }

        private static float ExactSkipCost(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode,
            TraverseParms parms, Route route)
        {
            planning = true;
            try
            {
                float total = 0f;
                foreach (var hop in route.Hops)
                {
                    total += ExactWalkCost(map, start, hop.Entry, PathEndMode.OnCell, parms) + route.JumpCost;
                    if (!float.IsFinite(total))
                        return float.PositiveInfinity;
                    start = hop.Exit.Position;
                }
                return total + ExactWalkCost(map, start, goal, mode, parms);
            }
            finally { planning = false; }
        }

        private static float ExactWalkCost(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms)
        {
            if (mode == PathEndMode.OnCell && start == goal.Cell)
                return 0f;
            using var path = map.pathFinder.FindPathNow(start, goal, parms, null, mode, null);
            return path?.Found == true ? path.TotalCost : float.PositiveInfinity;
        }

        private static Route? Plan(Map map, IntVec3 start, LocalTargetInfo goal, PathEndMode mode, TraverseParms parms, Trip attempted)
        {
            var pawn = parms.pawn;
            var component = map.GetComponent(componentType);
            if (component == null)
                return null;
            var registered = index.GetValue(component, BuildIndex).Doors;
            if (registered.Length < 2 || manager.GetValue(component) is not object native ||
                !(bool)canUse.Invoke(native, new object[] { pawn }) ||
                options.GetValue(null) is not object settings || skipCost.GetValue(settings) is not float jump)
                return null;

            var context = new[] { access.Invoke(new object[] { pawn, false }) };
            var candidates = new List<Door>(registered.Length);
            planning = true;
            try
            {
                foreach (var comp in registered)
                {
                    if (comp.parent is not Building door || !UsableCell(map, door, pawn, parms.maxDanger) ||
                        !BandCompatibility.TryLocate(map, door.Position, out _, out int band))
                        continue;
                    bool entry = (bool)canEnter.Invoke(comp, context);
                    bool exit = (bool)canExit.Invoke(comp, context) && ExitClear(door, pawn);
                    if (entry || exit)
                        candidates.Add(new Door(door, band, entry, exit));
                }
                if (!BandCompatibility.TryLocate(map, start, out _, out int startBand) ||
                    !BandCompatibility.TryLocate(map, goal.Cell, out _, out int goalBand))
                    return null;

                var route = SkipRouteSearch.Find(candidates.Count, i => candidates[i].Band,
                    i => candidates[i].Enter, i => candidates[i].Exit,
                    (from, to) =>
                    {
                        if (from < 0 && candidates[to].Band != startBand)
                            return float.PositiveInfinity;
                        var cell = from < 0 ? start : candidates[from].Building.Position;
                        var door = candidates[to].Building;
                        return (cell == door.Position || map.reachability.CanReach(cell, door, PathEndMode.OnCell, parms))
                            ? WalkCost(cell, door.Position, pawn) : float.PositiveInfinity;
                    },
                    from => candidates[from].Band == goalBand &&
                            map.reachability.CanReach(candidates[from].Building.Position, goal, mode, parms)
                        ? WalkCost(candidates[from].Building.Position, goal.Cell, pawn) : float.PositiveInfinity,
                    (from, to) => attempted?.Tried.Contains((candidates[from].Building, candidates[to].Building)) != true &&
                        MountCanUse(pawn, candidates[from].Building, candidates[to].Building),
                    Math.Max(1f, jump));
                if (route is not SkipRouteSearch.Route selected || selected.Steps.Count == 0)
                    return null;
                var hops = new List<Hop>(selected.Steps.Count);
                foreach (var step in selected.Steps)
                    hops.Add(new Hop(candidates[step.Entry].Building, candidates[step.Exit].Building));
                return new Route(hops, Math.Max(1f, jump));
            }
            finally { planning = false; }
        }

        private static DoorIndex BuildIndex(MapComponent component)
        {
            var result = new List<ThingComp>();
            if (doors.GetValue(component) is IEnumerable registered)
                foreach (var item in registered)
                    if (item is ThingComp comp && doorType.IsInstanceOfType(comp))
                        result.Add(comp);
            result.Sort((first, second) => first.parent.thingIDNumber.CompareTo(second.parent.thingIDNumber));
            return new DoorIndex { Doors = result.ToArray() };
        }

        private static float WalkCost(IntVec3 start, IntVec3 goal, Pawn pawn)
        {
            int x = Math.Abs(start.x - goal.x), z = Math.Abs(start.z - goal.z);
            return (Math.Max(x, z) - Math.Min(x, z)) * pawn.TicksPerMoveCardinal +
                Math.Min(x, z) * pawn.TicksPerMoveDiagonal;
        }

        private static bool UsableCell(Map map, Building door, Pawn pawn, Danger danger) =>
            door.Spawned && door.Map == map && !door.IsForbidden(pawn) && !door.Position.IsForbidden(pawn) &&
            door.Position.GetDangerFor(pawn, map) <= danger && pawn.CanReserve(door);

        private static bool ExitClear(Thing door, Pawn pawn) =>
            door.Position.Standable(door.Map) &&
            (door.Position.GetFirstPawn(door.Map) is not Pawn occupant || occupant == pawn);

        private static void CommitPending(Pawn_JobTracker __instance, Pawn ___pawn)
        {
            var pawn = ___pawn;
            if (pawn == null || !trips.TryGetValue(pawn, out var trip) || !trip.Pending)
                return;
            trip.Pending = false;
            if (!pawn.Spawned || pawn.CurJob != trip.Original)
                return;
            var map = pawn.Map;
            if (!LeadsAnimals(pawn) && Eligible(pawn, map, pawn.Position, trip.Destination) &&
                PreferSkip(map, pawn.Position, trip.Destination, trip.Mode,
                    TraverseParms.For(pawn, pawn.NormalMaxDanger()), trip, out var route))
            {
                var transition = JobMaker.MakeJob(skipJob, route.Entry, route.Exit);
                if (pawn.Reserve(route.Entry, transition, errorOnFailed: false))
                {
                    if (pawn.Reserve(route.Exit, transition, errorOnFailed: false))
                    {
                        trip.Tried.Add((route.Entry, route.Exit));
                        __instance.StartJob(transition, JobCondition.InterruptOptional, resumeCurJobAfterwards: true,
                            keepCarryingThingOverride: true);
                        return;
                    }
                    map.reservationManager.Release(route.Entry, pawn, transition);
                }
            }

            replaying = true;
            try { pawn.pather.StartPath(trip.Destination, trip.Mode); }
            finally { replaying = false; }
        }

        private static void CheckAutonomousAccess(Pawn ___pawn, Job ___job, ref bool __result)
        {
            if (!__result || ___pawn == null || ___job == null || !BandCompatibility.IsBanded(___pawn.Map))
                return;
            var entry = ___job.GetTarget(TargetIndex.A).Thing as Building;
            var exit = ___job.GetTarget(TargetIndex.B).Thing as Building;
            if (entry == null || exit == null || BandCompatibility.SameFloor(___pawn.Map, entry.Position, exit.Position))
                return;
            if (!MountCanUse(___pawn, entry, exit))
            {
                __result = false;
                return;
            }
            // The rider's ordered jumps keep the native driver policy.
            if (___job.playerForced || CompatibilityStatus.FloorSettings?.EnableSkiptech == false)
                return;
            var context = new[] { access.Invoke(new object[] { ___pawn, false }) };
            __result = NativeAccess(___pawn) && Authorize(entry, ___pawn, canEnter, context) && Authorize(exit, ___pawn, canExit, context) &&
                ExitClear(exit, ___pawn);
        }

        // Only the native seam moves ropees. The local skip job moves its actor alone.
        private static bool LeadsAnimals(Pawn pawn) => pawn?.roping?.Ropees?.Count > 0;

        private static bool MountCanUse(Pawn rider, Building entry, Building exit)
        {
            var mount = GiddyUpSkipdoors.MountOf(rider);
            if (mount == null)
                return true;
            if (!mount.Spawned || mount.Map != rider.Map || mount.Downed || !NativeAccess(mount) ||
                !exit.Position.Standable(mount.Map))
                return false;
            var context = new[] { access.Invoke(new object[] { mount, false }) };
            return Authorize(entry, mount, canEnter, context) && Authorize(exit, mount, canExit, context);
        }

        private static bool NativeAccess(Pawn pawn)
        {
            var component = pawn.Map.GetComponent(componentType);
            return component != null && manager.GetValue(component) is object native &&
                (bool)canUse.Invoke(native, new object[] { pawn });
        }

        private static bool Authorize(Building door, Pawn pawn, MethodInfo check, object[] context)
        {
            if (!door.Spawned || door.Map != pawn.Map || door.IsForbidden(pawn) || door.Position.IsForbidden(pawn))
                return false;
            foreach (var comp in door.AllComps)
                if (doorType.IsInstanceOfType(comp))
                    return (bool)check.Invoke(comp, context);
            return false;
        }
    }
}
