using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace DynamicFog;

/// <summary>
/// Mod metadata read by the SPT mod loader (SPT 4.1: IModMetadata; 4.0's AbstractModMetadata is gone).
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.matsix.dynamicfog";
    public string Name { get; init; } = "DynamicFog";
    public string Author { get; init; } = "matsix";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "GPL-3.0";
}

/// <summary>
/// Entry point. Enables the raid-weather patch and forces the fog service to construct early (so its config is
/// loaded/logged at startup).
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class DynamicFogStartup(
    ISptLogger<DynamicFogStartup> logger,
    DynamicFogService service // injected so DI builds it now (loads config)
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        LocalWeatherPatch.Service = service;
        new LocalWeatherPatch().Enable();
        logger.Success($"[DynamicFog] active — {service.Describe()}");
        return Task.CompletedTask;
    }
}
