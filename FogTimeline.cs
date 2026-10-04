using System.Globalization;
using SPTarkov.Server.Core.Models.Eft.Weather;
using SPTarkov.Server.Core.Models.Enums;

namespace DynamicFog;

/// <summary>
/// Fog on the TARKOV clock, built per raid from the server's forecast.
///
/// How the game uses raid weather (decompiled EFT client + SPT server source): each node's `time` string is
/// "UTC date + the node's Tarkov time of day", and the client keys its weather curves on that, then samples them
/// on the raid clock (today's UTC date + current Tarkov time, running at the server's acceleration). A 24 real
/// hour forecast covers ~7 Tarkov days, all folded onto one date, so inside a raid the nodes play in Tarkov
/// time-of-day order, not forecast order. Fog authored along the forecast therefore arrived as scattered patches.
///
/// So the raid's weather list is rebuilt here:
///   - fog comes from a Tarkov-time timeline: events in Tarkov hours, at most MaxEventsPerDay per "fog day"
///     (24 Tarkov hours from FogDayStartHour), deterministic per fog day so co-op players agree;
///   - each node gets the timeline's value at the Tarkov moment the raid clock will reach it;
///   - extra nodes every PointSpacingMinutes (Tarkov) let fog change smoothly however few forecast nodes are
///     left; their other weather is interpolated from the forecast, which the original nodes keep exactly.
/// </summary>
public sealed class FogTimeline
{
    private const double Day = 86400.0;
    private const double Hour = 3600.0;
    private const double RussiaOffset = 3 * Hour;   // WeatherHelper.GetInRaidTime: tarkov = real * accel + 3h
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    // The raid weather list spans this window around "now", in Tarkov hours. 30h ahead covers a raid on the
    // other time option (+12h) running ~2.5 real hours.
    private const double LeadHours = 2.0;
    private const double CoverHours = 30.0;

    public sealed record FogEvent(double Start, double Up, double Hold, double Down, double Peak)
    {
        public double End => Start + Up + Hold + Down;
    }

    private readonly DynamicFogConfig _cfg;
    private readonly List<(DateTime Pos, Weather W)> _keys = [];
    private readonly Dictionary<long, List<FogEvent>> _events = new();
    private readonly double _seasonFactor;

    public long NowTimestamp { get; }
    public double Acceleration { get; }
    public double TarkovNow { get; }
    public DateTime NowPos { get; }

    public FogTimeline(DynamicFogConfig cfg, IEnumerable<Weather> forecast, long nowTimestamp, double acceleration, Season? season)
    {
        _cfg = cfg;
        NowTimestamp = nowTimestamp;
        Acceleration = acceleration > 0 ? acceleration : 7.0;
        TarkovNow = RussiaOffset + nowTimestamp * Acceleration;
        NowPos = DateTimeOffset.FromUnixTimeSeconds(nowTimestamp).UtcDateTime.Date.AddSeconds(Mod(TarkovNow, Day));
        _seasonFactor = SeasonFactor(season);

        foreach (var w in forecast)
        {
            if (w.Time != null && DateTime.TryParseExact(w.Time, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pos))
            {
                _keys.Add((pos, w));
            }
        }
        _keys.Sort((a, b) => a.Pos.CompareTo(b.Pos));
    }

    public bool Valid => _keys.Count > 0;
    public int ForecastPoints => _keys.Count;

    /// Absolute Tarkov time (seconds) at which the raid clock reaches this curve position, and back.
    public double TarkovAt(DateTime pos) => TarkovNow + (pos - NowPos).TotalSeconds;
    public DateTime PosAt(double tarkov) => NowPos.AddSeconds(tarkov - TarkovNow);
    public double RealMinutesUntil(double tarkov) => (tarkov - TarkovNow) / Acceleration / 60.0;
    public static string Clock(double tarkov) => TimeSpan.FromSeconds(Mod(tarkov, Day)).ToString(@"hh\:mm");

    // ---- Raid weather list ---------------------------------------------------------------------

