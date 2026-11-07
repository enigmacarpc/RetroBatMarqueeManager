using System;
using System.Threading.Tasks;

namespace RetroBatMarqueeManager.Core.Interfaces
{
    public interface IScraperService
    {
        string Name { get; }
        event Action<string, string, string?> OnScrapeCompleted;
        Task<string?> CheckAndScrapeAsync(string systemName, string gameName, string gamePath, string mediaType);
        bool IsScraping(string systemName, string gameName, string mediaType);

        /// <summary>
        /// EN: For scrapers that "stage" files (e.g. a hero + logo) for the normal composition flow instead of
        /// returning a finished image: true when that media is already in place, so the manager stops the chain
        /// and lower-priority scrapers do not replace it. Default: false (scraper returns its own result).
        /// FR: Pour les scrapers qui déposent des fichiers pour la composition normale : true si les médias sont
        /// déjà en place, afin que la chaîne s'arrête. Par défaut : false.
        /// </summary>
        bool HasStagedMedia(string systemName, string gameName, string gamePath) => false;
    }
}
