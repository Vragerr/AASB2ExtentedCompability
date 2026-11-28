using System;
using System.Reflection;
using System.Text;
using Verse;
using LudeonTK;

namespace AASB2ExtendedCompatibility
{
    internal static class UtilitiesDiagnostics
    {
        private const string Dependency = "astryl.asabovesobelow2";
        private const BindingFlags StaticMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [DebugAction("AASB2 Extended", "Utility snapshot at cell")]
        private static void BeginCellSnapshot()
        {
            DebugTools.curTool = new DebugTool("AASBExt.Status.UtilityCell".Translate(), () =>
            {
                DebugTools.curTool = null;
                var map = Find.CurrentMap;
                if (map == null)
                    return;
                var cell = UI.MouseCell();
                if (cell.InBounds(map))
                    LogSnapshot(map, cell);
            }, null);
        }

        internal static void LogSnapshot()
        {
            var selected = Find.Selector?.SingleSelectedThing;
            LogSnapshot(selected?.Map, selected?.Position ?? IntVec3.Invalid);
        }

        private static void LogSnapshot(Map map, IntVec3 cell)
        {
            if (!ModsConfig.IsActive(Dependency))
                return;

            var report = new StringBuilder("[AASB2 Extended] Utility snapshot");
            var assembly = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "AsAboveSoBelow");
            if (assembly == null)
            {
                DiagnosticReports.Write("utilities", report.Append(": AASB2 assembly unavailable").ToString());
                return;
            }

            AppendCall(report, assembly, "ABColumnNetworks", "Report");
            if (RimefellerRisers.Ready)
                report.AppendLine().Append(RimefellerRisers.Status(map));
            if (DbhRiserLoad.Ready)
                report.AppendLine().Append(DbhRiserLoad.Status(map));
            if (ModsConfig.IsActive("dubwise.dubsbadhygiene") || ModsConfig.IsActive("dubwise.dubscentralheating"))
                AppendCall(report, assembly, "Patch_DubsPipes_ABRiserLink", "CounterReport");

            var carrier = DefDatabase<ThingDef>.GetNamedSilentFail("AB_Carrier_VEF_AB_PropaneNet");
            var guest = DefDatabase<ThingDef>.GetNamedSilentFail("BabySleepingSpotGuest");
            report.AppendLine().Append("shortHash: ").Append(DescribeHash(carrier)).Append("; ").Append(DescribeHash(guest));
            if (carrier != null && guest != null)
                report.Append("; collision=").Append(carrier.shortHash == guest.shortHash);

            if (map != null && cell.IsValid && cell.InBounds(map))
                AppendSelectedPair(report, assembly, cell, map);
            else
                report.AppendLine().Append("No cell selected; registry only.");

            if (ModsConfig.IsActive(DchRiser.Package))
                report.AppendLine().Append(DchRiser.Status(map));
            DiagnosticReports.Write("utilities", report.ToString());
        }

        private static void AppendCall(StringBuilder report, Assembly assembly, string type, string name)
        {
            var method = GetMethod(assembly, type, name, typeof(string), Type.EmptyTypes);
            report.AppendLine().Append(type).Append('.').Append(name).Append(": ");
            try
            {
                report.Append(method == null ? "signature unavailable" : (string)method.Invoke(null, null));
            }
            catch (TargetInvocationException error)
            {
                report.Append("failed: ").Append(error.InnerException?.GetType().Name ?? error.GetType().Name);
            }
        }

