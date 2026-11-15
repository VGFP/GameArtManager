using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace GameArtManager.Core
{
    /// <summary>
    /// Shared application engine used by both the Game Bar widget and the main window.
    /// </summary>
    public class AppEngine
    {
        public static AppEngine Instance { get; } = new AppEngine();

        public Settings Settings { get; private set; }
        public XboxLibrary Library { get; } = new XboxLibrary();
        public List<GameEntry> Entries { get; private set; } = new List<GameEntry>();
        public Matcher Matcher { get; private set; }

        public event Action EntriesChanged;
        public event Action<string> StatusMessage;

        private readonly MatchCache _cache = new MatchCache();

        private AppEngine()
        {
            Settings = SettingsStore.Load();
        }

        public bool ApiKeyMissing => string.IsNullOrEmpty(Settings.ApiKey);

        public SgdbClient Client => Matcher.Client;

        public void EnsureMatcher()
        {
            if (Matcher != null) return;
            Matcher = new Matcher(new SgdbClient(Settings.ApiKey), Settings);
            Matcher.LoadLocalSources();
        }

        public void SaveSettings(Settings settings)
        {
            Settings = settings;
            SettingsStore.Save(settings);
            Matcher = null; // rebuild with the new settings/key
        }

        /// <summary>Load a game's cached art as a bitmap (plain file stream — avoids
        /// StorageFile/broker quirks inside the app container).</summary>
        public static async Task<BitmapImage> LoadThumbnailAsync(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var stream = fs.AsRandomAccessStream())
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        public async Task ScanAsync()
        {
            StatusMessage?.Invoke("Scanning Xbox app library…");
            var entries = await Task.Run(() => Library.Scan());

            // apply cached match info without touching the network
            foreach (var entry in entries)
            {
                var cached = _cache.Get(entry.CacheKey);
                if (cached != null)
                {
                    entry.SgdbId = cached.SgdbId;
                    entry.SgdbName = cached.SgdbName;
                    entry.MatchMethod = "cache";
                }
                if (Matcher != null) entry.LocalName = Matcher.LocalName(entry);
            }

            // resolve local launcher names without needing the SGDB API
            await Task.Run(() => ResolveLocalNames(entries));

            Entries = entries;
            if (entries.Count == 0)
            {
                var notes = string.Join(" | ", Library.LastScanNotes.Take(4));
                StatusMessage?.Invoke($"Found 0 games. Diagnostics: {notes}");
            }
            else
            {
                StatusMessage?.Invoke($"Found {entries.Count} games ({entries.Count(e => e.FromFilename && !e.FromManifest)} via filename fallback).");
            }

            // full diagnostics for out-of-app inspection
            try
            {
                var dump = new System.Text.StringBuilder();
                dump.AppendLine("Root: " + Library.Root);
                dump.AppendLine("Entries: " + entries.Count);
                dump.AppendLine("steam names: " + (_steamNames?.Count.ToString() ?? "null"));
                dump.AppendLine("epic entries: " + (_epic != null ? (_epic.ByItemCount.ToString()) : "null"));
                dump.AppendLine("gog names: " + (_gogNames?.Count.ToString() ?? "null"));
                foreach (var d in Sources.SteamSource.Diagnostics) dump.AppendLine("  [steam] " + d);
                foreach (var d in Sources.GogSource.Diagnostics) dump.AppendLine("  [gog] " + d);

                // write probe: can the container write into the Xbox cache?
                try
                {
                    var probe = Path.Combine(Library.Root, ".write_probe");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                    dump.AppendLine("write probe: SUCCESS");
                }
                catch (Exception wex)
                {
                    dump.AppendLine("write probe: FAILED - " + wex.GetType().Name + ": " + wex.Message);
                }

                foreach (var note in Library.LastScanNotes) dump.AppendLine("  " + note);
                File.WriteAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "scan_log.txt"), dump.ToString());
            }
            catch
            {
                // diagnostics only
            }

            EntriesChanged?.Invoke();
        }

        private Dictionary<string, string> _steamNames;
        private Sources.EpicSource _epic;
        private Dictionary<string, string> _gogNames;

        /// <summary>Fill LocalName from launcher data (no API key needed).</summary>
        private void ResolveLocalNames(List<GameEntry> entries)
        {
            if (_steamNames == null) { try { _steamNames = Sources.SteamSource.InstalledGames(); } catch { _steamNames = new Dictionary<string, string>(); } }
            if (_epic == null) { try { _epic = new Sources.EpicSource(); } catch { _epic = new Sources.EpicSource(); } }
            if (_gogNames == null) { try { _gogNames = Sources.GogSource.InstalledGames(); } catch { _gogNames = new Dictionary<string, string>(); } }

            foreach (var entry in entries)
            {
                string local = null;
                try
                {
                    switch (entry.Platform)
                    {
                        case "steam":
                            _steamNames.TryGetValue(entry.ExternalId, out local);
                            break;
                        case "epic":
                            local = _epic.Lookup(entry.ExternalId);
                            break;
                        case "gog":
                            _gogNames.TryGetValue(entry.ExternalId, out local);
                            break;
                        default:
                            if (entry.Name != "Unknown") local = entry.Name;
                            break;
                    }
                }
                catch
                {
                    // keep null
                }
                entry.LocalName = local;
            }
        }

        public async Task AutoMatchAsync(IProgress<(int done, int total)> progress)
        {
            EnsureMatcher();
            StatusMessage?.Invoke("Matching…");
            await Task.Run(() => Matcher.MatchManyAsync(Entries, progress));
            var summary = $"Matching complete: {Matcher.MatchedCount} matched.";
            if (Matcher.LastSgdbError != null)
                summary += $" Some lookups failed — last API error: {Matcher.LastSgdbError}";
            StatusMessage?.Invoke(summary);
            EntriesChanged?.Invoke();
        }

        public async Task<int> ApplyBestAsync(IEnumerable<GameEntry> targets, IProgress<(int done, int total)> progress)
        {
            EnsureMatcher();
            var list = targets.Where(e => e.SgdbId.HasValue).ToList();
            var applied = 0;
            var total = list.Count;
            var done = 0;
            foreach (var entry in list)
            {
                try
                {
                    var art = await Client.BestSquareArtAsync(entry.SgdbId.Value, Settings);
                    if (art != null)
                    {
                        var png = await Imaging.DownloadSquarePngAsync(art.Url);
                        Library.ReplaceImage(entry, png);
                        applied++;
                    }
                    else
                    {
                        StatusMessage?.Invoke($"No artwork found for {entry.DisplayName}");
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage?.Invoke($"Failed for {entry.DisplayName}: {ex.Message}");
                }
                finally
                {
                    done++;
                    progress?.Report((done, total));
                }
            }
            EntriesChanged?.Invoke();
            return applied;
        }

        public async Task<List<SgdbGame>> SearchAsync(string term)
        {
            EnsureMatcher();
            return await Client.SearchAsync(term);
        }

        public async Task<Dictionary<string, List<SgdbArt>>> LoadArtAsync(long gameId)
        {
            EnsureMatcher();
            return await Client.AllArtAsync(gameId, Settings);
        }

        /// <summary>Tile art for the picker: HQ icons first, then square grids; when a
        /// game has neither, its portrait covers (applied as padded tiles).</summary>
        public async Task<List<SgdbArt>> LoadTileArtAsync(long gameId)
        {
            EnsureMatcher();
            var tiles = new List<SgdbArt>();
            tiles.AddRange((await Client.IconsAsync(gameId, Settings)).OrderByDescending(a => a.Score));
            tiles.AddRange(await Client.SquareGridsAsync(gameId, Settings));
            if (tiles.Count == 0)
                tiles.AddRange(await Client.PortraitCoversAsync(gameId, Settings));
            return tiles;
        }

        public async Task UseArtAsync(GameEntry entry, SgdbGame game, SgdbArt art)
        {
            EnsureMatcher();
            Matcher.SetManual(entry, game);
            var png = await Imaging.DownloadSquarePngAsync(art.Url);
            Library.ReplaceImage(entry, png);
            EntriesChanged?.Invoke();
        }

        public Task UseCustomImageAsync(GameEntry entry, string imagePath)
        {
            return Task.Run(() =>
            {
                var png = Imaging.FileToPng(imagePath);
                Library.ReplaceImage(entry, png);
                EntriesChanged?.Invoke();
            });
        }

        public bool Restore(GameEntry entry) => Library.RestoreOriginal(entry);

        /// <summary>Clear a wrong match (cache + entry) so the game can be re-matched manually.</summary>
        public void ClearMatch(GameEntry entry)
        {
            _cache.Remove(entry.CacheKey);
            entry.SgdbId = null;
            entry.SgdbName = null;
            entry.MatchMethod = null;
            Matcher?.Cache.Remove(entry.CacheKey);
            EntriesChanged?.Invoke();
        }

        public int RestoreAll()
        {
            var restored = 0;
            foreach (var entry in Entries.Where(e => e.Customized))
            {
                if (Library.RestoreOriginal(entry)) restored++;
            }
            EntriesChanged?.Invoke();
            return restored;
        }

        /// <summary>Delete orphaned cached-art files (games no longer tracked by the Xbox app).</summary>
        public int CleanupOrphans(List<GameEntry> orphans)
        {
            var deleted = 0;
            foreach (var entry in orphans)
            {
                if (Library.DeleteOrphanedFiles(entry))
                {
                    Entries.Remove(entry);
                    deleted++;
                }
            }
            EntriesChanged?.Invoke();
            return deleted;
        }
    }
}
