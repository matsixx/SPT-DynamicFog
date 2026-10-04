namespace DynamicFog;

/// <summary>
/// Live-tunable config (config.json next to the dll). Fog runs on the TARKOV clock (see FogTimeline): events
/// are measured in Tarkov hours (one Tarkov hour ≈ 8.6 real minutes), and each Tarkov day gets its own fog.
/// All chances are MULTIPLIERS on a base per-hour probability — conditions raise the odds, never force fog.
/// </summary>
public record DynamicFogConfig
{
    public bool Enabled { get; set; } = true;
    public bool DebugLog { get; set; } = false;

    /// When DebugLog is on, also log a periodic heartbeat (current Tarkov time, fog, next fog event) every this
    /// many REAL seconds so you can watch fog from the server console. 0 = no heartbeat.
    public double HeartbeatSeconds { get; set; } = 60;

    /// DIAGNOSTIC: when > 0, ignore all event logic and send EVERY raid weather node at this fog value.
    /// Use to prove the server→client fog path end-to-end (e.g. 0.075 = heavy fog everywhere).
    public double ForceConstantFog { get; set; } = 0.0;

    // ---- Event strength --------------------------------------------------------------------
    // Defaults were tuned by simulating SPT 4.1's weather tables through FogTimeline (2026-10-03):
    // moderate+ fog (>=0.02) ~13% of the time in summer / ~19% autumn, heavy (>=0.05) ~3% / ~4%,
    // heavy concentrated at dawn and never at midday; a typical event lasts ~6.4 Tarkov hours (~55 real min).
    // PeakSkew 1.6 makes heavy fog rarer (~2.7% / ~3.6%).

    /// Hard ceiling on fog. 0.075 == the game's "Heavy fog" raid-setup level.
    public double MaxFog { get; set; } = 0.075;
    /// An event picks a random peak in this range (capped at MaxFog).
    public double PeakFogMin { get; set; } = 0.015;
    public double PeakFogMax { get; set; } = 0.075;
    /// Bias of the peak pick: 1 = uniform across Min..Max; higher = light/moderate fog more common and heavy
    /// fog rarer (the peak is Min + (Max-Min) * random^PeakSkew).
    public double PeakSkew { get; set; } = 1.3;

    // ---- Event shape (Tarkov hours) --------------------------------------------------------
    /// Fog rolls in over this long.
    public double RampUpHoursMin { get; set; } = 1.0;
    public double RampUpHoursMax { get; set; } = 2.0;
    /// Fog holds at its peak (before burn-off) this long.
    public double HoldHoursMin { get; set; } = 2.0;
    public double HoldHoursMax { get; set; } = 6.0;
    /// Fog clears over this long.
    public double RampDownHoursMin { get; set; } = 1.0;
    public double RampDownHoursMax { get; set; } = 2.0;
    /// Minimum clear time between two fog events.
    public double MinGapHours { get; set; } = 4.0;

    // ---- When fog happens -------------------------------------------------------------------
    /// A "fog day" is 24 Tarkov hours starting at this Tarkov hour; each fog day rolls its own events.
    /// 15:00 puts the boundary in the afternoon, when fog is least likely, so night/dawn fog is never cut.
    public double FogDayStartHour { get; set; } = 15.0;
    /// At most this many fog events per fog day.
    public int MaxEventsPerDay { get; set; } = 2;
    /// Base chance per Tarkov hour that fog starts, before condition multipliers.
    public double BaseChancePerHour { get; set; } = 0.04;
    /// Clamp on the final (post-multiplier) per-hour chance.
    public double MaxChancePerHour { get; set; } = 0.3;

    /// Tarkov minutes between the extra weather points added to each raid, so fog can change smoothly however
    /// few forecast points are left (late in the UTC day). 15 = a point every ~2 real minutes in raid.
    public double PointSpacingMinutes { get; set; } = 15.0;

    // ---- Burn-off (fog thins DURING an event) ---------------------------------------------
    // Each moment of an event is scaled by these (1 = no thinning); morning (09-11) fades toward DayBurnOff.
    public bool UseBurnOff { get; set; } = true;
    /// Fog strength (fraction of the event's value) at midday, 12:00-17:00 Tarkov.
    public double DayBurnOff { get; set; } = 0.35;
    /// Fog strength in strong wind (>= WindyAbove); lerps from 1 at CalmWindBelow.
    public double WindBurnOff { get; set; } = 0.4;

    // ---- Conditions (multipliers on the per-hour chance) ------------------------------------
    // Time of day (dawn/night foggiest, midday burns off), on the Tarkov clock.
    public bool UseTimeOfDay { get; set; } = true;
    public double DawnMultiplier { get; set; } = 2.5;   // 05:00–08:00
    public double NightMultiplier { get; set; } = 2.0;  // 22:00–04:00
    public double DuskMultiplier { get; set; } = 1.3;   // 18:00–21:00
    public double DayMultiplier { get; set; } = 0.25;   // 12:00–17:00

    // Wind — calm air lets fog settle, wind disperses it.
    public bool UseWind { get; set; } = true;
    public double CalmWindMultiplier { get; set; } = 1.6;
    public double WindyMultiplier { get; set; } = 0.3;
    public double CalmWindBelow { get; set; } = 1.0;
    public double WindyAbove { get; set; } = 3.0;

    // Rain — humidity produces mist.
    public bool UseRain { get; set; } = true;
    public double RainMultiplier { get; set; } = 1.8;

    // Cold air near dewpoint favors fog.
    public bool UseCold { get; set; } = true;
    public double ColdMultiplier { get; set; } = 1.3;
    public double ColdBelowTemp { get; set; } = 5.0;

    // Season — autumn/winter foggiest, summer least.
    public bool UseSeason { get; set; } = true;
    public double SeasonSummerMultiplier { get; set; } = 0.6;
    public double SeasonAutumnMultiplier { get; set; } = 1.6;
    public double SeasonWinterMultiplier { get; set; } = 1.3;
    public double SeasonSpringMultiplier { get; set; } = 1.1;
}