        private static void AppendSelectedPair(StringBuilder report, Assembly assembly, IntVec3 cell, Map map)
        {
            var band = GetMethod(assembly, "ABBands", "BandOf", typeof(int), new[] { typeof(Map), typeof(IntVec3) });
            var coordinateRef = typeof(IntVec3).MakeByRefType();
            var pair = GetMethod(assembly, "ABColumnLink", "TryPartnerCells", typeof(bool),
                new[] { typeof(Map), typeof(IntVec3), coordinateRef, coordinateRef });
            if (band == null || pair == null)
            {
                report.Append("; band/partner signature unavailable");
                return;
            }

            try
            {
                AppendCell(report, "origin", map, cell, band);
                var args = new object[] { map, cell, IntVec3.Invalid, IntVec3.Invalid };
                if (!(bool)pair.Invoke(null, args))
                {
                    report.Append("; no partner cells resolved");
                    return;
                }
                AppendCell(report, "up", map, (IntVec3)args[2], band);
                AppendCell(report, "down", map, (IntVec3)args[3], band);
            }
            catch (TargetInvocationException error)
            {
                report.Append("; query failed: ").Append(error.InnerException?.GetType().Name ?? error.GetType().Name);
            }
        }

        private static void AppendCell(StringBuilder report, string direction, Map map, IntVec3 cell, MethodInfo band)
        {
            report.AppendLine().Append(direction).Append(": ").Append(cell);
            if (!cell.IsValid || !cell.InBounds(map))
                return;

            report.Append("; band=").Append(band.Invoke(null, new object[] { map, cell })).Append("; utility defs=");
            var found = false;
            foreach (var thing in cell.GetThingList(map))
            {
                var id = thing.def.defName;
                if (!id.StartsWith("AB_Carrier_", StringComparison.Ordinal) &&
                    !id.StartsWith("AB_Column", StringComparison.Ordinal) &&
                    !id.StartsWith("AB_WallRiser", StringComparison.Ordinal) && !HasNativeNetwork(thing))
                    continue;
                if (found)
                    report.Append(", ");
                found = true;
                report.Append(id);
                if (HarmonyLib.AccessTools.Field(thing.GetType(), "enabled")?.GetValue(thing) is
                    System.Collections.Generic.IEnumerable<string> enabledNetworks)
                    report.Append(" enabled=[").Append(string.Join(", ", enabledNetworks)).Append(']');
                AppendNetwork(report, thing);
            }
            if (!found)
                report.Append("(none)");
        }

        private static bool HasNativeNetwork(Thing thing)
        {
            if (!(thing is ThingWithComps building))
                return false;
            foreach (var comp in building.AllComps)
                if (NetworkComponentType(comp.GetType()) != null)
                    return true;
            return false;
        }

        private static Type NetworkComponentType(Type type)
        {
            while (type != null)
            {
                if (type.FullName == "DubsBadHygiene.CompPipe" ||
                    type.FullName == "DubsCentralHeating.CompPipe" ||
                    type.FullName == "Rimefeller.CompPipe" ||
                    type.FullName == "PipeSystem.CompResource" ||
                    type.FullName == "EccentricPower.CompFusionPipe" ||
                    type.FullName == "Rimatomics.CoolingSystem")
                    return type;
                type = type.BaseType;
            }
            return null;
        }

