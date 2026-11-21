# Star Wars arcade emulator

A C# emulator for Atari's 1983 *Star Wars* vector arcade hardware. It models the two Motorola 6809 CPUs, AVG vector generator, Matrix Processor, RIOT, four POKEYs, TMS5220 speech synthesizer, and X2212 NOVRAM. SDL3 provides the display, input, and audio front end.

## Requirements

- .NET 10 SDK
- Legally obtained Atari *Star Wars* ROM images in `Roms/`

ROM images are deliberately excluded. 
The expected ROM filenames are listed in `StarWarsMachine.LoadRoms`. 

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

## Code map

- `Cpu/M6809.cs` implements the main and sound CPU instruction set, flags, stacks, interrupts, and cycle accounting.
- `Hardware/StarWarsMemory.cs` describes the main- and sound-board memory maps.
- `Hardware/AtariStarWarsAvg.cs` runs the AVG state PROM and emits vector segments.
- `Hardware/StarWarsMathbox.cs` executes the Matrix Processor PROMs and divider/PRNG hardware.
- `Hardware/AtariPokey.cs`, `Hardware/Tms5220.cs`, and `Hardware/StarWarsAudio.cs` generate sound.
- `StarWarsMachine.cs` wires the boards together; `SdlVectorDisplay.cs` is the SDL front end.
