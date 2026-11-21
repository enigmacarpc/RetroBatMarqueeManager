using System.Text.Json;
using RetroBatMarqueeManager.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace RetroBatMarqueeManager.Application.Services
{
    public class OffsetStorageService
    {
        private readonly string _storagePath;
        private readonly ILogger<OffsetStorageService> _logger;
        private Dictionary<string, SystemOffsetData> _offsets = new(StringComparer.OrdinalIgnoreCase); // EN: 'Windows' (EmulationStation) and 'windows' (folder) are the same system

        public OffsetStorageService(ILogger<OffsetStorageService> logger)
        {
            _logger = logger;
            _storagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "offsets.json");
            LoadOffsets();
        }

        public (int offX, int offY, int logoX, int logoY, double fanartScale, double logoScale) GetOffset(string system, string game)
        {
            if (_offsets.TryGetValue(system, out var sysData))
            {
                if (sysData.Games.TryGetValue(game, out var gameData))
                {
                    return (gameData.OffX, gameData.OffY, gameData.LogoX, gameData.LogoY, gameData.FanartScale, gameData.LogoScale);
                }
            }
            return (0, 0, 0, 0, 1.0, 1.0);
        }

        public void UpdateOffset(string system, string game, int dx, int dy, bool isLogo)
        {
            if (!_offsets.TryGetValue(system, out var sysData))
            {
                sysData = new SystemOffsetData();
                _offsets[system] = sysData;
            }

            if (!sysData.Games.TryGetValue(game, out var gameData))
            {
                gameData = new GameOffsetData();
                sysData.Games[game] = gameData;
            }

            if (isLogo)
            {
                gameData.LogoX += dx;
                gameData.LogoY += dy;
                _logger.LogInformation($"Updated Logo Offset for {system}/{game}: {gameData.LogoX}, {gameData.LogoY}");
            }
            else
            {
                gameData.OffX += dx;
                gameData.OffY += dy;
                _logger.LogInformation($"Updated Background Offset for {system}/{game}: {gameData.OffX}, {gameData.OffY}");
            }

            SaveOffsets();
        }

        public void UpdateScale(string system, string game, double delta, bool isLogo)
        {
            if (!_offsets.TryGetValue(system, out var sysData))
            {
                sysData = new SystemOffsetData();
                _offsets[system] = sysData;
            }

            if (!sysData.Games.TryGetValue(game, out var gameData))
            {
                gameData = new GameOffsetData();
                sysData.Games[game] = gameData;
            }

            if (isLogo)
            {
                gameData.LogoScale = Math.Max(0.1, Math.Min(5.0, gameData.LogoScale + delta));
                _logger.LogInformation($"Updated Logo Scale for {system}/{game}: {gameData.LogoScale:F2}");
            }
            else
            {
                gameData.FanartScale = Math.Max(0.1, Math.Min(5.0, gameData.FanartScale + delta));
                _logger.LogInformation($"Updated Fanart Scale for {system}/{game}: {gameData.FanartScale:F2}");
            }

            SaveOffsets();
        }

        private void LoadOffsets()
        {
            if (!File.Exists(_storagePath)) return;

            try
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, SystemOffsetData>>(json);
                if (data != null)
                {
                    // EN: Merge keys that differ only by case. The EmulationStation spelling (e.g. 'Windows') wins on conflict:
                    // that is the one the user was looking at when adjusting.
                    var merged = new Dictionary<string, SystemOffsetData>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in data.OrderBy(k => k.Key == k.Key.ToLowerInvariant() ? 0 : 1))
                    {
                        if (!merged.TryGetValue(kv.Key, out var target)) { merged[kv.Key] = kv.Value; continue; }
                        foreach (var g in kv.Value.Games) target.Games[g.Key] = g.Value;
                    }
                    if (merged.Count != data.Count) _logger.LogInformation($"[Offsets] Merged {data.Count - merged.Count} system entries that differed only by letter case.");
                    _offsets = merged;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load offsets: {ex.Message}");
            }
        }

        private void SaveOffsets()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_offsets, options);
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save offsets: {ex.Message}");
            }
        }

        public class SystemOffsetData
        {
            public Dictionary<string, GameOffsetData> Games { get; set; } = new();
        }

        public class GameOffsetData
        {
            public int OffX { get; set; }
            public int OffY { get; set; }
            public int LogoX { get; set; }
            public int LogoY { get; set; }
            public double FanartScale { get; set; } = 1.0;
            public double LogoScale { get; set; } = 1.0;
        }
    }
}
