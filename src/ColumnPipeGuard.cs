using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class ColumnPipeGuard
    {
        private static Type columnClass;
        private static MethodInfo networkList, upCell, isCarrier;
        private static FieldInfo template, carrier, networkType, enabled, networkId, columnTypeExtension;
        private static MethodInfo columnType;
        private static readonly Dictionary<ThingDef, List<object>> matches = new();

        internal static bool Ready { get; }
        private static bool Enabled => CompatibilityStatus.FloorSettings?.ProtectColumnPipes != false;

        static ColumnPipeGuard()
        {
            if (!ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".column-pipes");
            try
            {
                columnClass = AccessTools.TypeByName("AsAboveSoBelow.Building_ABColumn");
                var assembly = columnClass?.Assembly ?? throw new InvalidOperationException("Column class missing.");
                var registry = assembly.GetType("AsAboveSoBelow.ABColumnNetworks", true);
                var network = assembly.GetType("AsAboveSoBelow.ABNetwork", true);
                networkList = AccessTools.PropertyGetter(registry, "All");
                upCell = AccessTools.DeclaredMethod(columnClass, "TryUpCell", new[] { typeof(IntVec3).MakeByRefType() });
                isCarrier = AccessTools.DeclaredMethod(registry, "IsCarrier", new[] { typeof(ThingDef) });
                template = AccessTools.DeclaredField(network, "template");
                carrier = AccessTools.DeclaredField(network, "carrier");
                networkType = AccessTools.DeclaredField(network, "type");
                networkId = AccessTools.DeclaredField(network, "id");
                enabled = AccessTools.DeclaredField(columnClass, "enabled");
                columnType = AccessTools.PropertyGetter(columnClass, "ColumnType");
                var extension = assembly.GetType("AsAboveSoBelow.ABColumnTypeExt", true);
                columnTypeExtension = AccessTools.DeclaredField(extension, "columnType");
                var toggle = AccessTools.DeclaredMethod(columnClass, "Toggle", new[] { network });
                var ensure = AccessTools.DeclaredMethod(columnClass, "EnsureCarrierAt", new[] { typeof(Map), typeof(IntVec3), network });
                var placement = AccessTools.DeclaredMethod(typeof(GenConstruct), "CanPlaceBlueprintAt_NewTemp",
                    new[] { typeof(BuildableDef), typeof(IntVec3), typeof(Rot4), typeof(Map), typeof(bool),
                        typeof(Thing), typeof(Thing), typeof(ThingDef), typeof(bool), typeof(bool), typeof(bool), typeof(Predicate<Thing>) });
                if (networkList?.IsStatic != true || !typeof(IEnumerable).IsAssignableFrom(networkList.ReturnType) ||
                    upCell?.ReturnType != typeof(bool) || isCarrier?.ReturnType != typeof(bool) ||
                    template?.FieldType != typeof(ThingDef) || carrier?.FieldType != typeof(ThingDef) ||
                    networkType == null || networkId?.FieldType != typeof(string) ||
                    enabled?.FieldType != typeof(List<string>) || columnType?.ReturnType != networkType.FieldType ||
                    columnTypeExtension?.FieldType != networkType.FieldType || !networkType.FieldType.IsEnum ||
                    toggle?.ReturnType != typeof(void) || ensure?.ReturnType != typeof(void) ||
                    placement?.ReturnType != typeof(AcceptanceReport))
                    throw new InvalidOperationException("Column and placement contracts changed.");

                harmony.Patch(placement, postfix: new HarmonyMethod(typeof(ColumnPipeGuard), nameof(CheckPlacement)));
                harmony.Patch(toggle, prefix: new HarmonyMethod(typeof(ColumnPipeGuard), nameof(BeforeToggle)));
                harmony.Patch(ensure, prefix: new HarmonyMethod(typeof(ColumnPipeGuard), nameof(BeforeCarrier)));
                foreach (var method in new[] { placement, toggle, ensure })
                    if (!IntegrationStatus.Registered(method, harmony.Id))
                        throw new InvalidOperationException("Column guard registration failed: " + method.Name);
                Ready = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Column pipe protection", "placement / toggle / carrier", error);
                Log.Warning("[AASB2 Extended] Column pipe protection unavailable: " + error.Message);
            }
        }

        private static bool SameNetwork(ThingDef definition, object network)
        {
            if (definition == null || (bool)isCarrier.Invoke(null, new object[] { definition }))
                return false;
            var sample = (ThingDef)template.GetValue(network);
            if (sample == null || carrier.GetValue(network) == null)
                return false;
            if (definition == sample)
                return true;
            if (definition.building?.isEdifice != false ||
                (definition.thingClass != sample.thingClass && definition.altitudeLayer != AltitudeLayer.Conduits) ||
                definition.comps == null || sample.comps == null)
                return false;
            foreach (var reference in sample.comps)
            {
                var type = reference.GetType();
                // Identify alternate pipe forms by their network discriminator, not by their label or material.
                var discriminator = type.FullName == "PipeSystem.CompProperties_Resource"
                    ? AccessTools.Field(type, "pipeNet")
                    : type.Name == "CompProperties_Pipe" ? AccessTools.Field(type, "mode") : null;
                if (discriminator == null)
                    continue;
                foreach (var candidate in definition.comps)
                    if (candidate.GetType() == type && Equals(discriminator.GetValue(candidate), discriminator.GetValue(reference)))
                        return true;
            }
            return false;
        }

        private static Thing Conflict(Map map, IntVec3 cell, object network)
        {
            if (!cell.InBounds(map))
                return null;
            foreach (var thing in cell.GetThingList(map))
            {
                var definition = thing.def.entityDefToBuild as ThingDef ?? thing.def;
                if (SameNetwork(definition, network))
                    return thing;
            }
            return null;
        }

        private static bool BeforeCarrier(Map __0, IntVec3 __1, object __2) =>
            !Enabled || __0 == null || Conflict(__0, __1, __2) == null;

        private static bool BeforeToggle(Building __instance, object __0)
        {
            if (!Enabled || !__instance.Spawned ||
                ((List<string>)enabled.GetValue(__instance)).Contains((string)networkId.GetValue(__0)))
                return true;

            var map = __instance.Map;
            var args = new object[] { IntVec3.Invalid };
            var conflict = Conflict(map, __instance.Position, __0);
            if (conflict == null && (bool)upCell.Invoke(__instance, args))
                conflict = Conflict(map, (IntVec3)args[0], __0);
            if (conflict == null)
                return true;
            Messages.Message("AASBExt.ColumnPipe.Conflict".Translate(conflict.LabelCap),
                conflict, MessageTypeDefOf.RejectInput, false);
            return false;
        }

        private static bool Reserves(Thing thing, IntVec3 endpoint, List<object> networks)
        {
            if (!columnClass.IsInstanceOfType(thing))
                return false;
            var type = columnType.Invoke(thing, null);
            if (!networks.Exists(network => Equals(type, networkType.GetValue(network))))
                return false;
            if (thing.OccupiedRect().Contains(endpoint))
                return true;
            var args = new object[] { IntVec3.Invalid };
            return (bool)upCell.Invoke(thing, args) && (IntVec3)args[0] == endpoint;
        }

        private static void CheckPlacement(BuildableDef __0, IntVec3 __1, Rot4 __2, Map __3, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || !Enabled || __0 is not ThingDef definition || !BandCompatibility.IsBanded(__3))
                return;
            if (columnClass.IsAssignableFrom(definition.thingClass))
            {
                RejectColumnOverExistingPipe(definition, __1, __2, __3, ref __result);
                return;
            }
            if (!matches.TryGetValue(definition, out var networks))
            {
                networks = new List<object>();
                foreach (var network in (IEnumerable)networkList.Invoke(null, null))
                    if (SameNetwork(definition, network))
                        networks.Add(network);
                matches.Add(definition, networks);
            }
            if (networks.Count == 0)
                return;

            foreach (var cell in GenAdj.OccupiedRect(__1, __2, definition.size))
            {
                bool blocked = ReservedAt(__3, cell, cell, networks) ||
                    (BandCompatibility.TryAdjacent(__3, cell, false, out var below) &&
                        ReservedAt(__3, below, cell, networks));
                if (!blocked)
                    continue;
                __result = new AcceptanceReport("AASBExt.ColumnPipe.Reserved".Translate());
                return;
            }
        }

        private static void RejectColumnOverExistingPipe(ThingDef definition, IntVec3 origin, Rot4 rotation,
            Map map, ref AcceptanceReport result)
        {
            // In the installed AASB2 contract, ColumnType falls back to enum value 1 without an extension.
            object type = Enum.ToObject(networkType.FieldType, 1);
            if (definition.modExtensions != null)
                foreach (var extension in definition.modExtensions)
                    if (columnTypeExtension.DeclaringType.IsInstanceOfType(extension))
                    {
                        type = columnTypeExtension.GetValue(extension);
                        break;
                    }

            bool hasUpper = BandCompatibility.TryAdjacent(map, origin, true, out var upper);
            foreach (var network in (IEnumerable)networkList.Invoke(null, null))
            {
                if (!Equals(type, networkType.GetValue(network)) || carrier.GetValue(network) == null)
                    continue;
                Thing conflict = hasUpper ? Conflict(map, upper, network) : null;
                foreach (var cell in GenAdj.OccupiedRect(origin, rotation, definition.size))
                {
                    if (conflict != null)
                        break;
                    conflict = Conflict(map, cell, network);
                }
                if (conflict == null)
                    continue;
                result = new AcceptanceReport("AASBExt.ColumnPipe.Conflict".Translate(conflict.LabelCap));
                return;
            }
        }

        private static bool ReservedAt(Map map, IntVec3 cell, IntVec3 endpoint, List<object> networks)
        {
            if (!cell.InBounds(map))
                return false;
            foreach (var thing in cell.GetThingList(map))
                if (Reserves(thing, endpoint, networks))
                    return true;
            return false;
        }
    }
}
