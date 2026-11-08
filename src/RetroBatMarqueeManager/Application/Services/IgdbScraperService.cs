using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: IGDB (igdb.com) scraper. IGDB is owned by Twitch, so access uses a Twitch application:
    ///     IgdbClientId + IgdbClientSecret (dev.twitch.tv/console) are exchanged for a short-lived app access token.
    ///     1. The game is searched by name (POST https://api.igdb.com/v4/games).
    ///     2. The best wide artwork (or screenshot) is downloaded from IGDB's image CDN.
    /// IGDB has NO game logos, so this scraper only stages the hero as {rom}-fanart (same folders and "never overwrite"
    /// rule as the Steam / SteamGridDB scrapers). It does not stop the scraper chain: a lower-priority source such as
    /// Steam or SteamGridDB can still add the logo. Without a logo the composition uses the system logo.
    /// FR: Scraper IGDB (identifiants Twitch). Ne dépose que le hero ({rom}-fanart) : IGDB n'a pas de logos de jeux.
    ///     N'interrompt pas la chaîne de scrapers afin qu'une source de priorité inférieure puisse ajouter le logo.
    /// </summary>
    public class IgdbScraperService : IScraperService
    {
        private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
        private const string GamesUrl = "https://api.igdb.com/v4/games";
        private const string ImageBase = "https://images.igdb.com/igdb/image/upload";
        private const string ImageSize = "t_1080p"; // 1920x1080

        public string Name => "IGDB";
        public event Action<string, string, string?>? OnScrapeCompleted;

        // EN: Problems that say nothing about the game itself (rate limit, network, server error, bad credentials).
        //     They are NOT written to the "not found" cache.
        private sealed class IgdbTransientException : Exception
        {
            public IgdbTransientException(string message) : base(message) { }
        }

        private readonly ILogger<IgdbScraperService> _logger;
        private readonly IConfigService _config;
        private readonly ScrapeOverrideService _overrides;
        private readonly string _failedPath;

        private static readonly HttpClient _http;
        private static readonly SemaphoreSlim _apiGate = new SemaphoreSlim(1, 1); // one game at a time (also guards the token)
        private static readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _pendingLock = new object();
        private static readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _failedLock = new object();
        private static DateTime _blockedUntilUtc = DateTime.MinValue;
        private static bool _warnedNoCredentials;

        // Twitch app access token (only touched while holding _apiGate)
        private static string? _token;
        private static DateTime _tokenExpiresUtc = DateTime.MinValue;
        private static string? _tokenOwner; // client id + secret the token was issued for

        static IgdbScraperService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBatMarqueeManager/1.0");
        }

        public IgdbScraperService(ILogger<IgdbScraperService> logger, IConfigService config, ScrapeOverrideService overrides)
        {
            _logger = logger;
            _config = config;
            _overrides = overrides;
            _failedPath = Path.Combine(_config.MarqueeImagePath, "_cache", "igdb_failed.json");
            LoadFailed();
        }

        // ------------------------------------------------------------------ IScraperService

        public Task<string?> CheckAndScrapeAsync(string systemName, string gameName, string gamePath, string mediaType)
        {
            // EN: This scraper never returns a path: it stages the hero, the normal composition flow picks it up.
            if (!IsConfigured(out _)) return Task.FromResult<string?>(null);

            string romName = ResolveRomName(gamePath, gameName);
            if (!NeedsDownload(systemName, romName)) return Task.FromResult<string?>(null);

            if (DateTime.UtcNow < _blockedUntilUtc) return Task.FromResult<string?>(null);

            string key = PendingKey(systemName, gameName);
            lock (_pendingLock)
            {
                if (!_pending.Add(key)) return Task.FromResult<string?>(null); // already running
            }

            _ = Task.Run(() => ScrapeInBackgroundAsync(systemName, gameName, romName));
            return Task.FromResult<string?>(null);
        }

        public bool IsScraping(string systemName, string gameName, string mediaType)
        {
            lock (_pendingLock)
            {
                return _pending.Contains(PendingKey(systemName, gameName));
            }
        }

        // EN: HasStagedMedia is deliberately NOT overridden (stays false): IGDB only provides the hero, so it must not
        //     stop lower-priority sources that can add a logo.

        // ------------------------------------------------------------------ config helpers

        private bool IsConfigured(out string[] kinds)
        {
            kinds = Array.Empty<string>();

            if (!_config.MarqueeAutoScraping) return false;

            if (string.IsNullOrWhiteSpace(_config.IgdbClientId) || string.IsNullOrWhiteSpace(_config.IgdbClientSecret))
            {
                if (!_warnedNoCredentials)
                {
                    _warnedNoCredentials = true;
                    _logger.LogWarning("[IGDB] IgdbClientId / IgdbClientSecret are not set in config.ini. Scraper is inactive.");
                }
                return false;
            }

            // EN: Order = preference. "artwork,screenshot" tries artworks first, then screenshots.
            kinds = (_config.IgdbMedia ?? "")
                .Split(new[] { ',', ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.ToLowerInvariant())
                // EN: "hero" = best wide image: artworks first, then screenshots (same as the default order).
                .SelectMany(k => k == "hero" ? new[] { "artwork", "screenshot" } : new[] { k })
                .Where(k => k == "artwork" || k == "screenshot")
                .Distinct()
                .ToArray();
            return kinds.Length > 0;
        }

        private static string ResolveRomName(string gamePath, string gameName)
        {
            var name = Path.GetFileNameWithoutExtension(gamePath ?? "");
            return string.IsNullOrWhiteSpace(name) ? gameName : name;
        }

        private static string PendingKey(string systemName, string gameName) => $"{systemName}|{gameName}";
        private static string FailKey(string systemName, string romName) => $"{systemName}|{romName}";

        // ------------------------------------------------------------------ where files live (mirrors MarqueeFileFinderService)

        // EN: Where we WRITE the hero: same pattern the finder reads (MarqueeFilePath) plus "-fanart".
        private string StagingBase(string systemName, string romName)
        {
            var pattern = _config.MarqueeFilePath
                .Replace("{system_name}", systemName)
                .Replace("{game_name}", romName) + "-fanart";
            return Path.Combine(_config.MarqueeImagePath, pattern);
        }

        // EN: Second place the finder looks (MarqueeFilePathDefault, e.g. RetroBat roms\{system}\images\{game}-marquee).
        private string DefaultBase(string systemName, string romName)
        {
            var pattern = _config.MarqueeFilePathDefault
                .Replace("{system_name}", systemName)
                .Replace("{game_name}", romName);
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

        private bool NeedsDownload(string systemName, string romName)
        {
            if (FindExisting(StagingBase(systemName, romName)) != null) return false;
            if (FindExisting(DefaultBase(systemName, romName)) != null) return false;
            return !IsFailed(systemName, romName);
        }

        // ------------------------------------------------------------------ background work

        private async Task ScrapeInBackgroundAsync(string systemName, string gameName, string romName)
        {
            string? resultPath = null;
            bool gateHeld = false;

            try
            {
                await _apiGate.WaitAsync();
                gateHeld = true;

                if (!IsConfigured(out var kinds)) return;

                _logger.LogInformation($"[IGDB] Starting scrape for '{gameName}' ({systemName}) [media: {string.Join(",", kinds)}]...");

                var ov = _overrides.Get(systemName, romName);
                if (ov != null && ov.HasDataFor(Name)) _overrides.MarkTried(Name, systemName, romName);

                string? imageId = await FindImageIdAsync(gameName, kinds, ov);
                if (imageId == null)
                {
                    _logger.LogWarning($"[IGDB] No matching game with artwork/screenshot found for '{gameName}'.");
                    MarkFailed(systemName, romName);
                }
                else
                {
                    resultPath = await DownloadImageAsync(imageId, systemName, romName);
                    if (resultPath == null) MarkFailed(systemName, romName);
                }
            }
            catch (IgdbTransientException ex)
            {
                // EN: Pause new scrapes briefly. Without this, the "scrape completed" event refreshes the game, which
                //     would immediately start another attempt for the same failure (retry loop).
                Block(TimeSpan.FromSeconds(30));
                _logger.LogWarning($"[IGDB] Scrape of '{gameName}' interrupted: {ex.Message}");
            }
            catch (Exception ex)
            {
                Block(TimeSpan.FromSeconds(60));
                _logger.LogError($"[IGDB] Error scraping '{gameName}': {ex.Message}");
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
                    _logger.LogInformation($"[IGDB] Staged hero for '{gameName}' ({resultPath})");
                OnScrapeCompleted?.Invoke(systemName, gameName, resultPath);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[IGDB] Error in scrape-completed handler: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ Twitch token

        // EN: Caller must hold _apiGate.
        private async Task<string> GetTokenAsync(bool forceRefresh)
        {
            string clientId = _config.IgdbClientId.Trim();
            string clientSecret = _config.IgdbClientSecret.Trim();
            string owner = clientId + ":" + clientSecret;

            if (!forceRefresh && _token != null && _tokenOwner == owner && DateTime.UtcNow < _tokenExpiresUtc)
                return _token;

            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                    new KeyValuePair<string, string>("grant_type", "client_credentials")
                })
            };

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                throw new IgdbTransientException($"Network error while requesting the Twitch token: {ex.Message}");
            }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.Forbidden
                    || resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // EN: Wrong id/secret will not fix itself; wait a long time instead of hammering Twitch.
                    Block(TimeSpan.FromMinutes(10));
                    throw new IgdbTransientException($"Twitch rejected the credentials (HTTP {(int)resp.StatusCode}). Check IgdbClientId and IgdbClientSecret in config.ini. Pausing for 10 minutes.");
                }
                if ((int)resp.StatusCode == 429)
                {
                    Block(TimeSpan.FromSeconds(60));
                    throw new IgdbTransientException("Rate limited by Twitch (HTTP 429). Pausing for 60 seconds.");
                }
                if (!resp.IsSuccessStatusCode)
                    throw new IgdbTransientException($"Twitch token request failed (HTTP {(int)resp.StatusCode}).");

                string body = await resp.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    string? token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(token))
                        throw new IgdbTransientException("Twitch answered without an access_token.");

                    int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 3600;
                    _token = token;
                    _tokenOwner = owner;
                    _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120)); // renew 2 min early
                    _logger.LogInformation("[IGDB] Obtained a Twitch access token.");
                    return token;
                }
                catch (JsonException ex)
                {
                    throw new IgdbTransientException($"Unexpected (non-JSON) answer from Twitch: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ IGDB search

        private sealed class ImageCandidate
        {
            public string ImageId = "";
            public int Width;
            public int Height;
        }

        private async Task<string?> FindImageIdAsync(string gameName, string[] kinds, GameOverride? ov = null)
        {
            // EN: Manual fix 1: an exact IGDB game ID.
            int? forcedId = ov?.NumericIdFor(Name);
            if (forcedId != null)
            {
                using var byId = await QueryGamesAsync(null, forcedId);
                if (byId != null && byId.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var game in byId.RootElement.EnumerateArray())
                    {
                        var img = PickImage(game, kinds);
                        if (img != null) return img;
                    }
                return null;
            }

            // EN: Manual fix 2: an explicit Name is trusted (first result that has artwork).
            bool trust = !string.IsNullOrWhiteSpace(ov?.Name);
            foreach (var term in SearchTerms(trust ? ov!.Name! : gameName))
            {
                using var doc = await QueryGamesAsync(term);
                if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                if (trust)
                {
                    foreach (var game in doc.RootElement.EnumerateArray())
                    {
                        var img = PickImage(game, kinds);
                        if (img != null) return img;
                    }
                    continue;
                }

                string wanted = Normalize(term);
                string? exactImage = null;
                string? closeImage = null;

                foreach (var game in doc.RootElement.EnumerateArray())
                {
                    string? imageId = PickImage(game, kinds);
                    if (imageId == null) continue; // game without usable artwork/screenshot

                    var names = new List<string>();
                    if (game.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) names.Add(n.GetString() ?? "");
                    if (game.TryGetProperty("alternative_names", out var alts) && alts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var alt in alts.EnumerateArray())
                            if (alt.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                                names.Add(an.GetString() ?? "");
                    }

                    foreach (var raw in names)
                    {
                        string norm = Normalize(raw);
                        if (norm.Length == 0 || wanted.Length == 0) continue;

                        if (norm == wanted) { exactImage ??= imageId; }
                        // EN: Accept a non-exact hit only if one name contains the other (avoids wrong-game artwork).
                        else if (norm.Contains(wanted) || wanted.Contains(norm)) { closeImage ??= imageId; }
                    }

                    if (exactImage != null) break;
                }

                var chosen = exactImage ?? closeImage;
                if (chosen != null) return chosen;
            }
            return null;
        }

        // EN: Best image of the preferred kind: landscape first, then the widest. Falls through to the next kind.
        private static string? PickImage(JsonElement game, string[] kinds)
        {
            foreach (var kind in kinds)
            {
                string prop = kind == "artwork" ? "artworks" : "screenshots";
                if (!game.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

                var list = new List<ImageCandidate>();
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    if (!el.TryGetProperty("image_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                    string id = idEl.GetString() ?? "";
                    if (!Regex.IsMatch(id, "^[A-Za-z0-9]+$")) continue; // ids are plain alphanumerics

                    int w = el.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var wi) ? wi : 0;
                    int h = el.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var hi) ? hi : 0;
                    list.Add(new ImageCandidate { ImageId = id, Width = w, Height = h });
                }

                if (list.Count == 0) continue;

                return list
                    .OrderByDescending(c => c.Width >= c.Height) // landscape first (unknown size 0>=0 counts as landscape)
                    .ThenByDescending(c => c.Width)
                    .First().ImageId;
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

        private static string EscapeQuery(string s) =>
            (s ?? "").Replace("\\", " ").Replace("\"", " ").Replace("\r", " ").Replace("\n", " ");

        // EN: null = nothing usable. Throws IgdbTransientException for anything that does not describe the game itself.
        private async Task<JsonDocument?> QueryGamesAsync(string? term, int? id = null)
        {
            string query =
                (id != null ? $"where id = {id.Value}; " : $"search \"{EscapeQuery(term ?? "")}\"; ") +
                "fields name,alternative_names.name,artworks.image_id,artworks.width,artworks.height," +
                "screenshots.image_id,screenshots.width,screenshots.height; limit 10;";

            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await GetTokenAsync(forceRefresh: attempt > 0);

                using var req = new HttpRequestMessage(HttpMethod.Post, GamesUrl)
                {
                    Content = new StringContent(query, Encoding.UTF8, "text/plain")
                };
                req.Headers.Add("Client-ID", _config.IgdbClientId.Trim());
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(req);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    throw new IgdbTransientException($"Network error during IGDB search: {ex.Message}");
                }

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        _token = null; // expired or revoked: fetch a fresh one and retry once
                        continue;
                    }
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        Block(TimeSpan.FromMinutes(10));
                        throw new IgdbTransientException($"IGDB rejected the request (HTTP {(int)resp.StatusCode}). Check IgdbClientId and IgdbClientSecret in config.ini. Pausing for 10 minutes.");
                    }
                    if ((int)resp.StatusCode == 429)
                    {
                        Block(TimeSpan.FromSeconds(60));
                        throw new IgdbTransientException("Rate limited by IGDB (HTTP 429). Pausing for 60 seconds.");
                    }
                    if (!resp.IsSuccessStatusCode)
                        throw new IgdbTransientException($"HTTP {(int)resp.StatusCode} from IGDB search.");

                    string body = await resp.Content.ReadAsStringAsync();
                    try
                    {
                        // EN: IGDB allows 4 requests per second; stay well below.
                        await Task.Delay(300);
                        return JsonDocument.Parse(body);
                    }
                    catch (JsonException ex)
                    {
                        throw new IgdbTransientException($"Unexpected (non-JSON) answer from IGDB: {ex.Message}");
                    }
                }
            }

            throw new IgdbTransientException("IGDB search failed after refreshing the token.");
        }

        // ------------------------------------------------------------------ IGDB image CDN

        // EN: Returns the extension to save with, or null if the app wouldn't find/read that format.
        private string? AcceptedExtension(string ext)
        {
            var accepted = _config.AcceptedFormats.Select(a => a.Trim().ToLowerInvariant()).ToList();
            if (accepted.Contains(ext)) return ext;
            if (ext == "jpg" && accepted.Contains("jpeg")) return "jpeg";
            return null;
        }

        private async Task<string?> DownloadImageAsync(string imageId, string systemName, string romName)
        {
            string? saveExt = AcceptedExtension("jpg");
            if (saveExt == null)
            {
                _logger.LogInformation("[IGDB] Skipping download: jpg is not in AcceptedFormats.");
                return null;
            }

            string finalPath = $"{StagingBase(systemName, romName)}.{saveExt}";
            if (File.Exists(finalPath)) return finalPath; // never overwrite

            string tmpPath = finalPath + ".part"; // not an accepted extension, so the finder ignores it
            string url = $"{ImageBase}/{ImageSize}/{imageId}.jpg";

            try
            {
                // EN: Plain request: the Twitch token / Client-ID are only ever sent to the API hosts, not the image CDN.
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    _logger.LogInformation($"[IGDB] Image {imageId} is not available on IGDB's CDN.");
                    return null;
                }
                if ((int)resp.StatusCode == 429)
                {
                    Block(TimeSpan.FromSeconds(60));
                    throw new IgdbTransientException("Rate limited by IGDB's image CDN (HTTP 429). Pausing for 60 seconds.");
                }
                if (!resp.IsSuccessStatusCode)
                    throw new IgdbTransientException($"Image download failed (HTTP {(int)resp.StatusCode}).");

                var mediaType = resp.Content.Headers.ContentType?.MediaType;
                if (mediaType == null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new IgdbTransientException($"Image download returned '{mediaType}' instead of an image.");

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
                _logger.LogInformation($"[IGDB] Saved {finalPath}");
                return finalPath;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                TryDelete(tmpPath);
                throw new IgdbTransientException($"Network error while downloading image: {ex.Message}");
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

        private bool IsFailed(string systemName, string romName)
        {
            if (_overrides.ShouldBypass(Name, systemName, romName)) return false; // fresh attempt after an override edit
            if (_overrides.IsRejected(systemName, romName)) return true;          // discarded with Ctrl+Shift+R
            lock (_failedLock) { return _failed.Contains(FailKey(systemName, romName)); }
        }

        private void MarkFailed(string systemName, string romName)
        {
            lock (_failedLock)
            {
                _failed.Add(FailKey(systemName, romName));
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
                        _logger.LogInformation("[IGDB] Cleared saved 'not found' list: missing media is searched again after each app start.");
                        return;
                    }
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_failedPath));
                    if (list == null) return;
                    foreach (var item in list) _failed.Add(item);
                    _logger.LogInformation($"[IGDB] Loaded {_failed.Count} cached 'not found' entries.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[IGDB] Failed to load not-found cache: {ex.Message}");
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
                _logger.LogWarning($"[IGDB] Failed to save not-found cache: {ex.Message}");
            }
        }
    }
}
