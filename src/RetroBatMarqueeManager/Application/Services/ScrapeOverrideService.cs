using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Manual corrections for the Steam / IGDB / SteamGridDB scrapers.
    ///  1. Per-game overrides in "scraper_overrides.ini" (next to config.ini): force the right game ID or search name.
    ///  2. A "rejected" list: the hotkey Ctrl+Shift+R moves the staged hero/logo of the current game away and stops
    ///     the scrapers from fetching it again until an override entry exists for that game.
    /// FR: Corrections manuelles pour les scrapers Steam / IGDB / SteamGridDB (ID ou nom forcé par jeu, et rejet d'un résultat).
    /// </summary>
    public class GameOverride
    {
        public string? Name { get; set; }
        public string? Steam { get; set; }
        public string? Igdb { get; set; }
        public string? SteamGridDb { get; set; }

        public string? IdFor(string scraper) => scraper switch
        {
            "Steam" => Steam,
            "IGDB" => Igdb,
            "SteamGridDB" => SteamGridDb,
            _ => null
        };

        /// <summary>EN: The numeric ID for this scraper (accepts a pasted URL too: first number wins).</summary>
        public int? NumericIdFor(string scraper)
        {
            var m = System.Text.RegularExpressions.Regex.Match(IdFor(scraper) ?? "", @"\d+");
            return m.Success && int.TryParse(m.Value, out var n) ? n : (int?)null;
        }

        /// <summary>EN: An ID for this scraper, or a name to search with.</summary>
        public bool HasDataFor(string scraper) => !string.IsNullOrWhiteSpace(IdFor(scraper)) || !string.IsNullOrWhiteSpace(Name);
    }

    public class ScrapeOverrideService
    {
        private readonly IConfigService _config;
        private readonly ILogger<ScrapeOverrideService> _logger;
        private readonly string _overridesPath;
        private readonly string _rejectedPath;

        private readonly object _lock = new object();
        private Dictionary<string, GameOverride> _overrides = new Dictionary<string, GameOverride>(StringComparer.OrdinalIgnoreCase);
        private DateTime _loadedStamp = DateTime.MinValue;
        private readonly HashSet<string> _tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ScrapeOverrideService(IConfigService config, ILogger<ScrapeOverrideService> logger)
        {
            _config = config;
            _logger = logger;
            _overridesPath = Path.Combine(Path.GetDirectoryName(_config.ConfigPath) ?? AppContext.BaseDirectory, "scraper_overrides.ini");
            _rejectedPath = Path.Combine(_config.MarqueeImagePath, "_cache", "scrape_rejected.json");
            LoadRejected();
            EnsureTemplate();
        }

        private static string Key(string system, string rom) => $"{system}\\{rom}";

        // ------------------------------------------------------------------ overrides

        public GameOverride? Get(string systemName, string romName)
        {
            lock (_lock)
            {
                Reload();
                if (_overrides.TryGetValue(Key(systemName, romName), out var ov)) return ov;
                if (_overrides.TryGetValue(Key("*", romName), out ov)) return ov;
                return null;
            }
        }

        /// <summary>
        /// EN: True once per edit of the overrides file when this scraper has data for the game. Lets the scraper ignore
        /// its "not found" cache and the reject list for one fresh attempt (no retry loop if that attempt fails again).
        /// </summary>
        public bool ShouldBypass(string scraper, string systemName, string romName)
        {
            lock (_lock)
            {
                var ov = Get(systemName, romName);
                if (ov == null || !ov.HasDataFor(scraper)) return false;
                return !_tried.Contains($"{scraper}|{Key(systemName, romName)}");
            }
        }

        public void MarkTried(string scraper, string systemName, string romName)
        {
            lock (_lock)
            {
                _tried.Add($"{scraper}|{Key(systemName, romName)}");
            }
            Unreject(systemName, romName);
        }

        private void Reload()
        {
            try
            {
                if (!File.Exists(_overridesPath)) { _overrides.Clear(); return; }
                var stamp = File.GetLastWriteTimeUtc(_overridesPath);
                if (stamp == _loadedStamp) return;

                var result = new Dictionary<string, GameOverride>(StringComparer.OrdinalIgnoreCase);
                GameOverride? current = null;
                foreach (var raw in File.ReadAllLines(_overridesPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        var section = line.Substring(1, line.Length - 2).Trim().Replace('/', '\\');
                        current = new GameOverride();
                        result[section] = current;
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0 || current == null) continue;
                    var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var v = line.Substring(eq + 1).Trim();
                    if (v.Length == 0) continue;
                    switch (k)
                    {
                        case "name": current.Name = v; break;
                        case "steam": current.Steam = v; break;
                        case "igdb": current.Igdb = v; break;
                        case "steamgriddb": current.SteamGridDb = v; break;
                    }
                }

                _overrides = result;
                _loadedStamp = stamp;
                _tried.Clear(); // an edit gives every override one fresh attempt
                _logger.LogInformation($"[Overrides] Loaded {result.Count} entr{(result.Count == 1 ? "y" : "ies")} from {_overridesPath}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Failed to read overrides file: {ex.Message}");
            }
        }

        private void EnsureTemplate()
        {
            try
            {
                if (File.Exists(_overridesPath)) return;
                File.WriteAllText(_overridesPath,
@"# Manual fixes for the Steam / IGDB / SteamGridDB scrapers.
# One section per game: [system\rom_file_name_without_extension]  (use * as the system to match any system).
# Keys (all optional):
#   Name        = search by this name instead of the game name (the top result is trusted)
#   Steam       = Steam app ID   (the number in store.steampowered.com/app/<ID>/)
#   IGDB        = IGDB game ID   (shown in the API / on the game page as the ID)
#   SteamGridDB = SteamGridDB game ID (the number in steamgriddb.com/game/<ID>)
# Saving this file gives each listed game one fresh attempt, even if it failed before or was rejected with Ctrl+Shift+R.
# Existing hero/logo files are never overwritten: press Ctrl+Shift+R on the game first to discard a wrong one.
#
# Example:
# [windows\Half-Life 2]
# Steam = 220
# Name = Half-Life 2
");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Could not create template: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ rejected results

        public bool IsRejected(string systemName, string romName)
        {
            lock (_lock) { return _rejected.Contains(Key(systemName, romName)); }
        }

        private void Unreject(string systemName, string romName)
        {
            lock (_lock)
            {
                if (_rejected.Remove(Key(systemName, romName))) SaveRejected();
            }
        }

        /// <summary>
        /// EN: Move the staged hero + logo of a game to _cache\discarded, delete its composed cache and remember the
        /// rejection so the scrapers don't fetch it again. Files in RetroBat's own images folder are never touched.
        /// </summary>
        public int Reject(string systemName, string romName)
        {
            int moved = 0;
            try
            {
                string discardDir = Path.Combine(_config.MarqueeImagePath, "_cache", "discarded", Sanitize(systemName));
                Directory.CreateDirectory(discardDir);

                var basePath = Path.Combine(_config.MarqueeImagePath,
                    _config.MarqueeFilePath.Replace("{system_name}", systemName).Replace("{game_name}", romName));

                foreach (var b in new[] { basePath, basePath + "-fanart" })
                {
                    foreach (var ext in _config.AcceptedFormats)
                    {
                        var src = $"{b}.{ext.Trim()}";
                        if (!File.Exists(src)) continue;
                        var dest = Path.Combine(discardDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(src)}");
                        File.Move(src, dest);
                        moved++;
                        _logger.LogInformation($"[Overrides] Discarded {src} -> {dest}");
                    }
                }

                // EN: composed cache (+ offset metadata + source signature) for this game
                var cacheDir = Path.Combine(_config.CachePath, systemName);
                if (Directory.Exists(cacheDir))
                {
                    foreach (var f in Directory.GetFiles(cacheDir, $"{romName}_composed*"))
                    {
                        try { File.Delete(f); } catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Discard failed: {ex.Message}");
            }

            lock (_lock)
            {
                _rejected.Add(Key(systemName, romName));
                // EN: once a game is rejected, an existing override must get a fresh attempt again
                foreach (var scraper in new[] { "Steam", "IGDB", "SteamGridDB" })
                    _tried.Remove($"{scraper}|{Key(systemName, romName)}");
                SaveRejected();
            }
            return moved;
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private void LoadRejected()
        {
            try
            {
                if (!File.Exists(_rejectedPath)) return;
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_rejectedPath));
                if (list == null) return;
                foreach (var i in list) _rejected.Add(i);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Failed to load rejected list: {ex.Message}");
            }
        }

        // EN: Caller holds _lock.
        private void SaveRejected()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_rejectedPath)!);
                File.WriteAllText(_rejectedPath, JsonSerializer.Serialize(_rejected.ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Failed to save rejected list: {ex.Message}");
            }
        }
    }
}
