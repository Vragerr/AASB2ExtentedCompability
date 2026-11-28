using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class FactoryPorts
    {
        private static MethodInfo reset;
        internal static bool Ready { get; private set; }

        static FactoryPorts()
        {
            if (!ModsConfig.IsActive("vanillaexpanded.vfefactory"))
                return;

            const string ns = "VanillaFurnitureExpandedFactory.";
            var conveyor = AccessTools.TypeByName(ns + "Building_Conveyor");
            var tunnel = AccessTools.TypeByName(ns + "Building_UndergroundConveyorBase");
            var entrance = AccessTools.TypeByName(ns + "Building_UndergroundConveyorEntrance");
            var exit = AccessTools.TypeByName(ns + "Building_UndergroundConveyorExit");
            if (conveyor == null || tunnel == null || entrance == null || exit == null ||
                !typeof(IThingHolder).IsAssignableFrom(conveyor))
            {
                Log.Warning("[AASB2 Extended] Factory disabled: conveyor types are incomplete.");
                return;
            }

            reset = AccessTools.DeclaredMethod(conveyor, "ResetConveyorState", Type.EmptyTypes);
            var search = AccessTools.DeclaredMethod(tunnel, "FindLinkTarget",
                new[] { typeof(IntVec3), typeof(Rot4), typeof(Rot4), typeof(float), typeof(Map), typeof(ThingDef) });
            var entranceTarget = AccessTools.DeclaredMethod(entrance, "IsValidLinkTarget", new[] { typeof(ThingDef) });
            var exitTarget = AccessTools.DeclaredMethod(exit, "IsValidLinkTarget", new[] { typeof(ThingDef) });
            var eject = AccessTools.DeclaredMethod(conveyor, "EjectItems", Type.EmptyTypes);
            var canTransfer = AccessTools.DeclaredMethod(conveyor, "CanTransferToTarget", new[] { conveyor });
            var transfer = AccessTools.DeclaredMethod(conveyor, "TransferItemTo", new[] { conveyor, typeof(Thing) });
            var accept = AccessTools.DeclaredMethod(conveyor, "TryAdd", new[] { typeof(Thing) });
            if (eject?.ReturnType == typeof(void))
            {
                try
                {
                    new Harmony(CompatibilityStatus.HarmonyId + ".factory-return")
                        .Patch(eject, prefix: new HarmonyMethod(typeof(FactoryPorts), nameof(EjectPortItems)));
                }
                catch (Exception error)
                {
                    Log.Warning("[AASB2 Extended] Factory cargo return disabled: " + error.Message);
                }
            }
            if (reset?.ReturnType != typeof(void) || search?.ReturnType != tunnel ||
                entranceTarget?.ReturnType != typeof(bool) || exitTarget?.ReturnType != typeof(bool) ||
                eject?.ReturnType != typeof(void) || canTransfer?.ReturnType != typeof(bool) ||
                transfer?.ReturnType != typeof(bool) || accept?.ReturnType != typeof(bool))
            {
                Log.Warning("[AASB2 Extended] Factory disabled: incompatible vertical transfer contract.");
                return;
            }

            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".factory-ports");
            try
            {
                harmony.Patch(search, prefix: new HarmonyMethod(typeof(FactoryPorts), nameof(SkipTunnelSearch)));
                var filter = new HarmonyMethod(typeof(FactoryPorts), nameof(ExcludePortFromTunnel));
                harmony.Patch(entranceTarget, postfix: filter);
                harmony.Patch(exitTarget, postfix: filter);
                harmony.Patch(canTransfer, postfix: new HarmonyMethod(typeof(FactoryPorts), nameof(RequirePortSpace)));
                harmony.Patch(transfer, prefix: new HarmonyMethod(typeof(FactoryPorts), nameof(RejectOccupiedTarget)));
                harmony.Patch(accept, prefix: new HarmonyMethod(typeof(FactoryPorts), nameof(RejectOccupiedPort)));
                var inspect = AccessTools.DeclaredMethod(tunnel, "GetInspectString", Type.EmptyTypes);
                if (inspect?.ReturnType == typeof(string))
                    harmony.Patch(inspect, postfix: new HarmonyMethod(typeof(FactoryPorts), nameof(RemoveNativeTunnelStatus)));
                foreach (var target in new[] { search, entranceTarget, exitTarget, canTransfer, transfer, accept })
                    if (!IntegrationStatus.Registered(target, harmony.Id))
                        throw new InvalidOperationException("Required Factory transfer patch did not register: " + target.Name);
                Ready = BandCompatibility.PortsReady &&
                    DefDatabase<ResearchProjectDef>.GetNamedSilentFail("VFE_BasicFactories") != null;
                if (!Ready)
                    Log.Warning("[AASB2 Extended] Vertical Factory ports disabled: AASB2 band API or VFE_BasicFactories is unavailable.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Factory transfer", "registration", error);
                Log.Warning("[AASB2 Extended] Vertical Factory ports disabled: " + error.Message);
            }
            FactoryPortPresentation.Install(conveyor, tunnel);
        }

        private static bool IsPort(ThingDef def) => def?.HasComp(typeof(CompVerticalFactoryPort)) == true;

        // No loaded link, tunnel queue or cross-reference is needed for these endpoints.
        private static bool SkipTunnelSearch(ThingDef __5) => !IsPort(__5);

        private static void ExcludePortFromTunnel(ThingDef __0, ref bool __result)
        {
            if (IsPort(__0))
                __result = false;
        }

        private static bool HasPortSpace(Thing target) =>
            !IsPort(target.def) || (target is IThingHolder holder && holder.GetDirectlyHeldThings()?.Count == 0);

        private static void RequirePortSpace(Thing __0, ref bool __result)
        {
            if (__result && __0 != null && !HasPortSpace(__0))
                __result = false;
        }

        private static bool RejectOccupiedTarget(Thing __0) => __0 == null || HasPortSpace(__0);
        private static bool RejectOccupiedPort(Thing __instance) => HasPortSpace(__instance);

        private static void RemoveNativeTunnelStatus(Thing __instance, ref string __result)
        {
            if (!IsPort(__instance.def) || string.IsNullOrEmpty(__result))
                return;

            string oldStatus = "VFEFactory_ConnectionMissing".Translate();
            var lines = new List<string>();
            foreach (var line in __result.Split('\n'))
            {
                string value = line.Trim();
                if (value.Length > 0 && value != oldStatus)
                    lines.Add(value);
            }
            __result = string.Join("\n", lines);
        }

        internal static void Restart(Thing building) => reset.Invoke(building, null);

        private static bool EjectPortItems(Thing __instance)
        {
            if (!(__instance is IThingHolder holder) || __instance.Map == null ||
                (!IsPort(__instance.def) && !BandCompatibility.IsBanded(__instance.Map)))
                return true;

            ThingOwner cargo = holder.GetDirectlyHeldThings();
            if (cargo == null)
                return true;
            var map = __instance.Map;
            var position = __instance.Position;
            var returns = map.GetComponent<FactoryPortReturns>();
            while (cargo.Count != 0)
            {
                var item = cargo[0];
                if (FactoryPortReturns.Drop(cargo, item, map, position))
                    continue;
                if (returns != null && returns.Retain(position, cargo, item))
                    continue;
                Log.ErrorOnce("[AASB2 Extended] Vertical Factory port could not return its contents.", 117620764);
                break;
            }
            return false;
        }
    }

    public sealed class CompProperties_VerticalFactoryPort : CompProperties
    {
        public bool input;
        public bool upward;
        public bool legacy;
        public bool section;

        public CompProperties_VerticalFactoryPort() => compClass = typeof(CompVerticalFactoryPort);
    }

    public sealed partial class CompVerticalFactoryPort : ThingComp
    {
        private int destination;
        private bool routeSelected;
        private FactoryShaft shaft;

        internal CompProperties_VerticalFactoryPort Port => (CompProperties_VerticalFactoryPort)props;
        internal ThingOwner Cargo => (parent as IThingHolder)?.GetDirectlyHeldThings();
        internal bool Powered => parent.GetComp<CompPowerTrader>()?.PowerOn != false;
        internal int Floor { get; private set; }
        internal bool Output => !Port.input && !Port.section;
        internal static bool Supported(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map))
                return false;
            var terrain = map.terrainGrid.TerrainAt(cell);
            return terrain != null && terrain.defName != "AB_OpenAir" && terrain.passability != Traversability.Impassable;
        }

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref destination, "verticalFactoryDestination", 0);
            Scribe_Values.Look(ref routeSelected, "verticalFactoryRouteSelected", false);
            ExposeRoutes();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            var map = parent.Map;
            if (!BandCompatibility.TryLocate(map, parent.Position, out var local, out var floor))
                return;
            Floor = floor;
            shaft = map.GetComponent<FactoryShafts>()?.Register(local, this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            if (shaft != null)
                map.GetComponent<FactoryShafts>()?.Unregister(shaft, this);
            shaft = null;
        }

        public override void CompTick()
        {
            if (FactoryPorts.Ready && Port.input && Cargo?.Count > 0)
                shaft?.Advance(Find.TickManager.TicksGame);
        }

        internal bool Accepts(Thing item)
        {
            var target = Cargo;
            return Powered && target?.Count == 0 && target.GetCountCanAccept(item, false) >= item.stackCount &&
                (!(parent is IStoreSettingsParent storage) || storage.GetStoreSettings()?.AllowedToAccept(item) != false);
        }

        internal void TryRestoreLegacyDestination()
        {
            if (shaft == null || routeSelected || destination != 0 || !Port.legacy ||
                !BandCompatibility.TryAdjacent(parent.Map, parent.Position, Port.upward, out var adjacent) ||
                !BandCompatibility.TryLocate(parent.Map, adjacent, out _, out var floor))
                return;
            var candidate = shaft.At(floor);
            if (candidate?.Output == true && candidate.Port.legacy && candidate.Port.upward == Port.upward)
                destination = candidate.parent.thingIDNumber;
        }

        private string Where(CompVerticalFactoryPort port)
        {
            int level = BandCompatibility.Level(port.parent.Map, port.parent.Position);
            return "AASBExt.Factory.Destination".Translate(level, shaft.Local.x, shaft.Local.z);
        }

        public override string CompInspectStringExtra()
        {
            if (shaft == null)
                return "AASBExt.Factory.Unlinked".Translate();
            string location = Where(this);
            if (Port.section)
                return location + "\n" + "AASBExt.Factory.Section".Translate();
            if (!Port.input)
            {
                int incoming = shaft.InputCount(this);
                string outputState = incoming == 0 ? "AASBExt.Factory.UnusedOutput" :
                    Cargo?.Count > 0 ? "AASBExt.Factory.Full" : "AASBExt.Factory.Linked";
                return location + "\n" + "AASBExt.Factory.Inputs".Translate(incoming) + "\n" + outputState.Translate();
            }

            var item = Cargo?.Count > 0 ? Cargo[0] : null;
            var output = PendingOutput == 0 && item != null ? ChooseOutput(item) ?? DisplayOutput : DisplayOutput;
            string state;
            if (output == null)
                state = (Routes.Count == 0 ? "AASBExt.Factory.WaitDestination" : "AASBExt.Factory.OutputGone").Translate();
            else if (shaft.RouteFailure(this, output) is string failure)
                state = failure.Translate();
            else if (!Powered || !output.Powered)
                state = "AASBExt.Factory.NoPower".Translate();
            else if (shaft.Remaining(this) > 0)
                state = "AASBExt.Factory.Progress".Translate(shaft.Remaining(this));
            else if (Cargo?.Count > 0 && !AllowsDestination(output.parent.thingIDNumber, Cargo[0]))
                state = "AASBExt.Factory.FilterRejected".Translate();
            else if (Cargo?.Count > 0 && !output.Accepts(Cargo[0]))
                state = "AASBExt.Factory.Full".Translate();
            else
                state = "AASBExt.Factory.Linked".Translate();
            string cargo = Cargo?.Count > 0 ? Cargo[0].LabelCap.ToString() : "-";
            return location + "\n" + state + "\n" + "AASBExt.Factory.Cargo".Translate(cargo) +
                (output == null ? "" : "\n" + Where(output));
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (shaft == null || !FactoryPorts.Ready || parent.Faction?.IsPlayer != true)
                yield break;
            yield return new Command_Action
            {
                defaultLabel = "AASBExt.Factory.BuildLinked".Translate(),
                icon = parent.def.uiIcon,
                action = BuildLinked
            };
            if (!Port.input)
                yield break;
            yield return new Command_Action
            {
                defaultLabel = "AASBExt.Factory.Configure".Translate(),
                icon = parent.def.uiIcon,
                action = ConfigureRoutes
            };
            var output = DisplayOutput;
            if (output != null)
                yield return new Command_Action
                {
                    defaultLabel = "AASBExt.Factory.ShowPartner".Translate(),
                    icon = output.parent.def.uiIcon,
                    action = () =>
                    {
                        if (output.parent.Spawned)
                            CameraJumper.TryJumpAndSelect(output.parent);
                    }
                };
        }

        [DebugAction("AASB2 Extended", "Inspect vertical conveyor",
            actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectVerticalConveyor()
        {
            SaveConveyorReport(Find.CurrentMap, UI.MouseCell());
        }

        internal static void SaveConveyorReport(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map))
                return;
            foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
            {
                var port = (thing as ThingWithComps)?.GetComp<CompVerticalFactoryPort>();
                if (port == null) continue;
                var cargo = port.Cargo;
                var item = cargo?.Count > 0 ? cargo[0] : null;
                DiagnosticReports.Write("conveyor", $"[AASB2 Extended] Vertical Factory: {thing.def.defName} #{thing.thingIDNumber} " +
                    $"map={map.uniqueID} cell={cell} level={BandCompatibility.Level(map, cell)} " +
                    $"supported={Supported(map, cell)} adapter={FactoryPorts.Ready} destination={port.destination} " +
                    $"cargo={item?.GetUniqueLoadID() ?? "-"} stack={item?.stackCount ?? 0} " +
                    $"ownedByPort={item != null && item.holdingOwner == cargo} " +
                    $"shaft={port.shaft?.Describe(port) ?? "-"}");
                return;
            }
        }
    }

    public sealed class PlaceWorker_VerticalFactoryPort : PlaceWorker
    {
        public override bool IsBuildDesignatorVisible(BuildableDef def) =>
            FactoryPorts.Ready && CompatibilityStatus.FloorSettings?.EnableFactoryPorts == true &&
            (def as ThingDef)?.GetCompProperties<CompProperties_VerticalFactoryPort>()?.legacy == false;

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map,
            Thing thingToIgnore = null, Thing thing = null)
        {
            if (!IsBuildDesignatorVisible(checkingDef))
                return new AcceptanceReport("AASBExt.Factory.Disabled".Translate());
            if (!CompVerticalFactoryPort.Supported(map, loc) || !BandCompatibility.TryLocate(map, loc, out _, out _))
                return new AcceptanceReport("AASBExt.Factory.NoSupport".Translate());
            foreach (var item in map.thingGrid.ThingsListAtFast(loc))
                if (item != thingToIgnore && item != thing &&
                    (item.def.HasComp(typeof(CompVerticalFactoryPort)) ||
                    (item.def.entityDefToBuild as ThingDef)?.HasComp(typeof(CompVerticalFactoryPort)) == true))
                    return new AcceptanceReport("AASBExt.Factory.Occupied".Translate());
            return true;
        }
    }

    public sealed class FactoryPortReturns : MapComponent, IThingHolder
    {
        private ThingOwner<Thing> contents;
        private Dictionary<int, IntVec3> origins = new Dictionary<int, IntVec3>();

        public FactoryPortReturns(Map map) : base(map) => contents = new ThingOwner<Thing>(this);

        public ThingOwner GetDirectlyHeldThings() => contents;
        public IThingHolder ParentHolder => null;
        public void GetChildHolders(List<IThingHolder> outChildren) =>
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, contents);

        internal static bool Drop(ThingOwner source, Thing item, Map map, IntVec3 origin)
        {
            bool Valid(IntVec3 cell) => BandCompatibility.SameFloor(map, origin, cell) &&
                CompVerticalFactoryPort.Supported(map, cell);
            if (Valid(origin) && source.TryDrop(item, origin, map, ThingPlaceMode.Direct, out _))
                return true;
            return source.Contains(item) && source.TryDrop(item, origin, map, ThingPlaceMode.Near, out _,
                nearPlaceValidator: Valid);
        }

        internal bool Retain(IntVec3 cell, ThingOwner source, Thing item)
        {
            if (!source.TryTransferToContainer(item, contents, false))
                return false;
            origins[item.thingIDNumber] = cell;
            Messages.Message("AASBExt.Factory.Retained".Translate(), MessageTypeDefOf.NeutralEvent);
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref contents, "verticalFactoryReturns", this);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && contents == null)
                contents = new ThingOwner<Thing>(this);
            Scribe_Collections.Look(ref origins, "verticalFactoryReturnCells", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && origins == null)
                origins = new Dictionary<int, IntVec3>();
        }

        internal void Release()
        {
            if (contents == null)
                return;
            for (int i = contents.Count - 1; i >= 0; i--)
            {
                Thing item = contents[i];
                int id = item.thingIDNumber;
                if (!origins.TryGetValue(id, out var origin) || !origin.InBounds(map))
                    continue;
                if (Drop(contents, item, map, origin))
                    origins.Remove(id);
            }
            if (contents.Count != 0)
                Messages.Message("AASBExt.Factory.Retained".Translate(), MessageTypeDefOf.NeutralEvent);
        }
    }
}
