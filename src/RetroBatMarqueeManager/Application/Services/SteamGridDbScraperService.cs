using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Scraper for SteamGridDB (https://www.steamgriddb.com) using its v2 API and a personal API key.
    /// Unlike ArcadeItalia/ScreenScraper it does not return a finished marquee. It "stages" a wide HERO image
    /// (saved as {rom}-fanart) and a transparent LOGO (saved as {rom}) in the folders the composition step already
    /// searches, so the existing Fanart + Logo composition builds the marquee. Existing files are never overwritten.
    /// FR: Scraper SteamGridDB (API v2 + clé API). Il dépose un "hero" ({rom}-fanart) et un logo ({rom}) aux
    /// emplacements déjà utilisés par la composition Fanart + Logo ; les fichiers existants ne sont jamais écrasés.
    /// </summary>
    public class SteamGridDbScraperService : IScraperService
    {
        private const string ApiBase = "https://www.steamgriddb.com/api/v2";

        public string Name => "SteamGridDB";
        public event Action<string, string, string?>? OnScrapeCompleted;

        private enum Asset { Hero, Logo }

        // EN: Thrown for problems that say nothing about the game itself (bad key, rate limit, network, server error),
        //     so they are NOT written to the "not found" cache.
        private sealed class SgdbTransientException : Exception
        {
            public SgdbTransientException(string message) : base(message) { }
        }

        private readonly ILogger<SteamGridDbScraperService> _logger;
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
        private static bool _warnedNoKey;

        static SteamGridDbScraperService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBatMarqueeManager/1.0");
        }

        public SteamGridDbScraperService(ILogger<SteamGridDbScraperService> logger, IConfigService config, ScrapeOverrideService overrides)
        {
            _logger = logger;
            _config = config;
            _overrides = overrides;
            _failedPath = Path.Combine(_config.MarqueeImagePath, "_cache", "steamgriddb_failed.json");
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
        /// or is known to be missing on SteamGridDB) and at least one asset exists. Lets the manager stop the chain
        /// so a lower-priority scraper does not replace the staged hero + logo.
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

            var parts = (_config.SteamGridDbMedia ?? "")
                .Split(new[] { ',', ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            wantHero = parts.Contains("hero", StringComparer.OrdinalIgnoreCase);
            wantLogo = parts.Contains("logo", StringComparer.OrdinalIgnoreCase);
            if (!wantHero && !wantLogo) return false;

            if (string.IsNullOrWhiteSpace(_config.SteamGridDbApiKey))
            {
                if (!_warnedNoKey)
                {
                    _warnedNoKey = true;
                    _logger.LogWarning("[SteamGridDB] No API key configured (SteamGridDbApiKey). Scraper is inactive.");
                }
                return false;
            }
            return true;
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

                _logger.LogInformation($"[SteamGridDB] Starting scrape for '{gameName}' ({systemName}) [hero:{needHero} logo:{needLogo}]...");

                var ov = _overrides.Get(systemName, romName);
                if (ov != null && ov.HasDataFor(Name)) _overrides.MarkTried(Name, systemName, romName);

                int? gameId = ov?.NumericIdFor(Name) ?? await FindGameIdAsync(gameName, ov);
                if (gameId == null)
                {
                    _logger.LogWarning($"[SteamGridDB] No matching game found for '{gameName}'.");
                    if (needHero) MarkFailed(Asset.Hero, systemName, romName);
                    if (needLogo) MarkFailed(Asset.Logo, systemName, romName);
                }
                else
                {
                    if (needHero)
                    {
                        var path = await DownloadBestAsync(Asset.Hero, gameId.Value, systemName, romName);
                        if (path != null) resultPath ??= path;
                        else MarkFailed(Asset.Hero, systemName, romName);
                    }
                    if (needLogo)
                    {
                        var path = await DownloadBestAsync(Asset.Logo, gameId.Value, systemName, romName);
                        if (path != null) resultPath ??= path;
                        else MarkFailed(Asset.Logo, systemName, romName);
                    }
                }
            }
            catch (SgdbTransientException ex)
            {
                // EN: Pause new scrapes briefly. Without this, the "scrape completed" event refreshes the game, which
                //     would immediately start another attempt for the same failure (retry loop).
                Block(TimeSpan.FromSeconds(30));
                _logger.LogWarning($"[SteamGridDB] Scrape of '{gameName}' interrupted: {ex.Message}");
            }
            catch (Exception ex)
            {
                Block(TimeSpan.FromSeconds(60));
                _logger.LogError($"[SteamGridDB] Error scraping '{gameName}': {ex.Message}");
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
                    _logger.LogInformation($"[SteamGridDB] Staged media for '{gameName}' (first file: {resultPath})");
                OnScrapeCompleted?.Invoke(systemName, gameName, resultPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[SteamGridDB] Error in scrape-completed handler: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ SteamGridDB API

        private async Task<int?> FindGameIdAsync(string gameName, GameOverride? ov = null)
        {
            // EN: An explicit Name override is trusted: the top result is used.
            bool trust = !string.IsNullOrWhiteSpace(ov?.Name);
            foreach (var term in SearchTerms(trust ? ov!.Name! : gameName))
            {
                using var doc = await GetJsonAsync($"{ApiBase}/search/autocomplete/{Uri.EscapeDataString(term)}");
                if (doc == null) continue;
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;

                string wanted = Normalize(term);
                int? firstClose = null;

                foreach (var item in data.EnumerateArray())
                {
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

        private async Task<string?> DownloadBestAsync(Asset asset, int gameId, string systemName, string romName)
        {
            string endpoint = asset == Asset.Hero ? "heroes" : "logos";
            string url = $"{ApiBase}/{endpoint}/game/{gameId}?nsfw=false&humor=false&epilepsy=false&types=static";

            using var doc = await GetJsonAsync(url, retryWithoutQueryOn400: true);
            if (doc == null) return null;
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

            var candidates = new List<(string url, string ext, int score, int width)>();
            foreach (var item in data.EnumerateArray())
            {
                string? imageUrl = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(imageUrl)) continue;

                string? ext = PickSaveExtension(item, imageUrl);
                if (ext == null) continue; // format the app can't read

                int score = item.TryGetProperty("score", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                int width = item.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
                candidates.Add((imageUrl, ext, score, width));
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation($"[SteamGridDB] No usable {asset} image for game id {gameId}.");
                return null;
            }

            // EN: Logos: prefer PNG (transparency), then best score. Heroes: best score, then widest.
            var best = asset == Asset.Logo
                ? candidates.OrderByDescending(c => c.ext == "png").ThenByDescending(c => c.score).First()
                : candidates.OrderByDescending(c => c.score).ThenByDescending(c => c.width).First();

            string targetBase = StagingBase(asset, systemName, romName);
            return await DownloadImageAsync(best.url, targetBase, best.ext);
        }

        // EN: Returns the file extension to save with, or null if the app wouldn't find/read that format.
        private string? PickSaveExtension(JsonElement item, string imageUrl)
        {
            string? mime = item.TryGetProperty("mime", out var m) ? m.GetString() : null;
            string ext = (mime?.ToLowerInvariant()) switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/webp" => "webp",
                _ => ""
            };

            if (ext.Length == 0)
            {
                try { ext = Path.GetExtension(new Uri(imageUrl).AbsolutePath).TrimStart('.').ToLowerInvariant(); }
                catch { return null; }
                if (ext == "jpeg") ext = "jpg";
            }

            var accepted = _config.AcceptedFormats.Select(a => a.Trim().ToLowerInvariant()).ToList();
            if (accepted.Contains(ext)) return ext;
            if (ext == "jpg" && accepted.Contains("jpeg")) return "jpeg";
            return null;
        }

        private async Task<string> DownloadImageAsync(string url, string targetBase, string ext)
        {
            string finalPath = $"{targetBase}.{ext}";
            if (File.Exists(finalPath)) return finalPath; // never overwrite

            string tmpPath = finalPath + ".part"; // not an accepted extension, so the finder ignores it
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                    throw new SgdbTransientException($"Image download failed (HTTP {(int)resp.StatusCode}).");

                var mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (mediaType == null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new SgdbTransientException($"Image download returned '{mediaType}' instead of an image.");

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
                _logger.LogInformation($"[SteamGridDB] Saved {finalPath}");
                return finalPath;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                TryDelete(tmpPath);
                throw new SgdbTransientException($"Network error while downloading image: {ex.Message}");
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

        // EN: GET + parse JSON. null = HTTP 404 (nothing there). Throws SgdbTransientException for everything that
        //     does not describe the game itself.
        private async Task<JsonDocument?> GetJsonAsync(string url, bool retryWithoutQueryOn400 = false)
        {
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.SteamGridDbApiKey.Trim());
                resp = await _http.SendAsync(req);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                Block(TimeSpan.FromSeconds(30));
                throw new SgdbTransientException($"Network error: {ex.Message}");
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    Block(TimeSpan.FromMinutes(10));
                    throw new SgdbTransientException($"API key rejected (HTTP {(int)resp.StatusCode}). Check SteamGridDbApiKey in config.ini.");
                }
                if ((int)resp.StatusCode == 429)
                {
                    Block(TimeSpan.FromSeconds(60));
                    throw new SgdbTransientException("Rate limited by SteamGridDB (HTTP 429). Pausing for 60 seconds.");
                }
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;

                if (resp.StatusCode == HttpStatusCode.BadRequest && retryWithoutQueryOn400 && url.Contains('?'))
                {
                    _logger.LogWarning("[SteamGridDB] HTTP 400 with filters; retrying without query filters.");
                    return await GetJsonAsync(url.Substring(0, url.IndexOf('?')), retryWithoutQueryOn400: false);
                }

                if (!resp.IsSuccessStatusCode)
                    throw new SgdbTransientException($"HTTP {(int)resp.StatusCode} from SteamGridDB.");

                string body = await resp.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    doc.Dispose();
                    throw new SgdbTransientException("SteamGridDB reported success=false.");
                }
                return doc;
            }
        }

        private static void Block(TimeSpan span)
        {
            var until = DateTime.UtcNow + span;
            if (until > _blockedUntilUtc) _blockedUntilUtc = until;
        }

        // ------------------------------------------------------------------ "not found" cache

        private bool IsFailed(Asset asset, string systemName, string romName)
        {
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
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_failedPath));
                    if (list == null) return;
                    foreach (var item in list) _failed.Add(item);
                    _logger.LogInformation($"[SteamGridDB] Loaded {_failed.Count} cached 'not found' entries.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SteamGridDB] Failed to load not-found cache: {ex.Message}");
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
                _logger.LogWarning($"[SteamGridDB] Failed to save not-found cache: {ex.Message}");
            }
        }
    }
}
