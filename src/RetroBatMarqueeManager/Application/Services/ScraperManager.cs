using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    public class ScraperManager : IScraperManager
    {
        public event Action<string, string, string?>? OnScrapeCompleted;

        private readonly IEnumerable<IScraperService> _scrapers;
        private readonly IConfigService _config;
        private readonly ILogger<ScraperManager> _logger;

        public ScraperManager(IEnumerable<IScraperService> scrapers, IConfigService config, ILogger<ScraperManager> logger)
        {
            _scrapers = scrapers;
            _config = config;
            _logger = logger;

            foreach (var scraper in _scrapers)
            {
                scraper.OnScrapeCompleted += (sys, game, path) =>
                {
                   OnScrapeCompleted?.Invoke(sys, game, path);
                };
            }

            var priorities = _config.ScraperPriorities;
            if (priorities.Count == 1 && _scrapers.Count() > 1)
                _logger.LogWarning($"[ScraperManager] PrioritySource lists only '{priorities[0]}'. Only the sources listed there are used: add every source you want, in order, separated by commas (available: {string.Join(", ", _scrapers.Select(s => s.Name))}).");

            _logger.LogInformation($"[ScraperManager] Initialized with {_scrapers.Count()} scrapers: {string.Join(", ", _scrapers.Select(s => s.Name))}");
        }

        public async Task<string?> CheckAndScrapeAsync(string systemName, string gameName, string gamePath, string mediaType)
        {
            var priorities = _config.ScraperPriorities;
            
            // Iterate through configured priorities
            foreach (var scraperName in priorities)
            {
                var scraper = _scrapers.FirstOrDefault(s => s.Name.Equals(scraperName, StringComparison.OrdinalIgnoreCase));
                if (scraper != null)
                {
                    // _logger.LogDebug($"[ScraperManager] Trying source: {scraperName} for {gameName}");
                    string? result;
                    try
                    {
                        result = await scraper.CheckAndScrapeAsync(systemName, gameName, gamePath, mediaType);
                    }
                    catch (Exception ex)
                    {
                        // EN: A failing source must not block the ones after it.
                        _logger.LogError($"[ScraperManager] {scraperName} failed for {gameName}: {ex.Message}. Trying next source.");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(result))
                    {
                        _logger.LogInformation($"[ScraperManager] Found media via {scraperName} for {gameName} ({result})");
                        return result;
                    }

                    // EN: Strict Priority: If this scraper is now working (background), do not proceed to lower priorities.
                    // FR: Priorité stricte : Si ce scraper travaille (arrière-plan), ne pas passer aux suivants.
                    if (scraper.IsScraping(systemName, gameName, mediaType))
                    {
                        // _logger.LogDebug($"[ScraperManager] {scraperName} is handling the request. Stopping chain.");
                        return null;
                    }

                    // EN: Scrapers that stage files for the composition flow (e.g. SteamGridDB hero + logo) return no path.
                    //     Once their media is in place, stop here so lower priorities do not replace it.
                    // FR: Les scrapers qui déposent des fichiers pour la composition ne renvoient pas de chemin.
                    //     Une fois les médias en place, on s'arrête ici.
                    if (scraper.HasStagedMedia(systemName, gameName, gamePath))
                    {
                        return null;
                    }
                }
                else
                {
                    _logger.LogWarning($"[ScraperManager] Configured scraper source '{scraperName}' not found/registered.");
                }
            }

            return null;
        }

        public bool IsScraping(string systemName, string gameName, string mediaType)
        {
            return _scrapers.Any(s => s.IsScraping(systemName, gameName, mediaType));
        }

        public string? GetActiveScraperName(string systemName, string gameName, string mediaType)
        {
             // EN: Check scrapers in priority order first
             // FR: Vérifier les scrapers dans l'ordre de priorité d'abord
             var priorities = _config.ScraperPriorities;
             
             foreach (var scraperName in priorities)
             {
                 var scraper = _scrapers.FirstOrDefault(s => s.Name.Equals(scraperName, StringComparison.OrdinalIgnoreCase));
                 if (scraper != null && scraper.IsScraping(systemName, gameName, mediaType))
                 {
                     return scraper.Name;
                 }
             }

             // EN: Fallback to any other scraper not in priority list (unlikely but safe)
             // FR: Repli sur tout autre scraper non listé (improbable mais sûr)
             return _scrapers.FirstOrDefault(s => s.IsScraping(systemName, gameName, mediaType))?.Name;
        }
    }
}
