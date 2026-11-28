using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class PipeProjection
    {
        private static Dictionary<Type, FastInvokeHandler> printers;
        internal static bool OpenAirReady { get; }

        static PipeProjection()
        {
            if (!ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;

            Bind(DbhRiserLoad.Package, "DubsBadHygiene");
            Bind(DchRiser.Package, "DubsCentralHeating");
            Bind(RimefellerRisers.Package, "Rimefeller");
            Bind("dubwise.rimatomics", "Rimatomics");
            if (printers == null)
                return;

            var below = AccessTools.TypeByName("AsAboveSoBelow.SectionLayer_ABBelowV2");
            var regenerate = below == null ? null : AccessTools.DeclaredMethod(below, nameof(SectionLayer.Regenerate));
            if (regenerate?.ReturnType != typeof(void))
                return;
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".openair-pipes");
            try
            {
                harmony.Patch(regenerate, transpiler: new HarmonyMethod(typeof(PipeProjection), nameof(UseWorldPipePrint)));
                OpenAirReady = IntegrationStatus.Registered(regenerate, harmony.Id);
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Open-air pipes", "AASB2 lower section", error);
                Log.Warning("[AASB2 Extended] Open-air pipe projection unavailable: " + error.GetBaseException().Message);
            }
        }

        private static void Bind(string package, string nameSpace)
        {
            if (!ModsConfig.IsActive(package))
                return;
            var building = AccessTools.TypeByName(nameSpace + ".Building_Pipe");
            var layerName = nameSpace is "Rimefeller" or "Rimatomics" ? "SectionLayer_ThingsPipe" : "SectionLayer_ThingsSewagePipe";
            var layer = building?.Assembly.GetType(nameSpace + "." + layerName);
            var nativePrint = building == null ? null : AccessTools.DeclaredMethod(building, "PrintForGrid", new[] { typeof(SectionLayer) });
            var nativeRegenerate = layer == null ? null : AccessTools.DeclaredMethod(layer, nameof(SectionLayer.Regenerate));
            if (building == null || !typeof(Building).IsAssignableFrom(building) ||
                nativePrint?.ReturnType != typeof(void) || nativeRegenerate?.ReturnType != typeof(void) ||
                !PatchProcessor.GetOriginalInstructions(nativeRegenerate).Any(code => code.Calls(nativePrint)))
                return;
            printers ??= new();
            printers.Add(building, HarmonyLib.MethodInvoker.GetHandler(nativePrint));
        }

        private static FastInvokeHandler WorldPrinter(Thing thing)
        {
            if (printers == null || thing is not Building)
                return null;
            var type = thing.GetType();
            if (printers.TryGetValue(type, out var print))
                return print;
            for (var parent = type.BaseType; parent != typeof(Building) && parent != null; parent = parent.BaseType)
                if (printers.TryGetValue(parent, out print))
                    break;
            printers[type] = print;
            return print;
        }

        internal static void Print(Thing thing, SectionLayer layer)
        {
            var pipe = WorldPrinter(thing);
            if (pipe == null || thing.def.drawerType is DrawerType.MapMeshOnly or DrawerType.MapMeshAndRealTime)
                thing.Print(layer);
            // The ordinary pipe layer calls PrintForGrid. Its implementation
            // selects the visible segments. Never call PrintForOverlay here.
            pipe?.Invoke(thing, layer);
        }

        private static DrawerType WorldDrawerType(Thing thing) =>
            thing.def.drawerType == DrawerType.None && WorldPrinter(thing) != null
                ? DrawerType.MapMeshOnly : thing.def.drawerType;

        private static IEnumerable<CodeInstruction> UseWorldPipePrint(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var def = AccessTools.Field(typeof(Thing), nameof(Thing.def));
            var drawer = AccessTools.Field(typeof(ThingDef), nameof(ThingDef.drawerType));
            var print = AccessTools.Method(typeof(Thing), nameof(Thing.Print), new[] { typeof(SectionLayer) });
            var kinds = code.Select((item, index) => (item, index))
                .Where(pair => pair.index > 0 && pair.item.LoadsField(drawer) && code[pair.index - 1].LoadsField(def))
                .Select(pair => pair.index).ToArray();
            var prints = code.Where(item => item.Calls(print)).ToArray();
            if (kinds.Length != 1 || prints.Length != 1)
                throw new InvalidOperationException("AASB2 lower object print contract changed.");

            int kind = kinds[0];
            code[kind - 1].opcode = OpCodes.Nop;
            code[kind - 1].operand = null;
            code[kind].opcode = OpCodes.Call;
            code[kind].operand = AccessTools.Method(typeof(PipeProjection), nameof(WorldDrawerType));
            prints[0].opcode = OpCodes.Call;
            prints[0].operand = AccessTools.Method(typeof(PipeProjection), nameof(Print));
            return code;
        }
    }
}