    /// <summary>The weather list to send to a raid: copies of the forecast nodes in the window plus in-between
    /// nodes, ordered along the raid clock, each with its Tarkov-time fog.</summary>
    public List<Weather> BuildRaidWeather(out int added)
    {
        var from = NowPos.AddHours(-LeadHours);
        var to = NowPos.AddHours(CoverHours);
        var list = new List<(DateTime Pos, Weather W)>();
        foreach (var (pos, w) in _keys)
        {
            if (pos >= from && pos <= to)
            {
                list.Add((pos, w with { }));   // copy: never mutate the cached forecast
            }
        }

        var step = TimeSpan.FromMinutes(Math.Max(5.0, _cfg.PointSpacingMinutes));
        var minSeparation = step / 3;   // skip an added node that would sit right on top of a forecast node
        added = 0;
        for (var pos = from; pos <= to; pos += step)
        {
            if (NearestKeyDistance(pos) < minSeparation)
            {
                continue;
            }
            var ts = (long)Math.Round(NowTimestamp + (pos - NowPos).TotalSeconds / Acceleration);
            list.Add((pos, SampleWeather(pos) with
            {
                Time = pos.ToString(TimeFormat, CultureInfo.InvariantCulture),
                Date = pos.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Timestamp = ts,
                SptInRaidTimestamp = ts,
            }));
            added++;
        }

        // The client takes the curve's span from the first and last list entries: keep it in clock order.
        list.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        foreach (var (pos, w) in list)
        {
            // Never below the vanilla value, never above the ceiling.
            w.Fog = Math.Clamp(Math.Max(w.Fog ?? 0.0, Fog(TarkovAt(pos))), 0.0, _cfg.MaxFog);
        }
        return list.Select(x => x.W).ToList();
    }

    /// The weather the raid shows at this curve position, interpolated between forecast nodes. Outside the
    /// forecast's span, fold by whole Tarkov days (the raid's pattern repeats by time of day on a date).
    public Weather SampleWeather(DateTime pos)
    {
        var first = _keys[0].Pos;
        var last = _keys[^1].Pos;
        if (pos > last)
        {
            var folded = pos.AddDays(-Math.Ceiling((pos - last).TotalDays));
            pos = folded >= first ? folded : last;
        }
        else if (pos < first)
        {
            var folded = pos.AddDays(Math.Ceiling((first - pos).TotalDays));
            pos = folded <= last ? folded : first;
        }

        var hi = 0;
        while (hi < _keys.Count && _keys[hi].Pos < pos)
        {
            hi++;
        }
        if (hi == 0)
        {
            return _keys[0].W with { };
        }
        if (hi == _keys.Count)
        {
            return _keys[^1].W with { };
        }

        var (pa, a) = _keys[hi - 1];
        var (pb, b) = _keys[hi];
        var span = (pb - pa).TotalSeconds;
        var t = span > 0 ? (pos - pa).TotalSeconds / span : 0.0;
        return (t < 0.5 ? a : b) with
        {
            Pressure = Lerp(a.Pressure, b.Pressure, t),
            Temperature = Lerp(a.Temperature, b.Temperature, t),
            Fog = Lerp(a.Fog, b.Fog, t),
            RainIntensity = Lerp(a.RainIntensity, b.RainIntensity, t),
            Rain = Lerp(a.Rain, b.Rain, t),
            WindGustiness = Lerp(a.WindGustiness, b.WindGustiness, t),
            WindSpeed = Lerp(a.WindSpeed, b.WindSpeed, t),
            Cloud = Lerp(a.Cloud, b.Cloud, t),
        };
    }

    private TimeSpan NearestKeyDistance(DateTime pos)
    {
        var best = TimeSpan.MaxValue;
        foreach (var (p, _) in _keys)
        {
            var d = (p - pos).Duration();
            if (d < best)
            {
                best = d;
            }
        }
        return best;
    }

    // ---- Fog timeline --------------------------------------------------------------------------

    /// Fog at an absolute Tarkov time: the fog day's events, thinned by burn-off (midday sun, wind).
    public double Fog(double tarkov)
    {
        var f = 0.0;
        foreach (var e in EventsFor(FogDayIndex(tarkov)))
        {
            var x = tarkov - e.Start;
            double shape;
            if (x < 0 || x >= e.Up + e.Hold + e.Down)
            {
                continue;
            }
            if (x < e.Up)
            {
                shape = x / e.Up;
            }
            else if (x < e.Up + e.Hold)
            {
                shape = 1.0;
            }
            else
            {
                shape = 1.0 - (x - e.Up - e.Hold) / e.Down;
            }
            f = Math.Max(f, e.Peak * shape);
        }
        if (f <= 0)
        {
            return 0.0;
        }
        return f * BurnOff(HourOfDay(tarkov), SmoothedWind(tarkov));
    }

