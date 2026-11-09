using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Closes the application shortly after RetroBat is closed. RetroBat counts as running while EmulationStation
    /// OR the RetroBat launcher process exists (the launcher stays alive while a game runs).
    /// The delay is RetroBatExitGraceSeconds in config.ini (default 15, 0 = never close automatically).
    /// FR: Ferme l'application peu après la fermeture de RetroBat.
    /// </summary>
    public class RetroBatMonitorService : BackgroundService
    {
        private readonly ILogger<RetroBatMonitorService> _logger;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly IConfigService _config;
        private static readonly string[] ProcessNames = { "emulationstation", "retrobat" };
        private const int CheckIntervalMs = 2000;

        private DateTime? _missingSince = null;

        public RetroBatMonitorService(ILogger<RetroBatMonitorService> logger, IHostApplicationLifetime appLifetime, IConfigService config)
        {
            _logger = logger;
            _appLifetime = appLifetime;
            _config = config;
        }

        private static bool IsRetroBatRunning()
        {
            foreach (var name in ProcessNames)
            {
                var processes = Process.GetProcessesByName(name);
                bool any = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int grace = _config.RetroBatExitGraceSeconds;
            if (grace <= 0)
            {
                _logger.LogInformation("RetroBat Monitor disabled (RetroBatExitGraceSeconds=0).");
                return;
            }

            _logger.LogInformation($"RetroBat Monitor started. Closing {grace}s after RetroBat has closed.");

            // Initial delay to let RetroBat start if launched together
            await Task.Delay(10000, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (IsRetroBatRunning())
                    {
                        if (_missingSince != null)
                        {
                            _logger.LogInformation("RetroBat process detected. Resetting shutdown timer.");
                            _missingSince = null;
                        }
                    }
                    else if (_missingSince == null)
                    {
                        _missingSince = DateTime.Now;
                        _logger.LogWarning($"RetroBat process not found. Closing in {grace}s unless it comes back.");
                    }
                    else if ((DateTime.Now - _missingSince.Value).TotalSeconds >= grace)
                    {
                        _logger.LogWarning("RetroBat is closed. Shutting down.");
                        _appLifetime.StopApplication();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error in RetroBat Monitor: {ex.Message}");
                }

                await Task.Delay(CheckIntervalMs, stoppingToken);
            }
        }
    }
}
