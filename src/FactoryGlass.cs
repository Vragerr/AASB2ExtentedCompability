using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    // Factory's DrawAt can eject or destroy cargo. Read its visual inputs instead.
    [StaticConstructorOnStartup]
    internal static class FactoryGlass
    {
        private static readonly Type conveyor;
        private static readonly MethodInfo itemPosition, showItems;
        private static readonly FieldInfo dynamicBelt, singleDirection, height;
        internal static bool Ready => itemPosition != null;

        static FactoryGlass()
        {
            if (!ModsConfig.IsActive("vanillaexpanded.vfefactory"))
                return;
            var type = AccessTools.TypeByName("VanillaFurnitureExpandedFactory.Building_Conveyor");
            if (type == null || !typeof(IThingHolder).IsAssignableFrom(type))
                return;
            var position = AccessTools.DeclaredMethod(type, "CalculateItemPosition", Type.EmptyTypes);
            var show = AccessTools.DeclaredPropertyGetter(type, "ShowItems");
            var dynamic = AccessTools.DeclaredField(type, "isDrawnDynamically");
            var single = AccessTools.DeclaredField(type, "cachedIsSingleDirectional");
            var offset = AccessTools.DeclaredField(type, "cachedYOffset");
            if (position?.ReturnType != typeof(Vector3) || show?.ReturnType != typeof(bool) ||
                dynamic?.FieldType != typeof(bool) || single?.FieldType != typeof(bool) || offset?.FieldType != typeof(float))
            {
                Log.Warning("[AASB2 Extended] Factory glass graphics contract differs.");
                return;
            }
            conveyor = type;
            itemPosition = position;
            showItems = show;
            dynamicBelt = dynamic;
            singleDirection = single;
            height = offset;
        }

        internal static bool Includes(Thing thing) => conveyor?.IsInstanceOfType(thing) == true;

        internal static bool TryDraw(Thing thing)
        {
            if (!Includes(thing))
                return false;
            if ((bool)dynamicBelt.GetValue(thing))
            {
                var draw = thing.DrawPos;
                draw.y += (float)height.GetValue(thing);
                var rotation = (bool)singleDirection.GetValue(thing) ? Rot4.North : thing.Rotation;
                thing.Graphic?.Draw(draw, rotation, thing);
            }

            var contents = ((IThingHolder)thing).GetDirectlyHeldThings();
            if (contents?.Count > 0 && (bool)showItems.Invoke(thing, null))
            {
                var position = (Vector3)itemPosition.Invoke(thing, null);
                // Graphic submissions only, without Thing.DrawAt, warnings, or inventory changes.
                foreach (var item in contents)
                    item.Graphic?.Draw(position, item.Rotation, item);
            }
            return true;
        }
    }
}
