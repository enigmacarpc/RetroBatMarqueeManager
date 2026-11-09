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
        private readonly string _noLogoPath;
        private readonly HashSet<string> _noLogo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            _noLogoPath = Path.Combine(_config.MarqueeImagePath, "_cache", "no_logo.json");
            LoadRejected();
            LoadNoLogo();
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
        public (int Moved, int Adopted) Reject(string systemName, string romName)
        {
            int moved = 0;
            int adopted = 0;
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
                DeleteComposed(systemName, romName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Discard failed: {ex.Message}");
            }

            // EN: Now look for the user's own images in RetroBat roms folder (per system) and use them.
            try { adopted = AdoptOwnImages(systemName, romName); }
            catch (Exception ex) { _logger.LogWarning($"[Overrides] Looking for own images failed: {ex.Message}"); }

            lock (_lock)
            {
                _rejected.Add(Key(systemName, romName));
                // EN: once a game is rejected, an existing override must get a fresh attempt again
                foreach (var scraper in new[] { "Steam", "IGDB", "SteamGridDB" })
                    _tried.Remove($"{scraper}|{Key(systemName, romName)}");
                SaveRejected();
            }
            return (moved, adopted);
        }

        // ------------------------------------------------------------------ no logo (Ctrl+D) / manual replace (Ctrl+I)

        /// <summary>EN: True when the user removed the logo that is drawn on top of this game's background.</summary>
        public bool IsNoLogo(string systemName, string romName)
        {
            lock (_lock) { return _noLogo.Contains(Key(systemName, romName)); }
        }

        /// <summary>
        /// EN: Toggle "no logo on top" for a game (Ctrl+D). Turning it on moves the staged logo to _cache\discarded and
        /// stops the scrapers from fetching one; turning it off lets a logo be used/fetched again. Returns the new state.
        /// </summary>
        public bool ToggleNoLogo(string systemName, string romName)
        {
            bool nowOn;
            lock (_lock)
            {
                nowOn = !_noLogo.Contains(Key(systemName, romName));
                if (nowOn) _noLogo.Add(Key(systemName, romName)); else _noLogo.Remove(Key(systemName, romName));
                SaveNoLogo();
            }
            if (nowOn) DiscardStaged(systemName, romName, hero: false, logo: true);
            DeleteComposed(systemName, romName);
            return nowOn;
        }

        /// <summary>
        /// EN: Use a file chosen by the user as the game's logo (drawn on top) or background (hero). The previous staged
        /// file of that kind is moved to _cache\discarded. Returns the new staged path, or null on failure.
        /// </summary>
        public string? ReplaceMedia(string systemName, string romName, string sourceFile, bool isHero)
        {
            try
            {
                if (!File.Exists(sourceFile)) return null;
                DiscardStaged(systemName, romName, hero: isHero, logo: !isHero);

                string pattern = _config.MarqueeFilePath.Replace("{system_name}", systemName).Replace("{game_name}", romName);
                if (isHero) pattern += "-fanart";
                string target = Path.Combine(_config.MarqueeImagePath, pattern) + Path.GetExtension(sourceFile).ToLowerInvariant();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target, overwrite: true);

                if (!isHero)
                {
                    lock (_lock) { if (_noLogo.Remove(Key(systemName, romName))) SaveNoLogo(); }
                }
                DeleteComposed(systemName, romName);
                _logger.LogInformation($"[Overrides] Using chosen {(isHero ? "background" : "logo")}: {sourceFile} -> {target}");
                return target;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Could not use chosen image: {ex.Message}");
                return null;
            }
        }

        private int DiscardStaged(string systemName, string romName, bool hero, bool logo)
        {
            int moved = 0;
            try
            {
                string discardDir = Path.Combine(_config.MarqueeImagePath, "_cache", "discarded", Sanitize(systemName));
                Directory.CreateDirectory(discardDir);
                var basePath = Path.Combine(_config.MarqueeImagePath,
                    _config.MarqueeFilePath.Replace("{system_name}", systemName).Replace("{game_name}", romName));

                var bases = new List<string>();
                if (logo) bases.Add(basePath);
                if (hero) bases.Add(basePath + "-fanart");
                foreach (var b in bases)
                    foreach (var ext in _config.AcceptedFormats)
                    {
                        var src = $"{b}.{ext.Trim()}";
                        if (!File.Exists(src)) continue;
                        File.Move(src, Path.Combine(discardDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(src)}"));
                        moved++;
                    }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Discard failed: {ex.Message}");
            }
            return moved;
        }

        private void DeleteComposed(string systemName, string romName)
        {
            try
            {
                // EN: The composed cache folder is named after the frontend's system (may differ from the real folder name,
                // e.g. gw / gameandwatch), so look in every system folder.
                if (!Directory.Exists(_config.CachePath)) return;
                foreach (var pattern in new[] { $"{romName}_composed*", $"{romName}-nologo_composed*" })
                    foreach (var f in Directory.GetFiles(_config.CachePath, pattern, SearchOption.AllDirectories))
                    {
                        try { File.Delete(f); } catch { /* best effort */ }
                    }
            }
            catch { /* best effort */ }
        }

        private void LoadNoLogo()
        {
            try
            {
                if (!File.Exists(_noLogoPath)) return;
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_noLogoPath));
                if (list != null) foreach (var i in list) _noLogo.Add(i);
            }
            catch (Exception ex) { _logger.LogWarning($"[Overrides] Failed to load no-logo list: {ex.Message}"); }
        }

        // EN: Caller holds _lock.
        private void SaveNoLogo()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_noLogoPath)!);
                File.WriteAllText(_noLogoPath, JsonSerializer.Serialize(_noLogo.ToList()));
            }
            catch (Exception ex) { _logger.LogWarning($"[Overrides] Failed to save no-logo list: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ own images from roms\<system>

        private static readonly string[] HeroMediaDirs = { "images", "media\\fanart", "media\\fanarts", "fanart", "downloaded_images" };
        private static readonly string[] LogoMediaDirs = { "images", "media\\wheel", "media\\wheels", "media\\logo", "media\\logos", "wheel", "logos", "downloaded_images" };
        private static readonly string[] HeroSuffixes = { "-fanart", "_fanart", "-hero" };
        private static readonly string[] LogoSuffixes = { "-wheel", "-logo", "_wheel", "_logo", "-marquee" }; // EN: "-marquee" last: RetroBat saves the scraped logo/wheel under that name

        /// <summary>
        /// EN: Copy the user's own hero/logo into the staging folder as {rom}-fanart / {rom}. Looks in
        /// RomsPath\<system>: first what that system's gamelist.xml points to (it differs per system), then the usual folders.
        /// Nothing is moved or deleted there.
        /// </summary>
        private int AdoptOwnImages(string systemName, string romName)
        {
            string systemDir = Path.Combine(_config.RomsPath, systemName);
            if (!Directory.Exists(systemDir)) return 0;

            var entry = ReadGamelistEntry(systemDir, romName);
            string? hero = FirstExisting(systemDir, entry.GetValueOrDefault("fanart"), HeroMediaDirs, HeroSuffixes, romName);
            string? logo = FirstExisting(systemDir, entry.GetValueOrDefault("wheel") ?? entry.GetValueOrDefault("logo") ?? entry.GetValueOrDefault("marquee"), LogoMediaDirs, LogoSuffixes, romName);

            int count = 0;
            if (hero != null && CopyToStaging(hero, systemName, romName, isHero: true)) count++;
            if (logo != null && CopyToStaging(logo, systemName, romName, isHero: false)) count++;
            _logger.LogInformation($"[Overrides] Own images for '{romName}': hero={(hero ?? "none")} logo={(logo ?? "none")}");
            return count;
        }

        /// <summary>
        /// EN: The user's own hero/background for a game from roms\{system} (gamelist.xml fanart tag first, then usual folders), or null.
        /// </summary>
        public string? FindOwnHero(string systemName, string romName)
        {
            try
            {
                string systemDir = Path.Combine(_config.RomsPath, systemName);
                if (!Directory.Exists(systemDir)) return null;
                var entry = ReadGamelistEntry(systemDir, romName);
                return FirstExisting(systemDir, entry.GetValueOrDefault("fanart"), HeroMediaDirs, HeroSuffixes, romName);
            }
            catch { return null; }
        }

        /// <summary>
        /// EN: The user's own logo for a game from roms\{system} (gamelist.xml wheel/logo tag first, then usual folders), or null.
        /// Used directly (not copied) when composing a marquee that has no game logo yet.
        /// </summary>
        public string? FindOwnLogo(string systemName, string romName)
        {
            try
            {
                string systemDir = Path.Combine(_config.RomsPath, systemName);
                if (!Directory.Exists(systemDir)) return null;
                var entry = ReadGamelistEntry(systemDir, romName);
                return FirstExisting(systemDir, entry.GetValueOrDefault("wheel") ?? entry.GetValueOrDefault("logo") ?? entry.GetValueOrDefault("marquee"), LogoMediaDirs, LogoSuffixes, romName);
            }
            catch { return null; }
        }

        // EN: Tag -> relative/absolute path from the matching <game> in roms\<system>\gamelist.xml. Empty if none.
        private readonly Dictionary<string, (DateTime Stamp, Dictionary<string, Dictionary<string, string>> Games)> _gamelists
            = new Dictionary<string, (DateTime, Dictionary<string, Dictionary<string, string>>)>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> ReadGamelistEntry(string systemDir, string romName)
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string file = Path.Combine(systemDir, "gamelist.xml");
                if (!File.Exists(file)) return empty;
                var stamp = File.GetLastWriteTimeUtc(file);

                lock (_lock)
                {
                    // EN: Parsed once per system and reloaded only when the file changes (browsing asks for every game).
                    if (!_gamelists.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                    {
                        var games = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                        var doc = System.Xml.Linq.XDocument.Load(file);
                        foreach (var game in doc.Descendants("game"))
                        {
                            string? path = game.Element("path")?.Value;
                            if (string.IsNullOrWhiteSpace(path)) continue;
                            string stem = Path.GetFileNameWithoutExtension(path.Replace('/', '\\'));
                            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var tag in new[] { "fanart", "wheel", "logo", "marquee" })
                            {
                                var v = game.Element(tag)?.Value?.Trim();
                                if (!string.IsNullOrEmpty(v)) tags[tag] = v;
                            }
                            if (tags.Count > 0) games[stem] = tags;
                        }
                        cached = (stamp, games);
                        _gamelists[file] = cached;
                    }
                    return cached.Games.TryGetValue(romName, out var entry) ? entry : empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Could not read gamelist.xml: {ex.Message}");
                return empty;
            }
        }

        private string? FirstExisting(string systemDir, string? listed, string[] dirs, string[] suffixes, string romName)
        {
            // EN: 1. exactly what gamelist.xml says
            if (!string.IsNullOrWhiteSpace(listed))
            {
                string rel = listed.Replace('/', '\\');
                string full = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(systemDir, rel.TrimStart('.', '\\')));
                if (File.Exists(full) && IsAccepted(full)) return full;
            }
            // EN: 2. usual folder + suffix names
            foreach (var dir in dirs)
                foreach (var suffix in suffixes)
                    foreach (var ext in _config.AcceptedFormats)
                    {
                        string candidate = Path.Combine(systemDir, dir, $"{romName}{suffix}.{ext.Trim()}");
                        if (File.Exists(candidate)) return candidate;
                    }
            return null;
        }

        private bool IsAccepted(string file)
        {
            string ext = Path.GetExtension(file).TrimStart('.');
            return _config.AcceptedFormats.Any(a => a.Trim().Equals(ext, StringComparison.OrdinalIgnoreCase));
        }

        private bool CopyToStaging(string source, string systemName, string romName, bool isHero)
        {
            try
            {
                string pattern = _config.MarqueeFilePath.Replace("{system_name}", systemName).Replace("{game_name}", romName);
                if (isHero) pattern += "-fanart";
                string target = Path.Combine(_config.MarqueeImagePath, pattern) + Path.GetExtension(source).ToLowerInvariant();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) return false; // never overwrite
                File.Copy(source, target);
                _logger.LogInformation($"[Overrides] Using your own image: {source} -> {target}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Overrides] Could not copy {source}: {ex.Message}");
                return false;
            }
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
