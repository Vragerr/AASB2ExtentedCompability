using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class DbhRiserLoad
    {
        internal const string Package = "dubwise.dubsbadhygiene";
        private const string CarrierPrefix = "AB_Carrier_DubsBadHygiene_";
        private static Type hostType, pipeType;
        private static Func<IEnumerable<ThingDef>> carrierDefs;
        private static MethodInfo regenerate, modeOf;
        private static FieldInfo dirty, full;
        private static readonly ConditionalWeakTable<MapComponent, RebuildState> states;

        private sealed class RebuildState
        {
            internal readonly HashSet<int> ConnectedModes = new();
            internal int Loaded, Promotions;
            internal bool LoadHandled;
        }

        internal static bool Ready { get; }

        static DbhRiserLoad()
        {
            if (!ModsConfig.IsActive(Package) || !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            states = new();
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".dbh-riser-load");
            try
            {
                var host = hostType = AccessTools.TypeByName("DubsBadHygiene.HygienePipeMapComp");
                pipeType = host?.Assembly.GetType("DubsBadHygiene.CompPipe");
                var registry = AccessTools.TypeByName("AsAboveSoBelow.ABColumnNetworks");
                var defs = registry == null ? null : AccessTools.PropertyGetter(registry, "AllCarrierDefs");
                var load = AccessTools.DeclaredMethod(typeof(Map), "FinalizeLoading", Type.EmptyTypes);
                regenerate = host == null ? null : AccessTools.DeclaredMethod(host, "RegenPipeGrids", Type.EmptyTypes);
                var changed = pipeType == null ? null : AccessTools.DeclaredMethod(host, "PipeChanged", new[] { pipeType });
                var rebuild = host == null ? null : AccessTools.DeclaredMethod(host, "RebuildPipeGrid", new[] { typeof(int) });
                dirty = host == null ? null : AccessTools.Field(host, "DirtyPipe");
                full = host == null ? null : AccessTools.Field(host, "FullRebuild");
                modeOf = pipeType == null ? null : AccessTools.PropertyGetter(pipeType, "mode");
                if (host == null || !typeof(MapComponent).IsAssignableFrom(hostType) || defs?.IsStatic != true ||
                    defs.ReturnType != typeof(IEnumerable<ThingDef>) || modeOf?.ReturnType.IsEnum != true ||
                    regenerate?.ReturnType != typeof(void) || rebuild?.ReturnType != typeof(void) ||
                    changed?.ReturnType != typeof(void) ||
                    dirty?.FieldType != typeof(bool[]) || full?.FieldType != typeof(bool[]) || load?.ReturnType != typeof(void) ||
                    !PatchProcessor.GetOriginalInstructions(regenerate).Exists(op => op.Calls(rebuild)))
                    throw new InvalidOperationException("DBH's native pipe scheduler contract changed.");

                carrierDefs = defs.CreateDelegate<Func<IEnumerable<ThingDef>>>();
                harmony.Patch(changed, postfix: Hook(nameof(AfterPipeChanged)));
                harmony.Patch(load, postfix: Hook(nameof(AfterLoad)));
                Ready = IntegrationStatus.Registered(changed, harmony.Id) && IntegrationStatus.Registered(load, harmony.Id);
                if (!Ready)
                    throw new InvalidOperationException("DBH recovery hooks did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("DBH risers", "native pipe scheduler", error);
                Log.Warning("[AASB2 Extended] DBH riser recovery unavailable: " + error.GetBaseException().Message);
            }
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(DbhRiserLoad), name) { priority = Priority.Last };

        private static bool Enabled(Map map) =>
            CompatibilityStatus.FloorSettings?.EnableDbhRiserRecovery != false && BandCompatibility.IsBanded(map);

        private static void AfterPipeChanged(MapComponent __instance, object __0, Map ___map, bool[] ___FullRebuild)
        {
            if (!Enabled(___map))
                return;
            int mode = Convert.ToInt32(modeOf.Invoke(__0, null));
            if ((uint)mode >= (uint)___FullRebuild.Length)
                return;

            // Only full rebuilds run AASB2's native callback that links carrier pairs.
            // Promote topology events here, away from DBH's frequent network queries.
            var state = states.GetOrCreateValue(__instance);
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
            var state = states.GetOrCreateValue(host);
            if (state.LoadHandled)
                return;
            state.LoadHandled = true;
            var pending = (bool[])dirty.GetValue(host);
            var rebuild = (bool[])full.GetValue(host);
            bool scheduled = false;
            for (int mode = 0; mode < Math.Min(pending.Length, rebuild.Length); mode++)
                if (HasCarrier(__instance, mode))
                {
                    pending[mode] = rebuild[mode] = true;
                    state.ConnectedModes.Add(mode);
                    state.Loaded++;
                    scheduled = true;
                }
            if (!scheduled)
                return;
            try
            {
                regenerate.Invoke(host, null);
            }
            catch (TargetInvocationException error)
            {
                Log.Error("[AASB2 Extended] DBH riser recovery failed: " + error.GetBaseException());
            }
        }

        internal static string Status(Map map) =>
            Ready && map?.GetComponent(hostType) is MapComponent host && states.TryGetValue(host, out var state)
                ? $"DBH risers: load modes={state.Loaded}; scheduler promotions={state.Promotions}; active modes={state.ConnectedModes.Count}"
                : "DBH risers: no targeted rebuild recorded";
    }
}
