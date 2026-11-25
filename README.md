# Star Wars arcade emulator

A C# emulator for Atari's 1983 *Star Wars* vector arcade hardware. It models the two Motorola 6809 CPUs, AVG vector generator, Matrix Processor, RIOT, four POKEYs, TMS5220 speech synthesizer, and X2212 NOVRAM. SDL3 provides the display, input, and audio front end.

[Play Star Wars in your browser](https://jimbojetset.github.io/Star-Wars/)

## Requirements

- .NET 10 SDK

The arcade ROMs and PROMs are included in `Roms/` and are used by both builds.

## Run

```sh
dotnet run
```

The emulator starts full-screen. Press Escape to quit.

| Control | Keyboard / mouse |
| --- | --- |
| Yoke | Arrow keys or mouse |
| Both triggers | Space or left mouse button |
| Right trigger | Right Shift or right mouse button |
| Left / right thumb buttons | 1 / 2 |
| Left / right coin | C / V |
| Auxiliary coin | A |
| Tilt | T |
| Self-test toggle | F1 |
| Diagnostic step | F2 |
| Pause / resume | Ctrl+P |
| Sound on / off | Ctrl+Q |

NOVRAM is saved to `nvram/starwars/x2212` on exit.

## Run in a browser

Build the optimized browser version once and republish after changing the emulator or its web assets:

```sh
dotnet workload install wasm-tools
dotnet publish Web/StarWars.Web.csproj -c Release
```

Launch the published build with:

```sh
dotnet run --project Web/StarWars.Web.csproj
```

Open `http://localhost:8080`. The browser fetches the ROMs automatically. Click **Start game** to enable audio. Press C to insert a coin, then follow the on-screen prompts. Use P or the Pause button to pause. Open **Diagnostics** to check the audio buffer, underruns, and playback level. The browser saves X2212 NOVRAM for the current origin, so use the same URL to keep that save.

Safari Lockdown Mode can block the WebAssembly runtime this build requires. To keep Lockdown Mode enabled, play in Brave on macOS or run the SDL desktop build with `dotnet run`.

On macOS, the WebAssembly AOT compiler can fail if the SDK path contains spaces, such as a `dotnetup` install under `Application Support`. Use a .NET SDK installed at a path without spaces for the Release publish.

For development, `dotnet run --project Web/StarWars.Web.csproj -p:RunInterpreted=true` serves the current source build through the WebAssembly interpreter. It can be slower than the published build.

The build includes the ROMs from `Roms/` in the static output. Host that output only where you have permission to distribute the ROMs.

### GitHub Pages

The Pages workflow publishes the optimized browser build on pushes to `main` and can also run manually. It checks that the checkout contains all 13 ROMs and PROMs before building. GitHub Pages uses **GitHub Actions** as its publishing source.

## Code map

- `Core/StarWarsMachine.cs` wires together the boards shared by the desktop and browser builds.
- `Core/Cpu/M6809.cs` implements both CPUs, including interrupts and cycle accounting.
- `Core/Hardware/` contains the memory maps, AVG, mathbox, RIOT, POKEYs, TMS5220, and X2212.
- `SdlVectorDisplay.cs` handles desktop display, controls, and PCM playback.
- `Web/` is the Blazor WebAssembly front end. Canvas draws the AVG vectors and an AudioWorklet consumes the same 48 kHz PCM used by SDL.
