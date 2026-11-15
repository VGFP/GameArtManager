using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GameArtManager.Core
{
    /// <summary>A third-party game visible in the Xbox app's library cache.</summary>
    public class GameEntry
    {
        public string Platform { get; set; }          // steam | gog | epic | ubi | ea | custom
        public string ExternalId { get; set; }        // id after the first ':'
        public string FullId { get; set; }            // "steam:1005300"
        public string CacheKey { get; set; }
        public string ImagePath { get; set; }
        public string Name { get; set; } = "Unknown";
        public DateTime? AddedDate { get; set; }
        public bool FromManifest { get; set; }
        public bool FromFilename { get; set; }
        public bool HasBackup { get; set; }
        public bool HasNew { get; set; }
        public string LocalName { get; set; }
        public long? SgdbId { get; set; }
        public string SgdbName { get; set; }
        public string MatchMethod { get; set; }       // exact | fuzzy | manual | cache
        public string InstallLocation { get; set; }   // custom entries only
        public string ExecutableName { get; set; }

        public string DisplayName => SgdbName ?? LocalName ?? Name;
        public bool HasCachedImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool Customized => HasBackup;

        /// <summary>Detected only as a cached image file, not tracked by the Xbox app
        /// (leftover art from uninstalled games) — safe to hide or delete.</summary>
        public bool IsOrphaned => FromFilename && !FromManifest;
    }

    /// <summary>
    /// Scans the Xbox app's third-party library and performs artwork replacement.
    /// Dual detection: manifests AND image filenames (manifests are frequently
    /// missing/stale - e.g. an Epic folder with no manifest at all).
    /// </summary>
    public class XboxLibrary
    {
        private static readonly string GamingAppPackage = "Microsoft.GamingApp_8wekyb3d8bbwe";
        private static readonly Dictionary<string, string> PlatformPrefixes = new Dictionary<string, string>
        {
            ["steam"] = "Steam",
            ["gog"] = "GOG",
            ["epic"] = "Epic",
            ["ubi"] = "Ubisoft",
            ["ea"] = "EA",
            ["customlibrarymanagement"] = "Custom",
        };

        public string Root { get; }

        /// <summary>Human-readable notes from the last Scan() run (diagnostics).</summary>
        public List<string> LastScanNotes { get; } = new List<string>();

        public XboxLibrary(string root = null)
        {
            if (root != null)
            {
                Root = root;
                return;
            }
            // NOTE: LOCALAPPDATA is virtualized to the app container's AC folder in UWP —
            // build the real user path from the (unvirtualized) user profile instead.
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            Root = Path.Combine(local, "Packages", GamingAppPackage, "LocalState", "ThirdPartyLibraries");
        }

        public bool Exists => Directory.Exists(Root);

        private List<(string folder, string platform)> PlatformFolders()
        {
            var result = new List<(string, string)>();
            if (!Directory.Exists(Root)) return result;
            foreach (var folder in Directory.GetDirectories(Root))
            {
                var name = Path.GetFileName(folder);
                if (PlatformPrefixes.TryGetValue(name.ToLowerInvariant(), out var platform))
                    result.Add((name, platform.ToLowerInvariant() == "Custom" ? "custom" : platform.ToLowerInvariant()));
            }
            return result;
        }

        private static Dictionary<string, Newtonsoft.Json.Linq.JObject> ParseManifest(string path)
        {
            try
            {
                return ParseManifestText(File.ReadAllText(path));
            }
            catch
            {
                return new Dictionary<string, Newtonsoft.Json.Linq.JObject>();
            }
        }

        /// <summary>Manifest parser body, shared by the System.IO and broker scan paths.</summary>
        private static Dictionary<string, Newtonsoft.Json.Linq.JObject> ParseManifestText(string text)
        {
            var entries = new Dictionary<string, Newtonsoft.Json.Linq.JObject>();
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(text);
                var cache = obj["gameCache"] as Newtonsoft.Json.Linq.JObject;
                if (cache == null) return entries;
                foreach (var prop in cache.Properties())
                {
                    if (prop.Name == "version") continue;
                    if (prop.Value is Newtonsoft.Json.Linq.JObject entry)
                    {
                        var id = (string)entry["id"] ?? prop.Name;
                        if (!string.IsNullOrEmpty(id) && id.Contains(":"))
                            entries[id] = entry;
                    }
                }
            }
            catch
            {
                // unreadable manifest -> skip (empty ea.manifest is common)
            }
            return entries;
        }

        private static DateTime? ParseAddedDate(object raw)
        {
            try
            {
                var ms = Convert.ToInt64(raw);
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).DateTime;
            }
            catch
            {
                return null;
            }
        }

        public List<GameEntry> Scan()
        {
            var entries = new Dictionary<string, GameEntry>();
            LastScanNotes.Clear();

            if (!Directory.Exists(Root))
            {
                LastScanNotes.Add("System.IO Directory.Exists(Root) = FALSE");
                LastScanNotes.Add("User profile: " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                LastScanNotes.Add("LOCALAPPDATA: " + Environment.GetEnvironmentVariable("LOCALAPPDATA"));

                // try the WinRT API as well (the widget used StorageFolder)
                try
                {
                    var sf = Windows.Storage.StorageFolder.GetFolderFromPathAsync(Root).AsTask().GetAwaiter().GetResult();
                    LastScanNotes.Add("StorageFolder.GetFolderFromPathAsync SUCCEEDED: " + sf.Name);
                }
                catch (Exception ex)
                {
                    LastScanNotes.Add($"StorageFolder.GetFolderFromPathAsync FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                LastScanNotes.Add("-> fix: Settings > Privacy & security > File system");
                return new List<GameEntry>();
            }
            LastScanNotes.Add("Root visible via System.IO: " + Root);

            List<(string folderName, string platform)> folders;
            try
            {
                folders = PlatformFolders();
            }
            catch (Exception ex)
            {
                LastScanNotes.Add("Could not list platform folders: " + ex.Message);
                return new List<GameEntry>();
            }
            LastScanNotes.Add($"Platform folders matched: {string.Join(", ", folders.Select(f => f.folderName))}");

            foreach (var (folderName, platform) in folders)
            {
                var folderPath = Path.Combine(Root, folderName);
                if (platform == "bnet") continue;

                // 1. manifest entries
                var manifestPath = FindManifest(folderPath, folderName);
                if (manifestPath != null)
                    AddManifestEntries(entries, folderName, ParseManifest(manifestPath));

                // 2. filename-only entries (manifest missing/stale)
                var manifestCount = entries.Values.Count(e => e.FromManifest &&
                    e.FullId.StartsWith(platform + ":", StringComparison.Ordinal));
                var fileCount = 0;
                try
                {
                    fileCount = AddFilenameEntries(entries, folderName, Directory.GetFiles(folderPath, "*.png"));
                }
                catch (Exception ex)
                {
                    LastScanNotes.Add($"folder '{folderName}': could not list images ({ex.Message})");
                }
                LastScanNotes.Add($"folder '{folderName}' ({platform}): {manifestCount} manifest entries, {fileCount} image-only");
            }

            foreach (var entry in entries.Values)
            {
                entry.HasBackup = HasSibling(entry, ".bak");
                entry.HasNew = HasSibling(entry, ".new");
            }

            return entries.Values
                .OrderBy(e => e.DisplayName == "Unknown" ? 1 : 0)
                .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Scan() with an automatic Windows.Storage (broker) fallback:
        /// direct System.IO first (works on most machines), then — when it yields
        /// nothing and the broker CAN see the root — a full broker-based scan.</summary>
        public async Task<List<GameEntry>> ScanWithFallbackAsync()
        {
            var entries = await Task.Run(() => Scan());
            if (entries.Count > 0) return entries;

            // entries empty: either access was denied (see LastScanNotes) or the
            // library is genuinely empty — the broker check tells them apart.
            if (!await BrokerFs.FolderExistsAsync(Root)) return entries;

            LastScanNotes.Add("scan via broker fallback");
            return await ScanBrokerAsync();
        }

        /// <summary>Full scan through the Windows.Storage broker (no System.IO).</summary>
        private async Task<List<GameEntry>> ScanBrokerAsync()
        {
            var entries = new Dictionary<string, GameEntry>();

            var folders = new List<(string folderName, string platform)>();
            foreach (var name in await BrokerFs.GetSubDirectoriesAsync(Root))
            {
                if (PlatformPrefixes.TryGetValue(name.ToLowerInvariant(), out var platform))
                    folders.Add((name, platform.ToLowerInvariant() == "Custom" ? "custom" : platform.ToLowerInvariant()));
            }
            LastScanNotes.Add($"Platform folders matched (broker): {string.Join(", ", folders.Select(f => f.folderName))}");

            foreach (var (folderName, platform) in folders)
            {
                var folderPath = Path.Combine(Root, folderName);
                if (platform == "bnet") continue;

                // 1. manifest entries (expected name first, then any *.manifest match, ignore case)
                var manifestText = await BrokerFs.ReadAllTextAsync(Path.Combine(folderPath, folderName + ".manifest"));
                if (manifestText == null)
                {
                    var candidate = (await BrokerFs.GetFilesAsync(folderPath)).FirstOrDefault(f =>
                        Path.GetFileName(f).Equals(folderName + ".manifest", StringComparison.OrdinalIgnoreCase));
                    if (candidate != null)
                        manifestText = await BrokerFs.ReadAllTextAsync(candidate);
                }
                if (manifestText != null)
                    AddManifestEntries(entries, folderName, ParseManifestText(manifestText));

                // 2. filename-only entries (manifest missing/stale)
                var manifestCount = entries.Values.Count(e => e.FromManifest &&
                    e.FullId.StartsWith(platform + ":", StringComparison.Ordinal));
                var fileCount = AddFilenameEntries(entries, folderName, await BrokerFs.GetFilesAsync(folderPath));
                LastScanNotes.Add($"folder '{folderName}' ({platform}): {manifestCount} manifest entries, {fileCount} image-only (broker)");
            }

            foreach (var entry in entries.Values)
            {
                entry.HasBackup = await HasSiblingAsync(entry, ".bak");
                entry.HasNew = await HasSiblingAsync(entry, ".new");
            }

            return entries.Values
                .OrderBy(e => e.DisplayName == "Unknown" ? 1 : 0)
                .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Manifest entries -> GameEntry objects (shared by both scan paths).</summary>
        private void AddManifestEntries(Dictionary<string, GameEntry> entries, string folderName,
            Dictionary<string, Newtonsoft.Json.Linq.JObject> manifest)
        {
            foreach (var pair in manifest)
            {
                var fullId = pair.Key;
                var raw = pair.Value;
                var prefix = fullId.Substring(0, fullId.IndexOf(':')).ToLowerInvariant();
                var external = fullId.Substring(fullId.IndexOf(':') + 1);

                var entry = new GameEntry
                {
                    Platform = prefix,
                    ExternalId = external,
                    FullId = fullId,
                    CacheKey = fullId,
                    Name = (string)raw["title"] ?? "Unknown",
                    AddedDate = ParseAddedDate(raw["addedDate"]),
                    FromManifest = true,
                    InstallLocation = (string)raw["installLocation"],
                    ExecutableName = (string)raw["executableName"],
                };
                var imagePath = (string)raw["imagePath"];
                entry.ImagePath = !string.IsNullOrEmpty(imagePath)
                    ? imagePath
                    : ImagePathFor(folderName, entry);
                entries[fullId] = entry;
            }
        }

        /// <summary>Filename-only entries -> GameEntry objects (shared by both scan paths;
        /// files are full paths, non-.png names are skipped).</summary>
        private static int AddFilenameEntries(Dictionary<string, GameEntry> entries, string folderName, IEnumerable<string> files)
        {
            var fileCount = 0;
            foreach (var file in files)
            {
                if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var filename = Path.GetFileName(file);
                var m = Regex.Match(filename, @"^([A-Za-z]+)_(.+)\.png$");
                if (!m.Success) continue;
                var prefix = m.Groups[1].Value.ToLowerInvariant();
                if (prefix != folderName.ToLowerInvariant().Replace("customlibrarymanagement", "custom")) continue;
                if (prefix != "steam" && prefix != "gog" && prefix != "epic" && prefix != "ubi" && prefix != "ea" && prefix != "custom") continue;

                var fullId = $"{prefix}:{m.Groups[2].Value}";
                if (entries.ContainsKey(fullId)) continue;

                entries[fullId] = new GameEntry
                {
                    Platform = prefix,
                    ExternalId = m.Groups[2].Value,
                    FullId = fullId,
                    CacheKey = fullId,
                    ImagePath = file,
                    FromFilename = true,
                };
                fileCount++;
            }
            return fileCount;
        }

        private static string FindManifest(string folderPath, string folderName)
        {
            var expected = Path.Combine(folderPath, folderName + ".manifest");
            if (File.Exists(expected)) return expected;
            try
            {
                var candidate = Directory.GetFiles(folderPath, "*.manifest")
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(folderName + ".manifest", StringComparison.OrdinalIgnoreCase));
                return candidate;
            }
            catch
            {
                return null;
            }
        }

        private string ImagePathFor(string folderName, GameEntry entry)
        {
            return Path.Combine(Root, folderName, entry.FullId.Replace(":", "_") + ".png");
        }

        private static bool HasSibling(GameEntry entry, string suffix)
        {
            if (string.IsNullOrEmpty(entry.ImagePath)) return false;
            var baseNoExt = Path.Combine(Path.GetDirectoryName(entry.ImagePath), Path.GetFileNameWithoutExtension(entry.ImagePath));
            var ext = Path.GetExtension(entry.ImagePath);
            return File.Exists(baseNoExt + ext + suffix);
        }

        /// <summary>Broker variant of HasSibling for machines where File.Exists is denied.</summary>
        private static async Task<bool> HasSiblingAsync(GameEntry entry, string suffix)
        {
            if (string.IsNullOrEmpty(entry.ImagePath)) return false;
            var baseNoExt = Path.Combine(Path.GetDirectoryName(entry.ImagePath), Path.GetFileNameWithoutExtension(entry.ImagePath));
            var ext = Path.GetExtension(entry.ImagePath);
            return await BrokerFs.FileExistsAsync(baseNoExt + ext + suffix);
        }

        /// <summary>Replace cached art. First change keeps a .bak of the original; a .new copy survives cache refreshes.
        /// System.IO is the primary channel; on access-denied it falls back to the Windows.Storage broker.
        /// Returns true when the artwork was written.</summary>
        public async Task<bool> ReplaceImageAsync(GameEntry entry, byte[] pngData)
        {
            if (string.IsNullOrEmpty(entry.ImagePath))
                throw new InvalidOperationException($"No image path for {entry.FullId}");
            var baseNoExt = Path.Combine(Path.GetDirectoryName(entry.ImagePath), Path.GetFileNameWithoutExtension(entry.ImagePath));
            var ext = Path.GetExtension(entry.ImagePath);
            var bakPath = baseNoExt + ext + ".bak";
            var newPath = baseNoExt + ext + ".new";

            // primary: direct System.IO (works on most machines)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.ImagePath));
                if (entry.HasCachedImage && !File.Exists(bakPath))
                    File.Move(entry.ImagePath, bakPath);

                File.WriteAllBytes(entry.ImagePath, pngData);
                File.WriteAllBytes(newPath, pngData);

                entry.HasBackup = File.Exists(bakPath);
                entry.HasNew = true;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // fall through to the broker
            }
            catch (IOException)
            {
                // fall through to the broker
            }

            // fallback: Windows.Storage broker
            var imageExists = entry.HasCachedImage || await BrokerFs.FileExistsAsync(entry.ImagePath);
            if (imageExists && !await BrokerFs.FileExistsAsync(bakPath))
                await BrokerFs.MoveFileAsync(entry.ImagePath, bakPath);
            var written = await BrokerFs.WriteAllBytesAsync(entry.ImagePath, pngData);
            var newWritten = await BrokerFs.WriteAllBytesAsync(newPath, pngData);
            entry.HasBackup = await BrokerFs.FileExistsAsync(bakPath);
            entry.HasNew = newWritten;
            return written;
        }

        /// <summary>Restores the original .bak. System.IO first, broker fallback on
        /// access-denied. Returns true when the original was restored.</summary>
        public async Task<bool> RestoreOriginalAsync(GameEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ImagePath)) return false;
            var baseNoExt = Path.Combine(Path.GetDirectoryName(entry.ImagePath), Path.GetFileNameWithoutExtension(entry.ImagePath));
            var ext = Path.GetExtension(entry.ImagePath);
            var bak = baseNoExt + ext + ".bak";
            var newCopy = baseNoExt + ext + ".new";

            // primary: direct System.IO (works on most machines)
            try
            {
                if (File.Exists(bak))
                {
                    if (File.Exists(newCopy)) File.Delete(newCopy);
                    if (File.Exists(entry.ImagePath)) File.Delete(entry.ImagePath);
                    File.Move(bak, entry.ImagePath);
                    entry.HasBackup = false;
                    entry.HasNew = false;
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // fall through to the broker
            }
            catch (IOException)
            {
                // fall through to the broker
            }

            // fallback: Windows.Storage broker (File.Exists above may be false simply
            // because direct access was denied, so re-check through the broker)
            if (!await BrokerFs.FileExistsAsync(bak)) return false;
            await BrokerFs.DeleteFileAsync(newCopy); // best effort
            var moved = await BrokerFs.MoveFileAsync(bak, entry.ImagePath);
            if (moved)
            {
                entry.HasBackup = false;
                entry.HasNew = false;
            }
            return moved;
        }

        /// <summary>Delete the cached image + .bak/.new of an ORPHANED entry
        /// (refuses manifest-tracked games as a safety measure). System.IO first,
        /// broker fallback per file on access-denied. Returns true when anything was deleted.</summary>
        public async Task<bool> DeleteOrphanedFilesAsync(GameEntry entry)
        {
            if (!entry.IsOrphaned || string.IsNullOrEmpty(entry.ImagePath)) return false;
            var any = false;
            foreach (var path in SiblingPaths(entry))
            {
                var deleted = false;
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted = true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // fall through to the broker
                }
                catch (IOException)
                {
                    // fall through to the broker
                }
                if (!deleted)
                    deleted = await BrokerFs.DeleteFileAsync(path);
                any |= deleted;
            }
            return any;
        }

        private static IEnumerable<string> SiblingPaths(GameEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ImagePath)) yield break;
            var dir = Path.GetDirectoryName(entry.ImagePath);
            var name = Path.GetFileNameWithoutExtension(entry.ImagePath);
            var ext = Path.GetExtension(entry.ImagePath);
            yield return entry.ImagePath;
            yield return Path.Combine(dir, name + ext + ".bak");
            yield return Path.Combine(dir, name + ext + ".new");
        }
    }
}
