using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SQLite;

namespace GameArtManager.Core.Sources
{
    /// <summary>
    /// GOG: installed-product names from the GOG Galaxy database.
    /// Names live in GamePieces (releaseKey "gog_<productId>", type 'originalTitle',
    /// value {"title": ...}); InstalledBaseProducts restricts to what is installed.
    /// The db is copied before reading because Galaxy keeps it locked in WAL mode.
    /// </summary>
    public static class GogSource
    {
        // NOTE: hardcoded — Environment.GetFolderPath(CommonApplicationData) is
        // virtualized to the app container's LocalState in UWP.
        private static string GalaxyDbPath =>
            Path.Combine(@"C:\ProgramData", "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

        private class PieceRow
        {
            public string ReleaseKey { get; set; }
            public string Value { get; set; }
        }

        private class InstalledRow
        {
            public long ProductId { get; set; }
        }

        public static List<string> Diagnostics { get; } = new List<string>();

        public static Dictionary<string, string> InstalledGames()
        {
            var games = new Dictionary<string, string>();
            Diagnostics.Clear();
            Diagnostics.Add("galaxy db exists: " + File.Exists(GalaxyDbPath) + " (" + GalaxyDbPath + ")");
            if (!File.Exists(GalaxyDbPath)) return games;

            var tempDb = Path.Combine(Path.GetTempPath(), $"gameart_gog_{Guid.NewGuid():N}.db");
            try
            {
                File.Copy(GalaxyDbPath, tempDb, overwrite: true);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    if (File.Exists(GalaxyDbPath + suffix))
                        File.Copy(GalaxyDbPath + suffix, tempDb + suffix, overwrite: true);
                }

                using (var db = new SQLiteConnection(tempDb, SQLiteOpenFlags.ReadOnly))
                {
                    var titles = new Dictionary<long, string>();
                    var cmd = db.CreateCommand(
                        "SELECT gp.releaseKey AS ReleaseKey, gp.value AS Value " +
                        "FROM GamePieces gp JOIN GamePieceTypes gpt ON gpt.id = gp.gamePieceTypeId " +
                        "WHERE gpt.type = 'originalTitle'");
                    foreach (var row in cmd.ExecuteQuery<PieceRow>())
                    {
                        if (row.ReleaseKey == null || !row.ReleaseKey.StartsWith("gog_")) continue;
                        if (!long.TryParse(row.ReleaseKey.Substring(4), out var productId)) continue;
                        try
                        {
                            var title = JObject.Parse(row.Value ?? "{}")["title"]?.Value<string>();
                            if (!string.IsNullOrEmpty(title)) titles[productId] = title;
                        }
                        catch
                        {
                            // malformed piece
                        }
                    }

                    var installed = new HashSet<long>();
                    try
                    {
                        foreach (var row in db.CreateCommand("SELECT productId AS ProductId FROM InstalledBaseProducts").ExecuteQuery<InstalledRow>())
                            installed.Add(row.ProductId);
                    }
                    catch
                    {
                        installed = new HashSet<long>(titles.Keys); // schema drift: fall back to all known
                    }

                    foreach (var id in installed)
                    {
                        if (titles.TryGetValue(id, out var title))
                            games[id.ToString()] = title;
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Add("galaxy db read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                TryDelete(tempDb);
                TryDelete(tempDb + "-wal");
                TryDelete(tempDb + "-shm");
            }
            return games;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }
    }
}
