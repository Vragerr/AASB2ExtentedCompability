using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class GiddyUpSkipdoors
    {
        internal const string Package = "memegoddess.giddyup";
        private static MethodInfo singleton, dataStore, tryData, mount;
        private static Type mountedDriver;
        private static FastInvokeHandler riderOf;
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
            mountedDriver = storage.Assembly.GetType("GiddyUp.Jobs.JobDriver_Mounted");
            var rider = mountedDriver == null ? null : AccessTools.PropertyGetter(mountedDriver, "Rider");
            if (rider?.ReturnType == typeof(Pawn) && typeof(JobDriver).IsAssignableFrom(mountedDriver))
                riderOf = HarmonyLib.MethodInvoker.GetHandler(rider);
            Ready = riderOf != null && singleton?.IsStatic == true && singleton.ReturnType == storage &&
                tryData?.ReturnType == typeof(bool) && mount?.ReturnType == typeof(Pawn);
        }

        internal static Pawn MountOf(Pawn rider, bool respectSetting = true)
        {
            if (!Ready || rider == null || (respectSetting && CompatibilityStatus.FloorSettings?.ProtectMountedSkipdoors == false) ||
                singleton.Invoke(null, null) is not object storage || dataStore.Invoke(storage, null) is not object data)
                return null;
            var args = new object[] { rider.thingIDNumber, null };
            return (bool)tryData.Invoke(data, args) && args[1] != null ? (Pawn)mount.Invoke(args[1], null) : null;
        }

        internal static bool IsFollowing(Pawn rider, Pawn mountPawn) =>
            mountPawn?.jobs?.curDriver is JobDriver driver && mountedDriver?.IsInstanceOfType(driver) == true &&
            ReferenceEquals(riderOf(driver), rider);

        internal static void CompleteCrossing(Pawn rider, Pawn mountPawn)
        {
            if (!IsFollowing(rider, mountPawn) || mountPawn.Map != rider.Map || mountPawn.Position == rider.Position)
                return;
            // Giddy-up's mounted driver follows by assigning the rider's Position.
            // Do this before replanning at the exit, with no second Skiptech commit.
            mountPawn.Position = rider.Position;
            mountPawn.Notify_Teleported(false, true);
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
