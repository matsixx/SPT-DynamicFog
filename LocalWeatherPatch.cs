using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Spt.Weather;

namespace DynamicFog;

/// <summary>
/// Postfix on WeatherController.GenerateLocal (client/localGame/weather — the weather a raid loads with):
/// rewrites the raid's weather so fog follows the Tarkov-time timeline.
/// </summary>
public class LocalWeatherPatch : AbstractPatch
{
    // Set by DynamicFogStartup before the patch is enabled (avoids the deprecated ServiceLocator).
    public static DynamicFogService? Service;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(WeatherController).GetMethod(nameof(WeatherController.GenerateLocal))!;
    }

    [PatchPostfix]
    public static void Postfix(GetLocalWeatherResponseData __result)
    {
        if (__result != null)
        {
            Service?.ShapeRaidWeather(__result);
        }
    }
}
