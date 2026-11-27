using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class VerticalVentOpening
    {
        internal static string Check(Map map, IntVec3 upper, Thing ignore, out IntVec3 lower)
        {
            lower = IntVec3.Invalid;
            if (!BandCompatibility.IsBanded(map) || !upper.InBounds(map) ||
                !BandCompatibility.TryAdjacent(map, upper, false, out lower) || !lower.InBounds(map) ||
                BandCompatibility.Level(map, lower) < 0 ||
                BandCompatibility.Level(map, upper) != BandCompatibility.Level(map, lower) + 1)
                return "AASBExt.Vent.NoPair";

            var roof = map.roofGrid.RoofAt(lower);
            if (roof?.isNatural == true)
                return "AASBExt.Vent.NaturalRoof";

            var grid = map.terrainGrid;
            if (roof == null || (grid.TerrainAt(upper)?.defName != "AB_RoofSurface" &&
                grid.UnderTerrainAt(upper)?.defName != "AB_RoofSurface" &&
                grid.BaseTerrainAt(upper)?.defName != "AB_RoofSurface"))
                return "AASBExt.Vent.RequiresConstructedRoof";

            if (map.edificeGrid[lower]?.def.Fillage == FillCategory.Full)
                return "AASBExt.Vent.BlockedBelow";
            foreach (var thing in upper.GetThingList(map))
                if (thing is Building building && thing != ignore &&
                    (building.def.Fillage != FillCategory.None || building.def.coversFloor))
                    return "AASBExt.Vent.BlockedAbove";
            return null;
        }
    }

    public sealed class PlaceWorker_VerticalVent : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map,
            Thing thingToIgnore = null, Thing thing = null)
        {
            var error = VerticalVentOpening.Check(map, loc, thingToIgnore ?? thing, out _);
            return error == null ? (AcceptanceReport)true : new AcceptanceReport(error.Translate());
        }
    }

    public sealed class Building_VerticalVent : Building
    {
        private int lastUpdate = -1;
        private float lastExchange;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<VerticalVentExchange>().Register(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map?.GetComponent<VerticalVentExchange>().Unregister(this);
            base.DeSpawn(mode);
        }

        private string State(out Room above, out Room below)
        {
            above = below = null;
            if (!Spawned)
                return "AASBExt.Vent.NoPair";
            var error = VerticalVentOpening.Check(Map, Position, this, out var lower);
            if (lower.InBounds(Map))
            {
                above = Position.GetRoom(Map);
                below = lower.GetRoom(Map);
            }
            if (CompatibilityStatus.FloorSettings?.EnableVentExchange == false)
                return "AASBExt.Vent.Disabled";
            if (!FlickUtility.WantsToBeOn(this))
                return "AASBExt.Vent.Closed";
            if (error != null)
                return error;
            if (above == null || above.UsesOutdoorTemperature || above.IsDoorway)
                return "AASBExt.Vent.EncloseAbove";
            if (below == null || below.UsesOutdoorTemperature || below.IsDoorway)
                return "AASBExt.Vent.EncloseBelow";
            if (above == below || above.CellCount < 1 || below.CellCount < 1)
                return "AASBExt.Vent.NoPair";
            return null;
        }

        public override string GetInspectString()
        {
            var original = base.GetInspectString();
            var error = State(out var above, out var below);
            string line = error == null
                ? "AASBExt.Vent.Exchange".Translate(above.Temperature.ToStringTemperature(), below.Temperature.ToStringTemperature())
                : error.Translate();
            if (error != null && above != null && below != null)
                line += "\n" + "AASBExt.Vent.Temperatures".Translate(
                    above.Temperature.ToStringTemperature(), below.Temperature.ToStringTemperature());
            return string.IsNullOrEmpty(original) ? line : original + "\n" + line;
        }

        internal void Exchange()
        {
            lastUpdate = Find.TickManager.TicksGame;
            lastExchange = 0f;
            if (State(out var above, out var below) != null)
                return;
            int aboveSize = above.CellCount;
            int belowSize = below.CellCount;
            float aboveTemp = above.Temperature;
            float belowTemp = below.Temperature;
            // RimWorld stores one temperature per Room. Weight both writes by the same cell counts.
            float exchange = (aboveTemp - belowTemp) * 0.12f * System.Math.Min(aboveSize, belowSize);
            if (!float.IsFinite(exchange))
                return;
            above.Temperature = aboveTemp - exchange / aboveSize;
            below.Temperature = belowTemp + exchange / belowSize;
            lastExchange = exchange;
        }

        internal void AppendReport(StringBuilder output)
        {
            var state = State(out var above, out var below);
            VerticalVentOpening.Check(Map, Position, this, out var lower);
            output.AppendLine().Append(ThingID).Append(" upper=").Append(Position).Append(" lower=").Append(lower)
                .Append(" roof=").Append(lower.IsValid && lower.InBounds(Map) ? Map.roofGrid.RoofAt(lower)?.defName : "-")
                .Append(" state=").Append(state ?? "exchange")
                .Append(" upper room=").Append(above?.ID).Append(" temperature=").Append(above?.Temperature)
                .Append(" cells=").Append(above?.CellCount)
                .Append(" lower room=").Append(below?.ID).Append(" temperature=").Append(below?.Temperature)
                .Append(" cells=").Append(below?.CellCount)
                .Append(" last update=").Append(lastUpdate).Append(" last exchange=").Append(lastExchange);
        }
    }

    public sealed class VerticalVentExchange : MapComponent
    {
        private readonly List<Building_VerticalVent> vents = new();

        public VerticalVentExchange(Map map) : base(map) { }

        internal void Register(Building_VerticalVent vent)
        {
            if (!vents.Contains(vent))
                vents.Add(vent);
        }

        internal void Unregister(Building_VerticalVent vent) => vents.Remove(vent);

        public override void MapComponentTick()
        {
            // MapPostTick runs after MapPreTick's native room equalization and building ticks.
            if (vents.Count == 0 || Find.TickManager.TicksGame % 250 != 0 ||
                CompatibilityStatus.FloorSettings?.EnableVentExchange == false)
                return;
            foreach (var vent in vents)
                vent.Exchange();
        }

        internal void SaveReport()
        {
            var output = new StringBuilder("Vertical ventilation grates: ").Append(vents.Count);
            foreach (var vent in vents)
                vent.AppendReport(output);
            DiagnosticReports.Write("vertical-ventilation", output.ToString());
        }
    }
}
