using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils;

namespace DynamicFog;

/// <summary>
/// Periodic heartbeat. When DebugLog is on, logs every HeartbeatSeconds: the current Tarkov time, the fog a raid
/// started now would have, and when the current or next fog event happens (Tarkov time + real minutes).
/// </summary>
[Injectable(TypePriority = OnUpdateOrder.InsuranceCallbacks)]
public class DynamicFogHeartbeat(
    ISptLogger<DynamicFogHeartbeat> logger,
    TimeUtil timeUtil,
    RaidWeatherService raidWeatherService,
    SeasonalEventService seasonalEventService,
    DynamicFogService fogService
) : IOnUpdate
{
    private long _nextLogAt;

    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        var interval = fogService.HeartbeatSeconds;
        if (!fogService.DebugLog || interval <= 0)
        {
            return Task.FromResult(true);
        }

        // Timestamp-gated so the heartbeat fires ~every HeartbeatSeconds regardless of how often the
        // server's update loop ticks.
        var now = timeUtil.GetTimeStamp();
        if (now < _nextLogAt)
        {
            return Task.FromResult(true);
        }
        _nextLogAt = now + (long)Math.Max(5, interval);

        try
        {
            var season = seasonalEventService.GetActiveWeatherSeason();
            var timeline = fogService.CreateTimeline(raidWeatherService.GetUpcomingWeather(), season);
            logger.Success(timeline.Valid
                ? $"[DynamicFog] {fogService.Status(timeline)}  season={season}"
                : $"[DynamicFog] heartbeat: no forecast yet (season={season})");
        }
        catch (Exception e)
        {
            logger.Warning($"[DynamicFog] heartbeat failed: {e.Message}");
        }

        return Task.FromResult(true);
    }
}
