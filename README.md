# DynamicFog

Fog that comes and goes with the weather in Single Player Tarkov. DynamicFog is a **server mod** that lets fog
reach heavy levels again and makes it roll in, linger and clear on Tarkov's own clock, depending on the season,
time of day, wind, rain and temperature.

Support my work on Ko-fi: https://ko-fi.com/matsix

Vanilla SPT picks each weather point's fog from a table that tops out at a barely visible level, with most
picks near zero, so real fog almost never happens. DynamicFog replaces that with fog events.

## Features

- **Fog events on the Tarkov clock.** Fog rolls in over 1 to 2 Tarkov hours, holds for 2 to 6, then clears over
  1 to 2. One Tarkov hour is about 8.6 real minutes, so a typical event lasts about 55 real minutes.
- **A different pattern every Tarkov day.** A new Tarkov day starts about every 3.4 real hours, and each one
  can have up to two fog events.
- **Driven by conditions.** Fog is most likely at dawn and at night, in calm air, in rain, in the cold, and in
  autumn and winter. Conditions raise the odds; they never force fog.
- **Burn-off.** Fog thins toward midday and in wind, the way real fog lifts.
- **Light to heavy.** Each event picks its own strength, up to the game's "Heavy fog" level. Heavy fog is the
  rarest and happens mostly around dawn.
- **Fair to bots.** The mod changes the same weather value the game and SAIN use for bot vision, so bots see
  less in fog too.
- **Server-side.** Nothing to install on the client, and every player in a co-op raid gets the same fog.

## Requirements

- SPT 4.1 server.
- A fog mod that draws fog from the weather. DynamicFog only sets the weather's fog value. It was built for
  [FogSix](https://github.com/matsixx/FogSix).

## Install

1. Download the latest zip from the [Releases page](../../releases) and stop the SPT server.
2. Extract it into the server's `user/mods` folder (`SPT_Runtime/user/mods` in SPT 4.1). You should
   end up with:
   ```
   user/mods/DynamicFog/DynamicFog.dll
   user/mods/DynamicFog/config.json
   ```
3. Start the server. The console prints `[DynamicFog] active` when the mod has loaded.

## Settings

Edit `config.json` next to the DLL, then restart the server. If the file is missing, the mod creates it with
the default settings when the server starts.

| Setting | Default | What it does |
|---|---|---|
| Enabled | true | Turns the mod on or off. |
| MaxFog | 0.075 | Hard ceiling on fog. 0.075 is the game's "Heavy fog". |
| PeakFogMin / PeakFogMax | 0.015 / 0.075 | The range an event picks its strength from. |
| PeakSkew | 1.3 | Higher makes heavy fog rarer. |
| RampUpHoursMin / Max | 1 / 2 | How long fog takes to roll in, in Tarkov hours. |
| HoldHoursMin / Max | 2 / 6 | How long fog holds at its peak. |
| RampDownHoursMin / Max | 1 / 2 | How long fog takes to clear. |
| MinGapHours | 4 | Minimum clear time between two events. |
| MaxEventsPerDay | 2 | Most events one Tarkov day can have. |
| BaseChancePerHour | 0.04 | How often fog forms. Raise it for more fog. |
| MaxChancePerHour | 0.3 | Cap on the chance after all conditions are applied. |
| FogDayStartHour | 15 | The Tarkov hour where one fog day ends and the next begins. |
| PointSpacingMinutes | 15 | Tarkov minutes between the extra weather points added to each raid. |
| DayBurnOff / WindBurnOff | 0.35 / 0.4 | How much fog remains at midday and in strong wind. |
| DebugLog | false | Prints the status lines described below. |

Each condition also has an on/off switch and a multiplier: time of day, wind, rain, cold and season.

With the defaults, fog at moderate strength or more is present about 13% of the time in summer and 19% in
autumn. Heavy fog is present about 3% and 4% of the time.

### Watching it work

Set `DebugLog` to `true`. The server console then prints:

- a line each time a raid loads, with the Tarkov time, the current fog and the next fog event;
- the fog for the next 24 Tarkov hours;
- a status line every `HeartbeatSeconds` (60) with when the next fog event starts, in Tarkov time and real minutes.

`ForceConstantFog` is a test switch. Set it above 0 and every raid gets that fog value, which is a quick way
to confirm the mod and your fog mod are working together.

## How it works

The server sends each raid its upcoming weather forecast, and the game plays those points back in order of
Tarkov time of day. DynamicFog rewrites the weather a raid receives:

- fog comes from a timeline measured in Tarkov hours, worked out the same way for every raid in the same
  Tarkov day;
- extra weather points are added every 15 Tarkov minutes so fog can change smoothly. They copy the surrounding
  rain, wind and clouds; only the fog differs.

The server's own forecast is left untouched.

## Building from source

Needs the .NET 10 SDK. The SPT packages come from NuGet.

```sh
dotnet build -c Release
```

The build copies the DLL into your server when the `ServerModsDir` folder set in `DynamicFog.csproj` exists.
Point it at your own `user/mods` folder, or pass `-p:DeployToServer=false` to skip the copy.

## License

GNU General Public License v3.0. See `LICENSE.txt`.
