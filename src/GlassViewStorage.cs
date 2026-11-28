using System;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class GlassViewStorage
    {
        private static readonly Func<Thing, Thing> storingBuilding;
        private static readonly Type buildingType;

        static GlassViewStorage()
        {
            if (!ModsConfig.IsActive("adaptive.storage.framework"))
                return;

            var building = AccessTools.TypeByName("AdaptiveStorage.ThingClass");
            var extensions = building?.Assembly.GetType("AdaptiveStorage.Utility.ThingExtensions");
            var lookup = extensions == null ? null : AccessTools.DeclaredMethod(extensions,
                "StoringAdaptiveStorage", new[] { typeof(Thing) });
            var draw = building == null ? null : AccessTools.DeclaredMethod(building, nameof(Thing.DynamicDrawPhaseAt),
                new[] { typeof(DrawPhase), typeof(UnityEngine.Vector3), typeof(bool) });
            if (lookup?.IsStatic != true || lookup.ReturnType != building ||
                !typeof(Thing).IsAssignableFrom(building) || draw?.ReturnType != typeof(void))
                return;
            storingBuilding = lookup.CreateDelegate<Func<Thing, Thing>>();
            buildingType = building;
        }

        internal static bool IsStored(Thing thing) =>
            storingBuilding != null && thing.def.category == ThingCategory.Item && storingBuilding(thing) != null;

        internal static bool HasNativeDraw(Thing thing) =>
            buildingType != null && thing.def.drawerType != DrawerType.MapMeshOnly &&
            buildingType.IsInstanceOfType(thing);
    }
}
