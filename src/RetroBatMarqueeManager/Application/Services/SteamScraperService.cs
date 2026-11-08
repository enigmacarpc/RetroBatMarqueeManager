using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Keyless scraper that uses Steam's own public endpoints (no API key, no SteamDB):
    ///     1. Steam store search finds the app ID from the game name.
    ///     2. The hero (library_hero.jpg) and logo (logo.png) are downloaded from Steam's image CDN by app ID.
    /// Like the SteamGridDB scraper it never returns a finished marquee. It "stages" the hero as {rom}-fanart and
    /// the logo as {rom} in the folders the Fanart + Logo composition already reads. Existing files are never overwritten.
    /// FR: Scraper sans clé qui utilise les points d'accès publics de Steam : recherche de l'app ID par nom, puis
    ///     téléchargement du hero et du logo depuis le CDN d'images de Steam. Les fichiers existants ne sont jamais écrasés.
    /// </summary>
    public class SteamScraperService : IScraperService
    {
        private const string StoreSearchUrl = "https://store.steampowered.com/api/storesearch/";

        // EN: Tried in order; a 403/404 on one host just moves on to the next.
        private static readonly string[] CdnBases =
        {
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps",
            "https://cdn.cloudflare.steamstatic.com/steam/apps"
        };

        public string Name => "Steam";
        public event Action<string, string, string?>? OnScrapeCompleted;

        private enum Asset { Hero, Logo }

        // EN: Problems that say nothing about the game itself (rate limit, network, server error).
        //     They are NOT written to the "not found" cache.
        private sealed class SteamTransientException : Exception
        {
            public SteamTransientException(string message) : base(message) { }
        }

        private readonly ILogger<SteamScraperService> _logger;
        private readonly IConfigService _config;
        private readonly ScrapeOverrideService _overrides;
        private readonly string _failedPath;

        private static readonly HttpClient _http;
        private static readonly SemaphoreSlim _apiGate = new SemaphoreSlim(1, 1); // one game at a time
        private static readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _pendingLock = new object();
        private static readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _failedLock = new object();
        private static DateTime _blockedUntilUtc = DateTime.MinValue;

        static SteamScraperService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBatMarqueeManager/1.0");
        }

        public SteamScraperService(ILogger<SteamScraperService> logger, IConfigService config, ScrapeOverrideService overrides)
        {
            _logger = logger;
            _config = config;
            _overrides = overrides;
            _failedPath = Path.Combine(_config.MarqueeImagePath, "_cache", "steam_failed.json");
            LoadFailed();
        }

        // ------------------------------------------------------------------ IScraperService

        public Task<string?> CheckAndScrapeAsync(string systemName, string gameName, string gamePath, string mediaType)
        {
            // EN: This scraper never returns a path: it stages files, the normal composition flow picks them up.
            if (!IsConfigured(out var wantHero, out var wantLogo))
                return Task.FromResult<string?>(null);

            string romName = ResolveRomName(gamePath, gameName);
            bool needHero = wantHero && NeedsDownload(Asset.Hero, systemName, romName);
            bool needLogo = wantLogo && NeedsDownload(Asset.Logo, systemName, romName);
            if (!needHero && !needLogo) return Task.FromResult<string?>(null);

            if (DateTime.UtcNow < _blockedUntilUtc) return Task.FromResult<string?>(null);

            string key = PendingKey(systemName, gameName);
            lock (_pendingLock)
            {
                if (!_pending.Add(key)) return Task.FromResult<string?>(null); // already running
            }

            _ = Task.Run(() => ScrapeInBackgroundAsync(systemName, gameName, romName, needHero, needLogo));
            return Task.FromResult<string?>(null);
        }

        public bool IsScraping(string systemName, string gameName, string mediaType)
        {
            lock (_pendingLock)
            {
                return _pending.Contains(PendingKey(systemName, gameName));
            }
        }

        /// <summary>
        /// EN: True when everything this scraper is responsible for is settled for the game (each wanted asset exists
        /// or is known to be missing on Steam) and at least one asset exists. Lets the manager stop the chain so a
        /// lower-priority scraper does not replace the staged hero + logo.
        /// </summary>
        public bool HasStagedMedia(string systemName, string gameName, string gamePath)
        {
            if (!IsConfigured(out var wantHero, out var wantLogo)) return false;

            string romName = ResolveRomName(gamePath, gameName);
            bool anyExists = false;

            foreach (var (wanted, asset) in new[] { (wantHero, Asset.Hero), (wantLogo, Asset.Logo) })
            {
                if (!wanted) continue;
                if (AssetExists(asset, systemName, romName)) { anyExists = true; continue; }
                if (!IsFailed(asset, systemName, romName)) return false; // not settled yet
            }
            return anyExists;
        }

        // ------------------------------------------------------------------ config helpers

        private bool IsConfigured(out bool wantHero, out bool wantLogo)
        {
            wantHero = false;
            wantLogo = false;

            if (!_config.MarqueeAutoScraping) return false;

            var parts = (_config.SteamMedia ?? "")
                .Split(new[] { ',', ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            wantHero = parts.Contains("hero", StringComparer.OrdinalIgnoreCase);
            wantLogo = parts.Contains("logo", StringComparer.OrdinalIgnoreCase);
            return wantHero || wantLogo;
        }

        private static string ResolveRomName(string gamePath, string gameName)
        {
            var name = Path.GetFileNameWithoutExtension(gamePath ?? "");
            return string.IsNullOrWhiteSpace(name) ? gameName : name;
        }

        private static string PendingKey(string systemName, string gameName) => $"{systemName}|{gameName}";
        private static string FailKey(Asset asset, string systemName, string romName) => $"{asset}|{systemName}|{romName}";

        // ------------------------------------------------------------------ where files live (mirrors MarqueeFileFinderService)

        // EN: Where we WRITE: same pattern the finder reads (MarqueeFilePath), hero gets "-fanart".
        private string StagingBase(Asset asset, string systemName, string romName)
        {
            var pattern = _config.MarqueeFilePath
                .Replace("{system_name}", systemName)
                .Replace("{game_name}", romName);
            if (asset == Asset.Hero) pattern += "-fanart";
            return Path.Combine(_config.MarqueeImagePath, pattern);
        }

        // EN: Second place the finder looks (MarqueeFilePathDefault, e.g. RetroBat roms\{system}\images\{game}-marquee).
        private string DefaultBase(Asset asset, string systemName, string romName)
        {
            var pattern = _config.MarqueeFilePathDefault
                .Replace("{system_name}", systemName)
                .Replace("{game_name}", romName);
            if (asset == Asset.Hero)
                pattern = pattern.Contains("-marquee") ? pattern.Replace("-marquee", "-fanart") : pattern + "-fanart";
            return Path.Combine(_config.MarqueeImagePathDefault, pattern);
        }

        private string? FindExisting(string basePath)
        {
            foreach (var ext in _config.AcceptedFormats)
            {
                var path = $"{basePath}.{ext.Trim()}";
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private bool AssetExists(Asset asset, string systemName, string romName)
        {
            return FindExisting(StagingBase(asset, systemName, romName)) != null
                || FindExisting(DefaultBase(asset, systemName, romName)) != null;
        }

        private bool NeedsDownload(Asset asset, string systemName, string romName)
        {
            return !AssetExists(asset, systemName, romName) && !IsFailed(asset, systemName, romName);
        }

        // ------------------------------------------------------------------ background work

        private async Task ScrapeInBackgroundAsync(string systemName, string gameName, string romName, bool needHero, bool needLogo)
        {
            string? resultPath = null;
            bool gateHeld = false;

            try
            {
                await _apiGate.WaitAsync();
                gateHeld = true;

                _logger.LogInformation($"[Steam] Starting scrape for '{gameName}' ({systemName}) [hero:{needHero} logo:{needLogo}]...");

                var ov = _overrides.Get(systemName, romName);
                if (ov != null && ov.HasDataFor(Name)) _overrides.MarkTried(Name, systemName, romName);

                int? appId = ov?.NumericIdFor(Name) ?? await FindAppIdAsync(gameName, ov);
                if (appId == null)
                {
                    _logger.LogWarning($"[Steam] No matching Steam app found for '{gameName}'.");
                    if (needHero) MarkFailed(Asset.Hero, systemName, romName);
                    if (needLogo) MarkFailed(Asset.Logo, systemName, romName);
                }
                else
                {
                    _logger.LogInformation($"[Steam] Matched '{gameName}' to app id {appId.Value}.");

                    if (needHero)
                    {
                        var path = await DownloadAssetAsync(Asset.Hero, appId.Value, systemName, romName);
                        if (path != null) resultPath ??= path;
                        else MarkFailed(Asset.Hero, systemName, romName);
                    }
                    if (needLogo)
                    {
                        var path = await DownloadAssetAsync(Asset.Logo, appId.Value, systemName, romName);
                        if (path != null) resultPath ??= path;
                        else MarkFailed(Asset.Logo, systemName, romName);
                    }
                }
            }
            catch (SteamTransientException ex)
            {
                // EN: Pause new scrapes briefly. Without this, the "scrape completed" event refreshes the game, which
                //     would immediately start another attempt for the same failure (retry loop).
                Block(TimeSpan.FromSeconds(30));
                _logger.LogWarning($"[Steam] Scrape of '{gameName}' interrupted: {ex.Message}");
            }
            catch (Exception ex)
            {
                Block(TimeSpan.FromSeconds(60));
                _logger.LogError($"[Steam] Error scraping '{gameName}': {ex.Message}");
            }
            finally
            {
                if (gateHeld) _apiGate.Release();
                // EN: Remove from pending BEFORE raising the event so listeners see IsScraping == false.
                lock (_pendingLock) { _pending.Remove(PendingKey(systemName, gameName)); }
            }

            try
            {
                if (resultPath != null)
                    _logger.LogInformation($"[Steam] Staged media for '{gameName}' (first file: {resultPath})");
                OnScrapeCompleted?.Invoke(systemName, gameName, resultPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Steam] Error in scrape-completed handler: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ Steam store search

        private async Task<int?> FindAppIdAsync(string gameName, GameOverride? ov = null)
        {
            // EN: An explicit Name override is trusted: the top Steam result is used.
            bool trust = !string.IsNullOrWhiteSpace(ov?.Name);
            foreach (var term in SearchTerms(trust ? ov!.Name! : gameName))
            {
                using var doc = await SearchStoreAsync(term);
                if (doc == null) continue;
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;

                string wanted = Normalize(term);
                int? firstClose = null;

                foreach (var item in items.EnumerateArray())
                {
                    // EN: Only real games/apps; skip bundles, subs, etc. when the type is given.
                    if (item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                        && !string.Equals(typeEl.GetString(), "app", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                    if (trust) return id;
                    string name = item.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
                    string norm = Normalize(name);
                    if (norm.Length == 0) continue;

                    if (norm == wanted) return id; // exact match wins

                    // EN: Accept a non-exact hit only if one name contains the other (avoids wrong-game artwork).
                    if (firstClose == null && wanted.Length > 0 && (norm.Contains(wanted) || wanted.Contains(norm)))
                        firstClose = id;
                }

                if (firstClose != null) return firstClose;
            }
            return null;
        }

        private static IEnumerable<string> SearchTerms(string gameName)
        {
            string basic = Regex.Replace(gameName ?? "", @"[\\/]+", " ").Trim();
            if (basic.Length > 0) yield return basic;

            // EN: Strip "(USA)", "[!]" and similar tags and retry.
            string cleaned = Regex.Replace(basic, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
            if (cleaned.Length > 0 && !cleaned.Equals(basic, StringComparison.OrdinalIgnoreCase)) yield return cleaned;
        }

        private static string Normalize(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "");

        // EN: null = nothing usable. Throws SteamTransientException for anything that does not describe the game itself.
        private async Task<JsonDocument?> SearchStoreAsync(string term)
        {
            string url = $"{StoreSearchUrl}?term={Uri.EscapeDataString(term)}&l=english&cc=us";

            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync(url);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                Block(TimeSpan.FromSeconds(30));
                throw new SteamTransientException($"Network error during store search: {ex.Message}");
            }

            using (resp)
            {
                if ((int)resp.StatusCode == 429)
                {
                    Block(TimeSpan.FromSeconds(60));
                    throw new SteamTransientException("Rate limited by Steam store search (HTTP 429). Pausing for 60 seconds.");
                }
                if (!resp.IsSuccessStatusCode)
                    throw new SteamTransientException($"HTTP {(int)resp.StatusCode} from Steam store search.");

                string body = await resp.Content.ReadAsStringAsync();
                try
                {
                    // EN: Be polite to the store: small pause between searches.
                    await Task.Delay(250);
                    return JsonDocument.Parse(body);
                }
                catch (JsonException ex)
                {
                    throw new SteamTransientException($"Unexpected (non-JSON) answer from Steam store search: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ Steam image CDN

        private static string[] FileNames(Asset asset) =>
            asset == Asset.Hero ? new[] { "library_hero.jpg" } : new[] { "logo.png" };

        // EN: Returns the extension to save with, or null if the app wouldn't find/read that format.
        private string? AcceptedExtension(string ext)
        {
            var accepted = _config.AcceptedFormats.Select(a => a.Trim().ToLowerInvariant()).ToList();
            if (accepted.Contains(ext)) return ext;
            if (ext == "jpg" && accepted.Contains("jpeg")) return "jpeg";
            return null;
        }

        private async Task<string?> DownloadAssetAsync(Asset asset, int appId, string systemName, string romName)
        {
            string targetBase = StagingBase(asset, systemName, romName);

            foreach (var file in FileNames(asset))
            {
                string? saveExt = AcceptedExtension(Path.GetExtension(file).TrimStart('.').ToLowerInvariant());
                if (saveExt == null)
                {
                    _logger.LogInformation($"[Steam] Skipping {file}: its format is not in AcceptedFormats.");
                    continue;
                }

                foreach (var baseUrl in CdnBases)
                {
                    string url = $"{baseUrl}/{appId}/{file}";
                    var path = await TryDownloadAsync(url, targetBase, saveExt);
                    if (path != null) return path;
                }
            }

            _logger.LogInformation($"[Steam] No {asset} image available on Steam's CDN for app id {appId}.");
            return null;
        }

        // EN: null = this URL has nothing (403/404). Throws SteamTransientException for network/server problems.
        private async Task<string?> TryDownloadAsync(string url, string targetBase, string ext)
        {
            string finalPath = $"{targetBase}.{ext}";
            if (File.Exists(finalPath)) return finalPath; // never overwrite

            string tmpPath = finalPath + ".part"; // not an accepted extension, so the finder ignores it

            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Forbidden)
                    return null;

                if ((int)resp.StatusCode == 429)
                {
                    Block(TimeSpan.FromSeconds(60));
                    throw new SteamTransientException("Rate limited by Steam's image CDN (HTTP 429). Pausing for 60 seconds.");
                }
                if (!resp.IsSuccessStatusCode)
                    throw new SteamTransientException($"Image download failed (HTTP {(int)resp.StatusCode}).");

                var mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (mediaType == null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new SteamTransientException($"Image download returned '{mediaType}' instead of an image.");

                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

                await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await resp.Content.CopyToAsync(fs);
                }

                if (File.Exists(finalPath))
                {
                    File.Delete(tmpPath); // someone created it meanwhile
                    return finalPath;
                }
                File.Move(tmpPath, finalPath);
                _logger.LogInformation($"[Steam] Saved {finalPath}");
                return finalPath;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                TryDelete(tmpPath);
                Block(TimeSpan.FromSeconds(30));
                throw new SteamTransientException($"Network error while downloading image: {ex.Message}");
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private static void Block(TimeSpan span)
        {
            var until = DateTime.UtcNow + span;
            if (until > _blockedUntilUtc) _blockedUntilUtc = until;
        }

        // ------------------------------------------------------------------ "not found" cache

        private bool IsFailed(Asset asset, string systemName, string romName)
        {
            if (asset == Asset.Logo && _overrides.IsNoLogo(systemName, romName)) return true; // user removed the logo (Ctrl+D)
            if (_overrides.ShouldBypass(Name, systemName, romName)) return false; // fresh attempt after an override edit
            if (_overrides.IsRejected(systemName, romName)) return true;          // discarded with Ctrl+Shift+R
            lock (_failedLock) { return _failed.Contains(FailKey(asset, systemName, romName)); }
        }

        private void MarkFailed(Asset asset, string systemName, string romName)
        {
            lock (_failedLock)
            {
                _failed.Add(FailKey(asset, systemName, romName));
                SaveFailed();
            }
        }

        private void LoadFailed()
        {
            try
            {
                lock (_failedLock)
                {
                    if (!File.Exists(_failedPath)) return;
                    if (_config.RetryMissingOnStart)
                    {
                    // EN: "Not found" results are retried at every app start: forget the saved list instead of loading it.
                        File.Delete(_failedPath);
                        _logger.LogInformation("[Steam] Cleared saved 'not found' list: missing media is searched again after each app start.");
                        return;
                    }
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_failedPath));
                    if (list == null) return;
                    foreach (var item in list) _failed.Add(item);
                    _logger.LogInformation($"[Steam] Loaded {_failed.Count} cached 'not found' entries.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Steam] Failed to load not-found cache: {ex.Message}");
            }
        }

        // EN: Caller must hold _failedLock.
        private void SaveFailed()
        {
            try
            {
                var dir = Path.GetDirectoryName(_failedPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_failedPath, JsonSerializer.Serialize(_failed.ToList()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Steam] Failed to save not-found cache: {ex.Message}");
            }
        }
    }
}
