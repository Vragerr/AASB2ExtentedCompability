using System;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class PlaceWorker_DbhCarrier : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map,
            Thing thingToIgnore = null, Thing thing = null)
        {
            if (map == null || !loc.InBounds(map) || !ModsConfig.IsActive("astryl.asabovesobelow2") ||
                !ModsConfig.IsActive("dubwise.dubsbadhygiene"))
                return true;

            var defName = checkingDef.defName;
            if (defName != "sewagePipeStuff" && defName != "sewagePipeHidden")
                return true;

            foreach (var candidate in loc.GetThingList(map))
                if (candidate != thingToIgnore && candidate != thing &&
                    candidate.def.defName.Equals("AB_Carrier_DubsBadHygiene_Sewage", StringComparison.Ordinal))
                    return new AcceptanceReport("AASBExt.Placement.DbhCarrier".Translate());

            return true;
        }
    }
}
