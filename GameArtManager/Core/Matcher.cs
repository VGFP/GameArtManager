using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FuzzySharp;
using GameArtManager.Core.Sources;

namespace GameArtManager.Core
{
    /// <summary>
    /// Three-tier matching: exact platform-ID -> fuzzy name search -> manual.
    /// Every resolution is cached so rescans are instant.
    /// </summary>
    public class Matcher
    {
        private static readonly Regex Hex32 = new Regex("^[0-9a-f]{32}$", RegexOptions.Compiled);

        public SgdbClient Client { get; }
        public Settings Settings { get; }
        public MatchCache Cache { get; } = new MatchCache();

        /// <summary>Diagnostics for the last MatchManyAsync run.</summary>
        public int MatchedCount { get; private set; }
        public string LastSgdbError { get; private set; }

        public Matcher(SgdbClient client, Settings settings)
        {
            Client = client;
            Settings = settings;
        }

        public Dictionary<string, string> SteamGames { get; private set; }
        public EpicSource Epic { get; private set; }
        public Dictionary<string, string> GogGames { get; private set; }

        public void LoadLocalSources()
        {
            try { SteamGames = SteamSource.InstalledGames(); }
            catch { SteamGames = new Dictionary<string, string>(); }
            try { Epic = new EpicSource(); }
            catch { Epic = new EpicSource(); }
            try { GogGames = GogSource.InstalledGames(); }
            catch { GogGames = new Dictionary<string, string>(); }
        }

        /// <summary>Authoritative local name for a game (launcher data).</summary>
        public string LocalName(GameEntry entry)
        {
            try
            {
                switch (entry.Platform)
                {
                    case "steam":
                        return SteamGames != null && SteamGames.TryGetValue(entry.ExternalId, out var steamName) ? steamName : null;
                    case "epic":
                        return Epic?.Lookup(entry.ExternalId);
                    case "gog":
                        return GogGames != null && GogGames.TryGetValue(entry.ExternalId, out var gogName) ? gogName : null;
                    default:
                        return entry.Name != "Unknown" ? entry.Name : null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Candidate SGDB platform ids, best first (Epic ids mix ns/item/appName).</summary>
        public static List<string> ExactCandidates(GameEntry entry)
        {
            var parts = entry.ExternalId.Replace(":", "_").Split('_').Where(p => p.Length > 0).ToList();
            if (entry.Platform == "epic")
            {
                var hex = parts.Where(p => Hex32.IsMatch(p.ToLowerInvariant())).ToList();
                return hex.Count > 0 ? hex : parts;
            }
            if (entry.Platform == "custom") return new List<string>();
            return new List<string> { entry.ExternalId };
        }

        public static string Normalize(string name)
        {
            // strip trademark marks BEFORE NFKD: it would turn ™ into "tm"
            var text = name.Replace("™", "").Replace("®", "").Replace("©", "");
            text = text.Normalize(System.Text.NormalizationForm.FormD);
            text = new string(text.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
            text = text.Replace("&", " and ").Replace("+", " plus ");
            text = Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.StartsWith("the ")) text = text.Substring(4);
            return text;
        }

        public static double Similarity(string a, string b)
        {
            var na = Normalize(a);
            var nb = Normalize(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            if (na == nb) return 100;
            return Fuzz.TokenSortRatio(na, nb);
        }

        public async Task<GameEntry> MatchAsync(GameEntry entry)
        {
            entry.LocalName = LocalName(entry);
            if (entry.Platform == "custom" && entry.Name != "Unknown")
                entry.LocalName = entry.Name;

            var cached = Cache.Get(entry.CacheKey);
            if (cached != null)
            {
                entry.SgdbId = cached.SgdbId;
                entry.SgdbName = cached.SgdbName;
                entry.MatchMethod = "cache";
                return entry;
            }

            var platform = SgdbClient.PlatformString(entry.Platform);
            if (platform != null)
            {
                foreach (var candidate in ExactCandidates(entry))
                {
                    SgdbGame game = null;
                    try
                    {
                        game = await Client.GameByPlatformAsync(platform, candidate);
                    }
                    catch (SgdbException ex)
                    {
                        if (LastSgdbError == null) LastSgdbError = ex.Message;
                    }
                    if (game != null)
                    {
                        SetMatch(entry, game, "exact");
                        return entry;
                    }
                }
            }

            var local = entry.LocalName;
            if (!string.IsNullOrEmpty(local))
            {
                List<SgdbGame> results;
                try
                {
                    results = await Client.SearchAsync(local);
                }
                catch (SgdbException ex)
                {
                    if (LastSgdbError == null) LastSgdbError = ex.Message;
                    results = new List<SgdbGame>();
                }
                SgdbGame best = null;
                double bestScore = 0;
                foreach (var result in results)
                {
                    var score = Similarity(local, result.Name ?? "");
                    if (score > bestScore)
                    {
                        best = result;
                        bestScore = score;
                    }
                }
                if (best != null && bestScore >= Settings.FuzzyThreshold)
                    SetMatch(entry, best, "fuzzy");
            }
            return entry;
        }

        private void SetMatch(GameEntry entry, SgdbGame game, string method)
        {
            entry.SgdbId = game.Id;
            entry.SgdbName = game.Name;
            entry.MatchMethod = method;
            MatchedCount++;
            Cache.Put(entry.CacheKey, new MatchRecord { SgdbId = game.Id, SgdbName = game.Name, Method = method });
        }

        public void SetManual(GameEntry entry, SgdbGame game) => SetMatch(entry, game, "manual");

        public void ClearMatch(GameEntry entry)
        {
            Cache.Remove(entry.CacheKey);
            entry.SgdbId = null;
            entry.SgdbName = null;
            entry.MatchMethod = null;
        }

        /// <summary>Parallel matching (bounded workers) with progress(done, total).</summary>
        public async Task MatchManyAsync(List<GameEntry> entries, IProgress<(int done, int total)> progress = null)
        {
            MatchedCount = 0;
            LastSgdbError = null;
            var total = entries.Count;
            var done = 0;
            var gate = new SemaphoreSlim(Math.Max(1, Settings.MatchWorkers));
            var exceptions = new ConcurrentQueue<Exception>();

            await Task.WhenAll(entries.Select(async entry =>
            {
                await gate.WaitAsync();
                try
                {
                    await MatchAsync(entry);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
                finally
                {
                    gate.Release();
                    var d = Interlocked.Increment(ref done);
                    progress?.Report((d, total));
                }
            }));

            if (!exceptions.IsEmpty) throw new AggregateException(exceptions);
        }
    }
}
