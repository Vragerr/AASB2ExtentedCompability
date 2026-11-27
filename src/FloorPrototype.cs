using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class FloorPrototypeSettings : ModSettings
    {
        public bool EnablePlacement;
        public bool EnableVisual;
        public bool EnableFactoryPorts;
        public bool EnableDubsSkylights = true;
        public bool EnableEndlessHorizon = true;
        public bool EnableSkiptech = true;
        public bool EnableGlassLight = true;
        public bool EnableVentExchange = true;
        public bool ProtectColumnPipes = true;
        public bool ProtectMountedSkipdoors = true;
        public bool GuardMissingSilhouettes = true;
        public bool LimitAnimaTrees;
        public int MaxAnimaTrees = 1;
        public bool AnimaSurfaceOnly;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EnablePlacement, "enableExperimentalFloorPlacement", false);
            Scribe_Values.Look(ref EnableVisual, "enableExperimentalGlassTerrainView", false);
            Scribe_Values.Look(ref EnableFactoryPorts, "enableVerticalFactoryPorts", false);
            Scribe_Values.Look(ref EnableDubsSkylights, "enableDubsSkylights", true);
            Scribe_Values.Look(ref EnableEndlessHorizon, "enableEndlessHorizon", true);
            Scribe_Values.Look(ref EnableSkiptech, "enableSkiptech", true);
            Scribe_Values.Look(ref EnableGlassLight, "enableGlassLight", true);
            Scribe_Values.Look(ref EnableVentExchange, "enableVentExchange", true);
            Scribe_Values.Look(ref ProtectMountedSkipdoors, "protectMountedSkipdoors", true);
            Scribe_Values.Look(ref GuardMissingSilhouettes, "guardMissingSilhouettes", true);
            Scribe_Values.Look(ref ProtectColumnPipes, "protectColumnPipes", true);
            Scribe_Values.Look(ref LimitAnimaTrees, "limitAnimaTrees", false);
            Scribe_Values.Look(ref MaxAnimaTrees, "maxAnimaTrees", 1);
            Scribe_Values.Look(ref AnimaSurfaceOnly, "animaSurfaceOnly", false);
            MaxAnimaTrees = UnityEngine.Mathf.Clamp(MaxAnimaTrees, 1, 50);
        }
    }

    public sealed class PlaceWorker_ExperimentalGlassFloor : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map,
            Thing thingToIgnore = null, Thing thing = null)
        {
            if (CompatibilityStatus.FloorSettings?.EnablePlacement != true)
                return new AcceptanceReport("AASBExt.Floor.Disabled".Translate());
            if (map == null || !loc.InBounds(map) ||
                map.terrainGrid.TerrainAt(loc)?.defName != "AB_RoofSurface")
                return new AcceptanceReport("AASBExt.Floor.RequiresRoof".Translate());
            return true;
        }
    }
}
