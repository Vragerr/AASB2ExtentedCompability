using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class RimefellerRisers
    {
        internal const string Package = "dubwise.rimefeller";
        private const string CarrierPrefix = "AB_Carrier_Rimefeller_";
        private static Type hostType, pipeType;
        private static Func<IEnumerable<ThingDef>> carrierDefs;
        private static MethodInfo regenerate, modeOf;
        private static FieldInfo dirty, full;
        private static readonly ConditionalWeakTable<MapComponent, RebuildState> states;

        private sealed class RebuildState
        {
            internal readonly HashSet<int> ConnectedModes = new();
            internal int Loaded, Promotions;
        }

        internal static bool Ready { get; }

        static RimefellerRisers()
        {
            if (!ModsConfig.IsActive(Package) || !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            states = new();
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".rimefeller-risers");
            try
            {
                hostType = AccessTools.TypeByName("Rimefeller.MapComponent_Rimefeller");
                var host = hostType?.Assembly.GetType("Rimefeller.UniversalPipeMapComp");
                pipeType = hostType?.Assembly.GetType("Rimefeller.CompPipe");
                var registry = AccessTools.TypeByName("AsAboveSoBelow.ABColumnNetworks");
                var defs = registry == null ? null : AccessTools.PropertyGetter(registry, "AllCarrierDefs");
                var load = AccessTools.DeclaredMethod(typeof(Map), "FinalizeLoading", Type.EmptyTypes);
                regenerate = host == null ? null : AccessTools.DeclaredMethod(host, "RegenPipeGrids", Type.EmptyTypes);
                var rebuild = host == null ? null : AccessTools.DeclaredMethod(host, "RebuildPipeGrid", new[] { typeof(int) });
                dirty = host == null ? null : AccessTools.Field(host, "DirtyPipe");
                full = host == null ? null : AccessTools.Field(host, "FullRebuild");
                modeOf = pipeType == null ? null : AccessTools.PropertyGetter(pipeType, "mode");
                if (host == null || !host.IsAssignableFrom(hostType) || defs?.IsStatic != true ||
                    defs.ReturnType != typeof(IEnumerable<ThingDef>) || modeOf?.ReturnType.IsEnum != true ||
                    regenerate?.ReturnType != typeof(void) || rebuild?.ReturnType != typeof(void) ||
                    dirty?.FieldType != typeof(bool[]) || full?.FieldType != typeof(bool[]) || load?.ReturnType != typeof(void) ||
                    !PatchProcessor.GetOriginalInstructions(regenerate).Exists(op => op.Calls(rebuild)))
                    throw new InvalidOperationException("Rimefeller's native pipe scheduler contract changed.");

                carrierDefs = defs.CreateDelegate<Func<IEnumerable<ThingDef>>>();
                harmony.Patch(regenerate, prefix: Hook(nameof(BeforeRegenerate)));
                harmony.Patch(load, postfix: Hook(nameof(AfterLoad)));
                Ready = IntegrationStatus.Registered(regenerate, harmony.Id) && IntegrationStatus.Registered(load, harmony.Id);
                if (!Ready)
                    throw new InvalidOperationException("Rimefeller recovery hooks did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Rimefeller risers", "native pipe scheduler", error);
                Log.Warning("[AASB2 Extended] Rimefeller riser recovery unavailable: " + error.GetBaseException().Message);
            }
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(RimefellerRisers), name) { priority = Priority.Last };

        private static bool Enabled(Map map) =>
            CompatibilityStatus.FloorSettings?.EnableRimefellerRisers != false && BandCompatibility.IsBanded(map);

        private static void BeforeRegenerate(MapComponent __instance, Map ___map, bool[] ___DirtyPipe, bool[] ___FullRebuild)
        {
            if (!hostType.IsInstanceOfType(__instance) || !Enabled(___map) ||
                Array.IndexOf(___DirtyPipe, true) < 0)
                return;
            var state = states.GetOrCreateValue(__instance);
            for (int mode = 0; mode < Math.Min(___DirtyPipe.Length, ___FullRebuild.Length); mode++)
            {
                if (!___DirtyPipe[mode])
                    continue;
                bool hadCarrier = state.ConnectedModes.Remove(mode);
                bool hasCarrier = HasCarrier(___map, mode);
                if (hasCarrier)
                    state.ConnectedModes.Add(mode);
                if ((hadCarrier || hasCarrier) && !___FullRebuild[mode])
                {
                    ___FullRebuild[mode] = true;
                    state.Promotions++;
                }
            }
        }

        private static bool HasCarrier(Map map, int mode)
        {
            foreach (var def in carrierDefs())
            {
                if (!def.defName.StartsWith(CarrierPrefix, StringComparison.Ordinal))
                    continue;
                foreach (var item in map.listerThings.ThingsOfDef(def))
                    if (item is ThingWithComps carrier && carrier.Spawned)
                        foreach (var comp in carrier.AllComps)
                            if (pipeType.IsInstanceOfType(comp) && Convert.ToInt32(modeOf.Invoke(comp, null)) == mode)
                                return true;
            }
            return false;
        }

        private static void AfterLoad(Map __instance)
        {
            if (!Enabled(__instance) || __instance.GetComponent(hostType) is not MapComponent host)
                return;
            var pending = (bool[])dirty.GetValue(host);
            var rebuild = (bool[])full.GetValue(host);
            bool scheduled = false;
            var state = states.GetOrCreateValue(host);
            for (int mode = 0; mode < Math.Min(pending.Length, rebuild.Length); mode++)
                if (HasCarrier(__instance, mode))
                {
                    pending[mode] = rebuild[mode] = true;
                    state.Loaded++;
                    scheduled = true;
                }
            if (scheduled)
                regenerate.Invoke(host, null);
        }

        internal static string Status(Map map) =>
            Ready && map?.GetComponent(hostType) is MapComponent host && states.TryGetValue(host, out var state)
                ? $"Rimefeller risers: load modes={state.Loaded}; scheduler promotions={state.Promotions}; active modes={state.ConnectedModes.Count}"
                : "Rimefeller risers: no targeted rebuild recorded";
    }
}
