using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class DchRiser
    {
        internal const string Package = "dubwise.dubscentralheating";
        private const string NetworkId = "DubsCentralHeating.Heating";
        private const string Owner = CompatibilityStatus.HarmonyId + ".dch-riser";
        private static readonly ConditionalWeakTable<MapComponent, RebuildState> states;
        private static MethodInfo rebuild, merge, byId;
        private static FieldInfo mapField, carrierField, dropped;
        private static object family;
        private static int heating;
        internal static bool Ready { get; }
        internal static bool ProvidedByAasb { get; }

        private sealed class RebuildState { internal bool HadRisers; internal int Rebuilds, LastMerges; }

        static DchRiser()
        {
            if (!ModsConfig.IsActive(Package) || !ModsConfig.IsActive("astryl.asabovesobelow2") ||
                !CompatibilityStatus.DchAtStartup)
                return;

            states = new();
            var harmony = new Harmony(Owner);
            try
            {
                var host = AccessTools.TypeByName("DubsCentralHeating.HygienePipeMapComp");
                var net = AccessTools.TypeByName("DubsCentralHeating.PlumbingNet");
                var pipe = AccessTools.TypeByName("DubsCentralHeating.CompPipe");
                var pipeKind = host?.Assembly.GetType("DubsCentralHeating.PipeType");
                var native = AccessTools.TypeByName("AsAboveSoBelow.Patch_DubsPipes_ABRiserLink");
                var registry = native?.Assembly.GetType("AsAboveSoBelow.ABColumnNetworks");
                var bridge = native?.GetNestedType("Family", BindingFlags.Public | BindingFlags.NonPublic);
                if (host == null || net == null || pipe == null || bridge == null || registry == null ||
                    pipeKind?.IsEnum != true || !Enum.IsDefined(pipeKind, "Heating"))
                    throw new InvalidOperationException("The Dubs heating or AASB2 riser contract is unavailable.");

                if (native.TypeInitializer != null &&
                    PatchProcessor.GetOriginalInstructions(native.TypeInitializer).Exists(op => Equals(op.operand, host.FullName)))
                {
                    ProvidedByAasb = true;
                    return;
                }

                heating = Convert.ToInt32(Enum.Parse(pipeKind, "Heating"));
                rebuild = AccessTools.DeclaredMethod(host, "RebuildPipeGrid", new[] { typeof(int) });
                var incremental = AccessTools.DeclaredMethod(host, "RebuildChangedNets", new[] { typeof(int) });
                var netList = AccessTools.Field(host, "PipeNets");
                var netRef = AccessTools.Field(pipe, "pipeNetRef");
                var piped = AccessTools.Field(net, "PipedThings");
                var init = AccessTools.DeclaredMethod(net, "InitNet", Type.EmptyTypes);
                mapField = AccessTools.Field(typeof(MapComponent), "map");
                byId = AccessTools.DeclaredMethod(registry, "ById", new[] { typeof(string) });
                carrierField = AccessTools.Field(byId.ReturnType, "carrier");
                merge = AccessTools.DeclaredMethod(native, "MergeFor", new[] { typeof(Map), bridge, typeof(object) });
                dropped = AccessTools.Field(bridge, "drops");
                if (netList?.FieldType != net.MakeArrayType() || netRef?.FieldType != net ||
                    init?.ReturnType != typeof(void) || rebuild?.ReturnType != typeof(void) ||
                    incremental?.ReturnType != typeof(void) || piped == null || merge?.ReturnType != typeof(void) ||
                    mapField?.FieldType != typeof(Map) || carrierField?.FieldType != typeof(ThingDef) ||
                    dropped?.FieldType != typeof(int))
                    throw new InvalidOperationException("The native Dubs heating rebuild contract changed.");

                family = Activator.CreateInstance(bridge, true);
                void Set(string name, object value)
                {
                    var field = AccessTools.Field(bridge, name);
                    if (field == null || !field.FieldType.IsInstanceOfType(value))
                        throw new InvalidOperationException("Missing native riser field " + name);
                    field.SetValue(family, value);
                }
                Set("name", "DubsCentralHeating");
                Set("prefix", NetworkId);
                Set("compPipe", pipe);
                Set("pipeNets", netList);
                Set("pipeNetRef", netRef);
                Set("pipedThings", piped);
                Set("initNet", init);

                harmony.Patch(rebuild, postfix: new HarmonyMethod(typeof(DchRiser), nameof(AfterRebuild)));
                harmony.Patch(incremental, prefix: new HarmonyMethod(typeof(DchRiser), nameof(BeforeIncremental)));
                if (!IntegrationStatus.Registered(rebuild, Owner) || !IntegrationStatus.Registered(incremental, Owner))
                    throw new InvalidOperationException("Dubs heating rebuild hooks did not register.");
                Ready = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(Owner);
                IntegrationStatus.NoteFailure("Dubs heating risers", "native rebuild", error);
                Log.Warning("[AASB2 Extended] Dubs heating risers unavailable: " + error.GetBaseException().Message);
            }
        }

        private static bool HasRisers(Map map)
        {
            var network = byId.Invoke(null, new object[] { NetworkId });
            return network != null && carrierField.GetValue(network) is ThingDef def &&
                map.listerThings.ThingsOfDef(def).Count != 0;
        }

        private static bool BeforeIncremental(MapComponent __instance, int __0)
        {
            if (__0 != heating || mapField.GetValue(__instance) is not Map map || !BandCompatibility.IsBanded(map))
                return true;
            if (!HasRisers(map) && !(states.TryGetValue(__instance, out var previous) && previous.HadRisers))
                return true;
            // Incremental rebuilding assumes every member is reachable over planar pipes.
            // Let the full native rebuild split the former union when a riser has been removed.
            rebuild.Invoke(__instance, new object[] { __0 });
            return false;
        }

        private static void AfterRebuild(MapComponent __instance, int __0)
        {
            if (__0 != heating || mapField.GetValue(__instance) is not Map map || !BandCompatibility.IsBanded(map))
                return;
            var state = states.GetOrCreateValue(__instance);
            state.HadRisers = HasRisers(map);
            state.Rebuilds++;
            state.LastMerges = 0;
            if (!state.HadRisers)
                return;
            int before = (int)dropped.GetValue(family);
            merge.Invoke(null, new[] { map, family, __instance });
            state.LastMerges = (int)dropped.GetValue(family) - before;
        }

        internal static string Status(Map map)
        {
            if (ProvidedByAasb)
                return "DCH heating risers: supplied by AASB2";
            if (!Ready || map == null)
                return $"DCH heating risers: active={Ready}";
            var host = map.GetComponent(AccessTools.TypeByName("DubsCentralHeating.HygienePipeMapComp"));
            return Ready && host != null && states.TryGetValue(host, out var state)
                ? $"DCH heating: rebuilds={state.Rebuilds}; carrier present={state.HadRisers}; last merged native nets={state.LastMerges}"
                : $"DCH heating risers: active={Ready}; no observed heating rebuild";
        }
    }
}
