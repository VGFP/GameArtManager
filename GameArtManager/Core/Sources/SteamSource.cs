using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameArtManager.Core.Sources
{
    /// <summary>Minimal Valve VDF (KeyValues) parser for libraryfolders.vdf / appmanifest.acf.</summary>
    public static class VdfParser
    {
        public static Dictionary<string, object> ParseFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static Dictionary<string, object> Parse(string text)
        {
            var tokens = Tokenize(text);
            var pos = 0;
            return ParseBody(tokens, ref pos, stopAtBrace: false);
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '"')
                {
                    var sb = new System.Text.StringBuilder();
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            i++;
                            var escaped = text[i];
                            if (escaped == 'n') sb.Append('\n');
                            else if (escaped == 't') sb.Append('\t');
                            else if (escaped == 'r') sb.Append('\r');
                            else sb.Append(escaped); // \\ and \" fall through
                        }
                        else
                        {
                            sb.Append(text[i]);
                        }
                        i++;
                    }
                    i++; // closing quote
                    tokens.Add(sb.ToString());
                }
                else if (c == '{' || c == '}')
                {
                    tokens.Add(c.ToString());
                    i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++; // line comment
                }
                else if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}')
                    {
                        sb.Append(text[i]);
                        i++;
                    }
                    tokens.Add(sb.ToString());
                }
            }
            return tokens;
        }

        private static Dictionary<string, object> ParseBody(List<string> tokens, ref int pos, bool stopAtBrace)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (pos < tokens.Count)
            {
                if (tokens[pos] == "}")
                {
                    pos++;
                    if (stopAtBrace) return dict;
                    continue;
                }
                var key = tokens[pos++];
                if (pos >= tokens.Count) break;
                if (tokens[pos] == "{")
                {
                    pos++;
                    dict[key] = ParseBody(tokens, ref pos, stopAtBrace: true);
                }
                else
                {
                    dict[key] = tokens[pos++];
                }
            }
            return dict;
        }

        public static string GetString(Dictionary<string, object> dict, string key)
            => dict.TryGetValue(key, out var value) ? value as string : null;

        public static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
            => dict.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
    }

    /// <summary>Steam: installed-game names from libraryfolders.vdf + appmanifest_*.acf.</summary>
    public static class SteamSource
    {
        public static List<string> Diagnostics { get; } = new List<string>();

        public static Dictionary<string, string> InstalledGames()
        {
            var games = new Dictionary<string, string>();
            Diagnostics.Clear();
            var root = FindSteamRoot();
            Diagnostics.Add("steam root: " + (root ?? "NOT FOUND"));
            Diagnostics.Add("ProgramFilesX86: " + Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            if (root == null) return games;

            var libraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(root, "steamapps")
            };

            var libraryVdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryVdf))
            {
                try
                {
                    var parsed = VdfParser.ParseFile(libraryVdf);
                    var folders = VdfParser.GetDict(parsed, "libraryfolders");
                    if (folders != null)
                    {
                        foreach (var entry in folders.Values.OfType<Dictionary<string, object>>())
                        {
                            var path = VdfParser.GetString(entry, "path");
                            if (!string.IsNullOrEmpty(path))
                                libraryPaths.Add(Path.Combine(path.Replace("\\\\", "\\"), "steamapps"));
                        }
                    }
                }
                catch
                {
                    // fall back to the default library only
                }
            }

            foreach (var steamapps in libraryPaths)
            {
                if (!Directory.Exists(steamapps))
                {
                    Diagnostics.Add("library not visible: " + steamapps);
                    continue;
                }
                try
                {
                    foreach (var acf in Directory.GetFiles(steamapps, "appmanifest_*.acf"))
                    {
                        try
                        {
                            var state = VdfParser.GetDict(VdfParser.ParseFile(acf), "AppState");
                            var appid = VdfParser.GetString(state, "appid");
                            var name = VdfParser.GetString(state, "name");
                            if (!string.IsNullOrEmpty(appid) && !string.IsNullOrEmpty(name))
                                games[appid] = name;
                        }
                        catch
                        {
                            // skip unreadable manifest
                        }
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Add("library read failed: " + steamapps + " (" + ex.Message + ")");
                }
            }
            Diagnostics.Add("steam games parsed: " + games.Count);
            return games;
        }

        private static string FindSteamRoot()
        {
            // Registry is not readable from an app container, and known-folder APIs
            // return empty/virtualized paths there — probe fixed locations instead.
            var candidates = new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"C:\Steam",
                @"D:\Steam",
                @"E:\Steam",
            };
            return candidates.FirstOrDefault(Directory.Exists);
        }
    }
}
