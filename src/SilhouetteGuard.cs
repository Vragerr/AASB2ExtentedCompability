using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class SilhouetteGuard
    {
        private static MethodInfo shouldDraw;
        internal static bool Ready { get; }
        private static bool reportedMissingGraphic;

        static SilhouetteGuard()
        {
            if (!ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            var owner = CompatibilityStatus.HarmonyId + ".silhouettes";
            var harmony = new Harmony(owner);
            try
            {
                var target = AccessTools.DeclaredMethod(typeof(DynamicDrawManager), "DrawSilhouettes");
                var args = target?.GetParameters();
                var buffer = args?.Length == 1 ? args[0].ParameterType : null;
                var entry = buffer?.IsGenericType == true ? buffer.GetGenericArguments()[0] : null;
                shouldDraw = AccessTools.DeclaredMethod(typeof(SilhouetteUtility), nameof(SilhouetteUtility.ShouldDrawSilhouette),
                    new[] { typeof(Thing) });
                var graphic = AccessTools.PropertyGetter(typeof(PawnRenderer), nameof(PawnRenderer.SilhouetteGraphic));
                if (target?.ReturnType != typeof(void) || target.IsStatic ||
                    entry?.DeclaringType != typeof(DynamicDrawManager) || entry.Name != "ThingCullDetails" ||
                    buffer.GetGenericTypeDefinition().FullName != "Unity.Collections.NativeArray`1" ||
                    shouldDraw?.ReturnType != typeof(bool) || !shouldDraw.IsStatic || graphic?.ReturnType != typeof(Graphic))
                    throw new InvalidOperationException("DynamicDrawManager silhouette contract differs.");
                var body = PatchProcessor.GetOriginalInstructions(target);
                int graphicIndex = body.FindIndex(code => code.Calls(graphic));
                if (graphicIndex < 0 || graphicIndex + 1 >= body.Count ||
                    body.FindAll(code => code.Calls(graphic)).Count != 1 ||
                    body[graphicIndex + 1].opcode != OpCodes.Ldfld ||
                    !Equals(body[graphicIndex + 1].operand, AccessTools.Field(typeof(Graphic), nameof(Graphic.drawSize))))
                    throw new InvalidOperationException("Silhouette graphic dereference differs from expected IL.");
                var patch = new HarmonyMethod(typeof(SilhouetteGuard), nameof(GuardSilhouette));
                harmony.Patch(target, transpiler: patch);
                if (!IntegrationStatus.Registered(target, owner, patch.method))
                    throw new InvalidOperationException("Silhouette guard did not register.");
                Ready = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(owner);
                IntegrationStatus.NoteFailure("Missing pawn silhouette", "DrawSilhouettes", error);
                Log.Warning("[AASB2 Extended] Silhouette guard unavailable: " + error.Message);
            }
        }

        private static IEnumerable<CodeInstruction> GuardSilhouette(IEnumerable<CodeInstruction> instructions)
        {
            var body = new List<CodeInstruction>(instructions);
            int index = body.FindIndex(code => code.Calls(shouldDraw));
            if (index < 0 || index + 1 >= body.Count ||
                body.FindAll(code => code.Calls(shouldDraw)).Count != 1 ||
                (body[index + 1].opcode != OpCodes.Brfalse &&
                 body[index + 1].opcode != OpCodes.Brfalse_S))
                throw new InvalidOperationException("Silhouette eligibility branch differs from expected IL.");
            body[index].operand = AccessTools.DeclaredMethod(typeof(SilhouetteGuard), nameof(CanDraw));
            return body;
        }

        private static bool CanDraw(Thing thing)
        {
            if (thing == null || !SilhouetteUtility.ShouldDrawSilhouette(thing))
                return false;
            if (CompatibilityStatus.FloorSettings?.GuardMissingSilhouettes == false || !(thing is Pawn pawn) || pawn.Drawer?.renderer?.SilhouetteGraphic != null)
                return true;
            if (!reportedMissingGraphic)
            {
                reportedMissingGraphic = true;
                Log.Warning("[AASB2 Extended] Skipped a pawn silhouette with no initialized graphic. def=" +
                    pawn.def?.defName + " kind=" + pawn.kindDef?.defName + " thing=" + pawn.ThingID);
            }
            return false;
        }
    }
}
