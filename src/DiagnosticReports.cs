using System;
using System.IO;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class DiagnosticReports
    {
        private static string Folder => Path.Combine(GenFilePaths.SaveDataFolderPath, "Diagnostics", "AASB2Extended");
        private static string latest;
        private static bool failed;

        internal static void Write(string topic, string report)
        {
            string pending = null;
            try
            {
                Directory.CreateDirectory(Folder);
                var now = DateTimeOffset.Now;
                string path = Path.Combine(Folder, $"{topic}-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
                pending = path + ".tmp";
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                {
                    writer.WriteLine($"Date: {now:O}");
                    writer.WriteLine($"RimWorld: {VersionControl.CurrentVersionStringWithRev}");
                    writer.WriteLine($"Mod: {ModLister.GetModWithIdentifier("vragerr.aasb2extendedcompatibility", false)?.ModVersion ?? "-"}");
                    writer.WriteLine($"Game tick: {Find.TickManager?.TicksGame.ToString() ?? "-"}; map: {Find.CurrentMap?.uniqueID.ToString() ?? "-"}");
                    writer.WriteLine();
                    writer.WriteLine(report);
                }
                File.Move(pending, path);
                pending = null;
                latest = path;
                failed = false;
                Log.Message("[AASB2Extended] Diagnostic report saved to " + path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                failed = true;
                Log.Warning("[AASB2Extended] Could not write diagnostic report: " + error.Message);
            }
            finally
            {
                if (pending != null)
                {
                    try { File.Delete(pending); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal static void DrawStatus(Listing_Standard listing)
        {
            if (failed)
                listing.Label("AASBExt.Diagnostics.Failed".Translate());
            if (latest != null)
                listing.Label("AASBExt.Diagnostics.Latest".Translate(latest));
            if (Directory.Exists(Folder) && listing.ButtonText("AASBExt.Diagnostics.Open".Translate()))
                Application.OpenURL(new Uri(Folder + Path.DirectorySeparatorChar).AbsoluteUri);
        }
    }
}