        private static void AppendNetwork(StringBuilder report, Thing thing)
        {
            if (!(thing is ThingWithComps building))
                return;

            foreach (var comp in building.AllComps)
            {
                var type = NetworkComponentType(comp.GetType());
                if (type == null)
                    continue;

                bool dbh = type.FullName == "DubsBadHygiene.CompPipe";
                var vef = type.FullName == "PipeSystem.CompResource";
                string getter = vef ? "PipeNet" : type.FullName == "EccentricPower.CompFusionPipe"
                    ? "Network" : type.FullName == "Rimatomics.CoolingSystem" ? "CoolingNet" : "pipeNet";
                var property = type.GetProperty(getter,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var expected = vef ? "PipeSystem.PipeNet" :
                    type.FullName == "Rimefeller.CompPipe" ? "Rimefeller.PipelineNet" :
                    getter == "Network" ? "EccentricPower.FusionNetwork" :
                    getter == "CoolingNet" ? "Rimatomics.CoolingNet" :
                    type.FullName == "DubsCentralHeating.CompPipe" ? "DubsCentralHeating.PlumbingNet" : "DubsBadHygiene.PlumbingNet";
                report.Append(" [comp=").Append(type.FullName);
                // The DBH getter can regenerate a dirty grid. Read its existing reference
                // so taking a report does not repair the state we are trying to capture.
                var netRef = dbh ? HarmonyLib.AccessTools.Field(type, "pipeNetRef") : null;
                try
                {
                    if (dbh)
                        report.Append(" mode=").Append(HarmonyLib.AccessTools.Property(type, "mode")?.GetValue(comp))
                            .Append(" closed=").Append(HarmonyLib.AccessTools.Property(type, "closed")?.GetValue(comp));
                    report.Append("]");
                    if (dbh ? netRef?.FieldType.FullName != expected :
                        property?.PropertyType.FullName != expected || property.GetGetMethod(true) == null)
                    {
                        report.Append(" [network contract unavailable]");
                        continue;
                    }
                    var net = dbh ? netRef.GetValue(comp) : property.GetValue(comp);
                    report.Append(" [").Append(expected).Append('=')
                        .Append(net == null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(net).ToString())
                        .Append(']');
                    if (net == null)
                        report.Append(" [no native network]");
                    else
                        AppendStorage(report, net, expected);
                }
                catch (TargetInvocationException error)
                {
                    report.Append(" [network query failed: ").Append(error.InnerException?.GetType().Name).Append(']');
                }
            }
        }

        private static void AppendStorage(StringBuilder report, object net, string type)
        {
            if (type == "DubsCentralHeating.PlumbingNet" || type == "DubsBadHygiene.PlumbingNet")
                foreach (string field in new[] { "BoilerCapacitySum", "HeatStoreCapacitySum", "HeatingCap", "HeatStores", "Radiators", "HotWaterTanks" })
                {
                    var info = HarmonyLib.AccessTools.Field(net.GetType(), field);
                    var value = info?.GetValue(net);
                    if (value is float number)
                        report.Append(" ").Append(field).Append("=").Append(number);
                    else if (value is System.Collections.ICollection members)
                        report.Append(" ").Append(field).Append(" count=").Append(members.Count);
                }
            if (type == "DubsBadHygiene.PlumbingNet")
                foreach (string field in new[] { "WaterWells", "WaterPumps", "WaterTowers", "WaterUsers",
                    "Sewers", "Toilets", "WaterCap", "WaterPumpedPerTick" })
                {
                    var value = HarmonyLib.AccessTools.Field(net.GetType(), field)?.GetValue(net);
                    if (value is System.Collections.ICollection members)
                        report.Append(" ").Append(field).Append(" count=").Append(members.Count);
                    else if (value is float number)
                        report.Append(" ").Append(field).Append("=").Append(number);
                }
            var metrics = type == "DubsBadHygiene.PlumbingNet" ? new[] { "WaterStorage" } :
                type == "Rimefeller.PipelineNet" ? new[] { "TotalFuelNow" } :
                type == "PipeSystem.PipeNet" ? new[] { "Stored", "AvailableCapacity",
                    "TotalProductionThisTick", "TotalConsumptionThisTick" } : Array.Empty<string>();
            foreach (var metric in metrics)
            {
                var getter = net.GetType().GetProperty(metric, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getter?.GetMethod != null && (getter.PropertyType == typeof(float) || getter.PropertyType == typeof(double)))
                    report.Append(" ").Append(metric).Append("=").Append(getter.GetValue(net));
            }
        }

        private static MethodInfo GetMethod(Assembly assembly, string type, string name, Type result, Type[] args)
        {
            var method = assembly.GetType("AsAboveSoBelow." + type, throwOnError: false)
                ?.GetMethod(name, StaticMethods, null, args, null);
            return method?.ReturnType == result ? method : null;
        }

        private static string DescribeHash(ThingDef def)
            => def == null ? "(def absent)" : def.defName + "=" + def.shortHash;
    }
}