    /// Wind averaged over ±1 Tarkov hour. The raid's wind jumps between neighbouring forecast nodes (they come
    /// from different real times), and burn-off following it raw made fog pulse every few minutes.
    private double SmoothedWind(double tarkov)
    {
        var sum = 0.0;
        for (var k = -2; k <= 2; k++)
        {
            sum += SampleWeather(PosAt(tarkov + k * 1800.0)).WindSpeed ?? 0.0;
        }
        return sum / 5.0;
    }

    /// The event under way at <paramref name="tarkov"/>, else the next one within the next few fog days.
    public FogEvent? CurrentOrNextEvent(double tarkov, int fogDaysAhead = 2)
    {
        var day = FogDayIndex(tarkov);
        for (var d = day; d <= day + fogDaysAhead; d++)
        {
            foreach (var e in EventsFor(d).OrderBy(e => e.Start))
            {
                if (e.End > tarkov)
                {
                    return e;
                }
            }
        }
        return null;
    }

    private long FogDayIndex(double tarkov) =>
        (long)Math.Floor((tarkov - _cfg.FogDayStartHour * Hour) / Day);

    /// A fog day's events. Deterministic per fog day: the same day always rolls the same dice, so every raid
    /// (and every co-op player) in that Tarkov day sees the same fog; only the weather conditions feeding the
    /// odds come from the live forecast.
    private List<FogEvent> EventsFor(long fogDay)
    {
        if (_events.TryGetValue(fogDay, out var cached))
        {
            return cached;
        }

        var events = new List<FogEvent>();   // in hours from the fog day's start
        var dayStart = fogDay * Day + _cfg.FogDayStartHour * Hour;
        var rng = new Random(Seed(fogDay));
        var gap = Math.Max(0.0, _cfg.MinGapHours);
        var upMin = Math.Max(0.1, _cfg.RampUpHoursMin);
        var holdMin = Math.Max(0.0, _cfg.HoldHoursMin);
        var downMin = Math.Max(0.1, _cfg.RampDownHoursMin);
        var minLength = upMin + holdMin + downMin;

        for (var n = 0; n < Math.Max(0, _cfg.MaxEventsPerDay); n++)
        {
            // Candidate start hours that keep a full gap from the fog day's edges and from placed events.
            var candidates = new List<(int Hour, double Chance)>();
            for (var k = (int)Math.Ceiling(gap / 2); k <= 24 - gap / 2 - minLength; k++)
            {
                if (events.All(e => k >= e.End + gap || k + minLength + gap <= e.Start))
                {
                    candidates.Add((k, StartChance(dayStart + k * Hour)));
                }
            }
            if (candidates.Count == 0)
            {
                break;
            }

            // Does fog form at all? (As if each candidate hour rolled its own chance.)
            var noFog = candidates.Aggregate(1.0, (acc, c) => acc * (1.0 - c.Chance));
            if (rng.NextDouble() >= 1.0 - noFog)
            {
                break;
            }

            // When: weighted by each hour's chance, so fog favours calm, damp nights and dawns.
            var pick = rng.NextDouble() * candidates.Sum(c => c.Chance);
            var startHour = candidates[^1].Hour;
            foreach (var c in candidates)
            {
                pick -= c.Chance;
                if (pick <= 0)
                {
                    startHour = c.Hour;
                    break;
                }
            }

            var start = startHour + rng.NextDouble();
            var up = Uniform(rng, upMin, _cfg.RampUpHoursMax);
            var hold = Uniform(rng, holdMin, _cfg.HoldHoursMax);
            var down = Uniform(rng, downMin, _cfg.RampDownHoursMax);
            var peak = Math.Min(_cfg.MaxFog,
                Lerp(_cfg.PeakFogMin, _cfg.PeakFogMax, Math.Pow(rng.NextDouble(), Math.Max(0.1, _cfg.PeakSkew))));

            // Clear before the fog day ends and before any later event (minus the gap): shorten the hold first.
            var endLimit = 24 - gap / 2;
            foreach (var e in events.Where(e => e.Start > start))
            {
                endLimit = Math.Min(endLimit, e.Start - gap);
            }
            var over = start + up + hold + down - endLimit;
            if (over > 0)
            {
                var cut = Math.Min(over, hold);
                hold -= cut;
                over -= cut;
                down = Math.Max(0.25, down - over);
            }
            events.Add(new FogEvent(start, up, hold, down, peak));
        }

        var result = events
            .Select(e => new FogEvent(dayStart + e.Start * Hour, e.Up * Hour, e.Hold * Hour, e.Down * Hour, e.Peak))
            .ToList();
        _events[fogDay] = result;
        return result;
    }

