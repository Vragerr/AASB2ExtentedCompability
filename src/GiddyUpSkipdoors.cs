using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class GiddyUpSkipdoors
    {
        internal const string Package = "memegoddess.giddyup";
        private static MethodInfo singleton, dataStore, tryData, mount;
        internal static bool Ready { get; }

        static GiddyUpSkipdoors()
        {
            if (!ModsConfig.IsActive(Package) || !ModsConfig.IsActive(SkiptechAutonomous.Package) ||
                !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            var storage = AccessTools.TypeByName("GiddyUp.ExtendedDataStorage");
            var data = storage?.Assembly.GetType("GiddyUp.ExtendedPawnData");
            if (storage == null || data == null)
                return;
            singleton = AccessTools.PropertyGetter(storage, "Singleton");
            dataStore = AccessTools.PropertyGetter(storage, "ExtendedPawnDataStore");
            tryData = dataStore == null ? null : dataStore.ReturnType.GetMethod("TryGetValue",
                new[] { typeof(int), data.MakeByRefType() });
            mount = AccessTools.PropertyGetter(data, "Mount");
            Ready = singleton?.IsStatic == true && singleton.ReturnType == storage &&
                tryData?.ReturnType == typeof(bool) && mount?.ReturnType == typeof(Pawn);
        }

        internal static Pawn MountOf(Pawn rider)
        {
            if (!Ready || rider == null || CompatibilityStatus.FloorSettings?.ProtectMountedSkipdoors == false ||
                singleton.Invoke(null, null) is not object storage || dataStore.Invoke(storage, null) is not object data)
                return null;
            var args = new object[] { rider.thingIDNumber, null };
            return (bool)tryData.Invoke(data, args) && args[1] != null ? (Pawn)mount.Invoke(args[1], null) : null;
        }

        internal static void SaveReport()
        {
            var pawn = Find.Selector?.SingleSelectedThing as Pawn;
            var selectedMount = MountOf(pawn);
            DiagnosticReports.Write("giddy-up-skipdoors",
                $"Giddy-up version: {ModLister.GetModWithIdentifier(Package, false)?.ModVersion}\n" +
                $"Mount contract available: {Ready}\nProtection enabled: {CompatibilityStatus.FloorSettings?.ProtectMountedSkipdoors}\n" +
                $"Selected pawn: {pawn?.ThingID ?? "-"}\nSelected mount: {selectedMount?.ThingID ?? "-"}");
        }
    }
}
