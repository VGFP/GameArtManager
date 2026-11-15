using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GameArtManager.Core.Sources
{
    /// <summary>Epic Games Store: installed-game names from launcher .item manifests.</summary>
    public class EpicSource
    {
        private readonly Dictionary<string, string> _byItem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byApp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _byNamespace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public EpicSource()
        {
            // NOTE: hardcoded — CommonApplicationData is virtualized in UWP containers.
            var manifests = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (!Directory.Exists(manifests)) return;

            foreach (var file in Directory.GetFiles(manifests, "*.item"))
            {
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(file));
                    var name = obj["DisplayName"]?.Value<string>();
                    if (string.IsNullOrEmpty(name)) continue;

                    var itemId = obj["CatalogItemId"]?.Value<string>();
                    if (!string.IsNullOrEmpty(itemId)) _byItem[itemId.ToLowerInvariant()] = name;
                    var appName = obj["AppName"]?.Value<string>();
                    if (!string.IsNullOrEmpty(appName)) _byApp[appName.ToLowerInvariant()] = name;
                    var ns = obj["CatalogNamespace"]?.Value<string>();
                    if (!string.IsNullOrEmpty(ns) && !_byNamespace.ContainsKey(ns.ToLowerInvariant()))
                        _byNamespace[ns.ToLowerInvariant()] = name;
                }
                catch
                {
                    // skip unreadable manifest
                }
            }
        }

        public int ByItemCount => _byItem.Count;

        /// <summary>Epic cache ids mix namespace / catalogItemId / appName; try all.</summary>
        public string Lookup(string externalId)
        {
            var parts = externalId.Replace(":", "_").Split('_')
                .Where(p => p.Length > 0).Select(p => p.ToLowerInvariant()).ToList();

            foreach (var part in parts) // catalog item id first (32-hex)
            {
                if (part.Length == 32 && part.All(Uri.IsHexDigit))
                    if (_byItem.TryGetValue(part, out var item)) return item;
            }
            foreach (var part in parts) // appName next (e.g. 'fn', 'sugar')
            {
                if (_byApp.TryGetValue(part, out var app)) return app;
            }
            foreach (var part in parts)
            {
                if (_byNamespace.TryGetValue(part, out var ns)) return ns;
            }
            return null;
        }
    }
}
