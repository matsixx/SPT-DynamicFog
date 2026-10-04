using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Weather;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Weather;
using SPTarkov.Server.Core.Utils;

namespace DynamicFog;

/// <summary>
/// Loads the config and rewrites the weather each raid receives so fog follows a Tarkov-time timeline
/// (see <see cref="FogTimeline"/>). The cached forecast itself is left vanilla.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class DynamicFogService
{
    private readonly ISptLogger<DynamicFogService> _logger;
    private readonly TimeUtil _timeUtil;
    private readonly WeatherConfig _weatherConfig;
    private readonly DynamicFogConfig _config;

    public DynamicFogService(
        ISptLogger<DynamicFogService> logger,
        TimeUtil timeUtil,
        WeatherConfig weatherConfig,
        ModHelper modHelper)
    {
        _logger = logger;
        _timeUtil = timeUtil;
        _weatherConfig = weatherConfig;

        _config = LoadConfig(modHelper);
    }

    /// The default settings as config.json text (same key names the file ships with).
    public static string DefaultConfigJson() =>
        JsonSerializer.Serialize(new DynamicFogConfig(), new JsonSerializerOptions { WriteIndented = true });

    /// Reads config.json from the mod folder. A missing file is written out with the defaults so there is
    /// something to edit; an unreadable one falls back to the defaults and is left alone.
    private DynamicFogConfig LoadConfig(ModHelper modHelper)
    {
        try
        {
            var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            var path = Path.Combine(modPath, "config.json");
            if (!File.Exists(path))
            {
                try
                {
                    File.WriteAllText(path, DefaultConfigJson());
                    _logger.Success("[DynamicFog] config.json was missing — created it with the default settings");
                }
                catch (Exception e)
                {
                    _logger.Warning($"[DynamicFog] config.json is missing and could not be created ({e.Message}); using defaults");
                }
                return new DynamicFogConfig();
            }
            return modHelper.GetJsonDataFromFile<DynamicFogConfig>(modPath, "config.json") ?? new DynamicFogConfig();
        }
        catch (Exception e)
        {
            _logger.Warning($"[DynamicFog] could not read config.json, using defaults: {e.Message}");
            return new DynamicFogConfig();
        }
    }

    public string Describe() =>
        _config.Enabled
            ? $"Tarkov-time fog: maxFog={_config.MaxFog}, baseChance={_config.BaseChancePerHour}/h, " +
              $"up to {_config.MaxEventsPerDay} events per fog day from {_config.FogDayStartHour:00}:00, " +
              $"hold {_config.HoldHoursMin}-{_config.HoldHoursMax} Tarkov h, points every {_config.PointSpacingMinutes} Tarkov min"
            : "disabled";

    // Exposed for the periodic heartbeat (DynamicFogHeartbeat).
    public bool DebugLog => _config.DebugLog;
    public double HeartbeatSeconds => _config.HeartbeatSeconds;

    public FogTimeline CreateTimeline(IEnumerable<Weather> forecast, Season? season) =>
        new(_config, forecast, _timeUtil.GetTimeStamp(), _weatherConfig.Acceleration ?? 7.0, season);

    /// <summary>
    /// Postfix target for WeatherController.GenerateLocal (the weather a raid loads with): replaces the node list
    /// with copies carrying Tarkov-time fog plus in-between points. Never mutates the cached forecast.
    /// </summary>
    public void ShapeRaidWeather(GetLocalWeatherResponseData response)
    {
        if (!_config.Enabled || response.Weather == null || response.Weather.Count == 0)
        {
            return;
        }

        try
        {
            if (_config.ForceConstantFog > 0)
            {
                var forced = Math.Clamp(_config.ForceConstantFog, 0.0, 1.0);
                response.Weather = response.Weather.Select(w => w with { Fog = forced }).ToList();
                _logger.Success($"[DynamicFog] TEST ForceConstantFog: raid weather ({response.Weather.Count} points) set to {forced:F4}");
                return;
            }

            var timeline = CreateTimeline(response.Weather, response.Season);
            if (!timeline.Valid)
            {
                _logger.Warning("[DynamicFog] raid weather had no readable times — left vanilla");
                return;
            }

            var forecastPoints = response.Weather.Count;
            response.Weather = timeline.BuildRaidWeather(out var added);

            if (_config.DebugLog)
            {
                _logger.Success($"[DynamicFog] raid weather: {forecastPoints} forecast + {added} added points | {Status(timeline)}");
                var samples = Enumerable.Range(0, 13)
                    .Select(k => timeline.TarkovNow + k * 2 * 3600.0)
                    .Select(a => $"{FogTimeline.Clock(a)} {timeline.Fog(a):F3}");
                _logger.Info($"[DynamicFog] fog over the next 24 Tarkov h: {string.Join(" | ", samples)}");
            }
        }
        catch (Exception e)
        {
            // Leave the vanilla response in place rather than break raid loading.
            _logger.Error($"[DynamicFog] failed to shape raid weather, sent vanilla: {e}");
        }
    }

    /// One-line status: Tarkov time, current fog + weather, and the current or next fog event.
    public string Status(FogTimeline t)
    {
        var now = t.TarkovNow;
        var w = t.SampleWeather(t.NowPos);
        var fog = Math.Clamp(Math.Max(w.Fog ?? 0.0, t.Fog(now)), 0.0, _config.MaxFog);
        var line = $"Tarkov {FogTimeline.Clock(now)}  fog={fog:F4} ({Label(fog)})  " +
                   $"rain={w.Rain ?? 0:F1} wind={w.WindSpeed ?? 0:F1} temp={w.Temperature ?? 0:F0}";

        var e = t.CurrentOrNextEvent(now);
        if (e == null)
        {
            return line + " | no fog in the next 2 Tarkov days";
        }
        if (e.Start <= now)
        {
            return line + $" | fog event under way: peak {e.Peak:F4} ({Label(e.Peak)}), clears ~{FogTimeline.Clock(e.End)} Tarkov " +
                   $"(in ~{t.RealMinutesUntil(e.End):F0} real min)";
        }
        return line + $" | next fog ~{FogTimeline.Clock(e.Start)} Tarkov (in ~{t.RealMinutesUntil(e.Start):F0} real min), " +
               $"peak {e.Peak:F4} ({Label(e.Peak)}), lasts ~{(e.End - e.Start) / 3600.0:F1} Tarkov h " +
               $"(~{(e.End - e.Start) / t.Acceleration / 60.0:F0} real min)";
    }

    private static string Label(double fog) =>
        fog >= 0.05 ? "HEAVY" : fog >= 0.02 ? "moderate" : fog >= 0.006 ? "light" : "clear";
}
