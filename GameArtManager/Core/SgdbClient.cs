using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GameArtManager.Core
{
    public class SgdbException : Exception
    {
        /// <summary>HTTP status code when the error came from a response, otherwise null.</summary>
        public int? StatusCode { get; }

        public SgdbException(string message, int? statusCode = null) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    public class SgdbGame
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    public class SgdbArt
    {
        public long Id { get; set; }
        public int Score { get; set; }
        public string Style { get; set; }
        public string Url { get; set; }
        public string Thumb { get; set; }
        public string Format { get; set; }
        public string Dimensions { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Nsfw { get; set; }
        public bool Humor { get; set; }
        public string Author { get; set; }
    }

    /// <summary>SteamGridDB API v2 client (Bearer token, full paging).</summary>
    public class SgdbClient
    {
        private const string BaseUrl = "https://www.steamgriddb.com/api/v2";
        private static readonly Dictionary<string, string> PlatformMap = new Dictionary<string, string>
        {
            ["steam"] = "steam",
            ["gog"] = "gog",
            ["epic"] = "egs",
            ["ubi"] = "uplay",
            ["ea"] = "origin",
        };

        private static readonly HashSet<string> ValidGridStyles = new HashSet<string>
        {
            "alternate", "material", "white_logo", "no_logo"
        };

        private readonly HttpClient _http;

        public SgdbClient(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new SgdbException("SteamGridDB API key is not set");
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task<JToken> GetAsync(string path)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync(BaseUrl + path);
            }
            catch (Exception ex)
            {
                throw new SgdbException($"Network error: {ex.Message}");
            }
            if ((int)resp.StatusCode == 401)
                throw new SgdbException("Unauthorized - check your SteamGridDB API key", 401);
            if ((int)resp.StatusCode == 404)
                throw new SgdbException("Not found", 404);
            if ((int)resp.StatusCode == 429)
                throw new SgdbException("Rate limited by SteamGridDB - slow down and retry", 429);

            var text = await resp.Content.ReadAsStringAsync();
            JObject payload;
            try
            {
                payload = JObject.Parse(text);
            }
            catch
            {
                throw new SgdbException($"Bad JSON from {path} (HTTP {(int)resp.StatusCode})");
            }
            if ((int)resp.StatusCode >= 400 || payload["success"]?.Value<bool>() != true)
            {
                var errors = payload["errors"] is JArray arr
                    ? string.Join("; ", arr.Select(t => t.ToString()))
                    : $"HTTP {(int)resp.StatusCode}";
                throw new SgdbException(errors, (int)resp.StatusCode);
            }
            return payload["data"];
        }

        /// <summary>Exact platform lookup; returns null when the id is unknown (404) —
        /// a normal miss, not an API failure.</summary>
        public async Task<SgdbGame> GameByPlatformAsync(string sgdbPlatform, string platformId)
        {
            try
            {
                var data = await GetAsync($"/games/{sgdbPlatform}/{Uri.EscapeDataString(platformId)}");
                return new SgdbGame { Id = data["id"].Value<long>(), Name = data["name"]?.Value<string>() };
            }
            catch (SgdbException ex) when (ex.StatusCode == 404)
            {
                return null;
            }
        }

        public async Task<List<SgdbGame>> SearchAsync(string term)
        {
            var data = await GetAsync($"/search/autocomplete/{Uri.EscapeDataString(term)}");
            return data.Cast<JObject>()
                .Select(o => new SgdbGame { Id = o["id"].Value<long>(), Name = o["name"]?.Value<string>() })
                .ToList();
        }

        private async Task<List<SgdbArt>> ArtPageAsync(string kind, long gameId, Dictionary<string, string> parameters)
        {
            var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var data = await GetAsync($"/{kind}/game/{gameId}{(query.Length > 0 ? "?" + query : "")}");
            return data?.Cast<JObject>().Select(o => new SgdbArt
            {
                Id = o["id"].Value<long>(),
                Score = o["score"]?.Value<int>() ?? 0,
                Style = o["style"]?.Value<string>(),
                Url = o["url"]?.Value<string>(),
                Thumb = o["thumb"]?.Value<string>(),
                Format = o["format"]?.Value<string>(),
                Dimensions = o["dimensions"]?.Value<string>(),
                Width = o["width"]?.Value<int>() ?? 0,
                Height = o["height"]?.Value<int>() ?? 0,
                Nsfw = o["nsfw"]?.Value<bool>() ?? false,
                Humor = o["humor"]?.Value<bool>() ?? false,
                Author = o["author"]?["username"]?.Value<string>(),
            }).ToList() ?? new List<SgdbArt>();
        }

        /// <summary>Fetch artwork across pages (SGDB pagination is 0-based!).</summary>
        private async Task<List<SgdbArt>> PagedArtAsync(string kind, long gameId, Dictionary<string, string> parameters, int maxPages = 5)
        {
            var result = new List<SgdbArt>();
            for (var page = 0; page < maxPages; page++)
            {
                var p = new Dictionary<string, string>(parameters) { ["page"] = page.ToString() };
                var batch = await ArtPageAsync(kind, gameId, p);
                if (batch.Count == 0) break;
                result.AddRange(batch);
                if (batch.Count < 50) break;
            }
            return result;
        }

        private string SanitizedStyles(Settings settings)
        {
            var styles = settings.Styles.Where(s => ValidGridStyles.Contains(s)).ToList();
            return styles.Count > 0 ? string.Join(",", styles) : null;
        }

        /// <summary>HQ square icons for the picker/apply flow (256px and up).</summary>
        public async Task<List<SgdbArt>> IconsAsync(long gameId, Settings settings)
        {
            var iconParams = new Dictionary<string, string>
            {
                ["types"] = "static",
                ["nsfw"] = settings.Nsfw.ToString().ToLowerInvariant(),
                ["humor"] = settings.Humor.ToString().ToLowerInvariant(),
                ["dimensions"] = string.Join(",", settings.IconDimensions),
            };
            try
            {
                return await PagedArtAsync("icons", gameId, iconParams, maxPages: 2);
            }
            catch (SgdbException)
            {
                return new List<SgdbArt>();
            }
        }

        /// <summary>Best HQ square art: icons first (256px+), then square grids, then a
        /// portrait cover as last resort (the caller pads covers into square tiles).</summary>
        public async Task<SgdbArt> BestSquareArtAsync(long gameId, Settings settings)
        {
            try
            {
                var icons = await IconsAsync(gameId, settings);
                var bestIcon = icons.OrderByDescending(a => a.Score).FirstOrDefault();
                if (bestIcon != null) return bestIcon;
            }
            catch (SgdbException)
            {
                // fall through to grids
            }

            var squares = await SquareGridsAsync(gameId, settings);
            if (squares.Count > 0) return squares[0];

            var covers = await PortraitCoversAsync(gameId, settings);
            return covers.FirstOrDefault();
        }

        /// <summary>Square grid uploads (any size, e.g. 1024x1024 masonry art), filtered
        /// client-side from the full grid list — the API has no "any square" dimension filter.</summary>
        public async Task<List<SgdbArt>> SquareGridsAsync(long gameId, Settings settings)
        {
            try
            {
                var grids = await PagedArtAsync("grids", gameId, new Dictionary<string, string>
                {
                    ["types"] = "static",
                    ["nsfw"] = settings.Nsfw.ToString().ToLowerInvariant(),
                    ["humor"] = settings.Humor.ToString().ToLowerInvariant(),
                }, maxPages: 3);
                return grids
                    .Where(g => g.Width > 0 && g.Width == g.Height && g.Width >= 256)
                    .OrderByDescending(g => g.Score)
                    .ToList();
            }
            catch (SgdbException)
            {
                return new List<SgdbArt>();
            }
        }

        /// <summary>Portrait covers (600x900-style) usable as padded tiles when nothing
        /// square exists for a game.</summary>
        public async Task<List<SgdbArt>> PortraitCoversAsync(long gameId, Settings settings)
        {
            try
            {
                var grids = await PagedArtAsync("grids", gameId, new Dictionary<string, string>
                {
                    ["types"] = "static",
                    ["nsfw"] = settings.Nsfw.ToString().ToLowerInvariant(),
                    ["humor"] = settings.Humor.ToString().ToLowerInvariant(),
                }, maxPages: 2);
                return grids
                    .Where(g => g.Width > 0 && g.Height > g.Width && g.Width >= 300)
                    .OrderByDescending(g => g.Score)
                    .ToList();
            }
            catch (SgdbException)
            {
                return new List<SgdbArt>();
            }
        }

        public async Task<Dictionary<string, List<SgdbArt>>> AllArtAsync(long gameId, Settings settings)
        {
            var common = new Dictionary<string, string>
            {
                ["types"] = "static",
                ["nsfw"] = settings.Nsfw.ToString().ToLowerInvariant(),
                ["humor"] = settings.Humor.ToString().ToLowerInvariant(),
            };
            var styles = SanitizedStyles(settings);
            // no dimension/format filter: manual browse should show every shape (covers,
            // squares, landscape grids) — non-square picks are padded into tiles when applied
            var grids = new Dictionary<string, string>(common);
            if (styles != null) grids["styles"] = styles;
            var icons = new Dictionary<string, string>(common) { ["formats"] = "png", ["dimensions"] = string.Join(",", settings.IconDimensions) };

            var result = new Dictionary<string, List<SgdbArt>>();
            foreach (var (kind, parameters) in new[] { ("grids", grids), ("icons", icons), ("heroes", common), ("logos", common) })
            {
                try
                {
                    var list = await PagedArtAsync(kind, gameId, parameters, maxPages: 2);
                    if (list.Count == 0 && parameters.ContainsKey("styles"))
                    {
                        // style filter can hide everything — retry unfiltered
                        list = await PagedArtAsync(kind, gameId, parameters.Where(p => p.Key != "styles").ToDictionary(p => p.Key, p => p.Value), maxPages: 2);
                    }
                    result[kind] = list;
                }
                catch (SgdbException)
                {
                    result[kind] = new List<SgdbArt>();
                }
            }
            return result;
        }

        public static string PlatformString(string platform) =>
            PlatformMap.TryGetValue(platform, out var value) ? value : null;
    }
}