    /// Chance that fog starts in this Tarkov hour: a base chance times the condition multipliers.
    private double StartChance(double tarkov)
    {
        var w = SampleWeather(PosAt(tarkov));
        var chance = _cfg.BaseChancePerHour
                     * _seasonFactor
                     * TimeFactor(HourOfDay(tarkov))
                     * WindFactor(w.WindSpeed ?? 0.0)
                     * RainFactor(w.Rain ?? 0.0)
                     * ColdFactor(w.Temperature ?? 99.0);
        return Math.Clamp(chance, 0.0, _cfg.MaxChancePerHour);
    }

    // ---- Conditions ----------------------------------------------------------------------------

    /// Fraction of an event's fog that survives: the sun burns it off through the day, wind disperses it.
    private double BurnOff(int hour, double wind)
    {
        if (!_cfg.UseBurnOff)
        {
            return 1.0;
        }
        var sun = hour switch
        {
            >= 9 and <= 11 => Lerp(1.0, _cfg.DayBurnOff, (hour - 8) / 4.0),
            >= 12 and <= 17 => _cfg.DayBurnOff,
            _ => 1.0,
        };
        var windT = (wind - _cfg.CalmWindBelow) / Math.Max(0.0001, _cfg.WindyAbove - _cfg.CalmWindBelow);
        return sun * Lerp(1.0, _cfg.WindBurnOff, windT);
    }

    private double TimeFactor(int hour)
    {
        if (!_cfg.UseTimeOfDay)
        {
            return 1.0;
        }
        return hour switch
        {
            >= 5 and <= 8 => _cfg.DawnMultiplier,
            >= 9 and <= 11 => Lerp(_cfg.DawnMultiplier, _cfg.DayMultiplier, (hour - 8) / 4.0),   // morning burn-off
            >= 12 and <= 17 => _cfg.DayMultiplier,
            >= 18 and <= 21 => _cfg.DuskMultiplier,
            _ => _cfg.NightMultiplier,   // 22:00-04:00
        };
    }

    private double WindFactor(double wind)
    {
        if (!_cfg.UseWind)
        {
            return 1.0;
        }
        if (wind <= _cfg.CalmWindBelow)
        {
            return _cfg.CalmWindMultiplier;
        }
        if (wind >= _cfg.WindyAbove)
        {
            return _cfg.WindyMultiplier;
        }
        var t = (wind - _cfg.CalmWindBelow) / Math.Max(0.0001, _cfg.WindyAbove - _cfg.CalmWindBelow);
        return Lerp(_cfg.CalmWindMultiplier, _cfg.WindyMultiplier, t);
    }

    // Server rain scale: above 1 means actual rain.
    private double RainFactor(double rain) => _cfg.UseRain && rain > 1.0 ? _cfg.RainMultiplier : 1.0;

    private double ColdFactor(double temperature) =>
        _cfg.UseCold && temperature <= _cfg.ColdBelowTemp ? _cfg.ColdMultiplier : 1.0;

    private double SeasonFactor(Season? season)
    {
        if (!_cfg.UseSeason || season == null)
        {
            return 1.0;
        }
        // Match by name so we don't depend on exact enum members across SPT versions.
        var name = season.Value.ToString().ToUpperInvariant();
        if (name.Contains("WINTER"))
        {
            return _cfg.SeasonWinterMultiplier;
        }
        if (name.Contains("AUTUMN") || name.Contains("STORM"))
        {
            return _cfg.SeasonAutumnMultiplier;
        }
        if (name.Contains("SPRING"))
        {
            return _cfg.SeasonSpringMultiplier;
        }
        if (name.Contains("SUMMER"))
        {
            return _cfg.SeasonSummerMultiplier;
        }
        return 1.0;
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static int HourOfDay(double tarkov) => (int)(Mod(tarkov, Day) / Hour) % 24;

    private static double Mod(double x, double m) => ((x % m) + m) % m;

    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0.0, 1.0);

    private static double? Lerp(double? a, double? b, double t) =>
        a.HasValue && b.HasValue ? a.Value + (b.Value - a.Value) * t : (t < 0.5 ? a : b);

    private static double Uniform(Random rng, double min, double max) =>
        min + (Math.Max(min, max) - min) * rng.NextDouble();

    // SplitMix64: a stable seed per fog day (HashCode is randomised per process, so it can't be used).
    private static int Seed(long fogDay)
    {
        var z = unchecked((ulong)fogDay * 0x9E3779B97F4A7C15UL + 0x6A09E667F3BCC909UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return unchecked((int)(z ^ (z >> 31)));
    }
}
