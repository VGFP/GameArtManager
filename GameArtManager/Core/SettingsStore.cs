using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Windows.Storage;

namespace GameArtManager.Core
{
    /// <summary>User preferences, persisted as JSON in the package LocalFolder.</summary>
    public class Settings
    {
        // never round-trips through settings.json; persisted via ApiKeyStore (DPAPI vault)
        [Newtonsoft.Json.JsonIgnore]
        public string ApiKey { get; set; } = "";
        public List<string> GridDimensions { get; set; } = new List<string> { "512x512", "1024x1024" };
        // HQ square icons only — small (128) ones would look blurry next to the ~182px defaults
        public List<string> IconDimensions { get; set; } = new List<string> { "256", "512", "1024" };
        // valid grid styles per the SGDB API: alternate, material, white_logo, no_logo
        public List<string> Styles { get; set; } = new List<string> { "alternate", "material", "white_logo" };
        public bool Nsfw { get; set; } = false;
        public bool Humor { get; set; } = false;
        public int FuzzyThreshold { get; set; } = 85;
        public int MatchWorkers { get; set; } = 6;
    }

    public static class SettingsStore
    {
        public static Settings Load()
        {
            try
            {
                var file = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");
                if (File.Exists(file))
                {
                    var raw = File.ReadAllText(file);

                    // one-time migration: older builds persisted the API key in plaintext
                    var json = JObject.Parse(raw);
                    var legacyKey = json["ApiKey"] != null ? json["ApiKey"].ToString() : "";
                    if (!string.IsNullOrEmpty(legacyKey))
                    {
                        ApiKeyStore.Save(legacyKey);
                        json.Remove("ApiKey");
                        File.WriteAllText(file, json.ToString());
                    }

                    var settings = JsonConvert.DeserializeObject<Settings>(raw) ?? new Settings();
                    settings.ApiKey = ApiKeyStore.Load();
                    return settings;
                }
            }
            catch
            {
                // corrupt settings -> defaults
            }
            return new Settings();
        }

        public static void Save(Settings settings)
        {
            var file = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");
            // ApiKey is [JsonIgnore]d: JSON stays clean, the key goes to the vault below
            File.WriteAllText(file, JsonConvert.SerializeObject(settings, Formatting.Indented));
            ApiKeyStore.Save(settings.ApiKey);
        }
    }

    /// <summary>Stores the SteamGridDB API key in the DPAPI-encrypted Windows PasswordVault.</summary>
    internal static class ApiKeyStore
    {
        private const string Resource = "GameArtManager";
        private const string UserName = "sgdb";

        /// <summary>Returns the stored key, or "" when none exists / the vault is unavailable.</summary>
        public static string Load()
        {
            try
            {
                var vault = new Windows.Security.Credentials.PasswordVault();
                foreach (var cred in vault.RetrieveAll())
                {
                    if (cred.Resource == Resource && cred.UserName == UserName)
                    {
                        cred.RetrievePassword();
                        return cred.Password;
                    }
                }
            }
            catch
            {
                // vault unavailable -> behave as if no key is stored
            }
            return "";
        }

        /// <summary>Persists the key, or clears it when key is null/empty. Never throws.</summary>
        public static void Save(string key)
        {
            try
            {
                var vault = new Windows.Security.Credentials.PasswordVault();
                try
                {
                    foreach (var cred in vault.RetrieveAll())
                    {
                        if (cred.Resource == Resource && cred.UserName == UserName)
                            vault.Remove(cred);
                    }
                }
                catch
                {
                    // best-effort cleanup of the previous entry
                }
                if (!string.IsNullOrEmpty(key))
                    vault.Add(new Windows.Security.Credentials.PasswordCredential(Resource, UserName, key));
            }
            catch
            {
                // never let credential-store failures crash the app
            }
        }
    }

    /// <summary>Persisted matching results keyed by "platform:external_id".</summary>
    public class MatchRecord
    {
        public long SgdbId { get; set; }
        public string SgdbName { get; set; }
        public string Method { get; set; }
        public long Ts { get; set; }
    }

    public class MatchCache
    {
        private readonly object _lock = new object();
        private readonly string _path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "matches.json");
        private Dictionary<string, MatchRecord> _data = new Dictionary<string, MatchRecord>();

        public MatchCache()
        {
            try
            {
                if (File.Exists(_path))
                    _data = JsonConvert.DeserializeObject<Dictionary<string, MatchRecord>>(File.ReadAllText(_path))
                            ?? new Dictionary<string, MatchRecord>();
            }
            catch
            {
                _data = new Dictionary<string, MatchRecord>();
            }
        }

        public MatchRecord Get(string key)
        {
            lock (_lock) return _data.TryGetValue(key, out var record) ? record : null;
        }

        public void Put(string key, MatchRecord record)
        {
            lock (_lock)
            {
                record.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _data[key] = record;
                Flush();
            }
        }

        public void Remove(string key)
        {
            lock (_lock)
            {
                if (_data.Remove(key)) Flush();
            }
        }

        private void Flush()
        {
            try
            {
                File.WriteAllText(_path, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch
            {
                // non-fatal
            }
        }
    }
}
