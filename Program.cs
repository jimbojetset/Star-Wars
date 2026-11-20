using System.Diagnostics;
using StarWars;
using StarWars.Hardware;

try
{
    var machine = new StarWarsMachine();
    try
    {
        string romDirectory = Path.Combine(AppContext.BaseDirectory, "Roms");
        if (!Directory.Exists(romDirectory))
        {
            romDirectory = Path.Combine(Environment.CurrentDirectory, "Roms");
        }

        string nvramPath = Path.Combine(
            Environment.CurrentDirectory,
            "nvram",
            "starwars",
            "x2212");

        machine.LoadRoms(romDirectory);
        machine.LoadNovram(nvramPath);
        machine.Reset();

        using var display = new SdlVectorDisplay();
        var emulationTimer = Stopwatch.StartNew();
        double pendingCpuCycles = 0;

        while (true)
        {
            double elapsedSeconds = emulationTimer.Elapsed.TotalSeconds;
            emulationTimer.Restart();
            if (!display.ProcessEvents(machine, elapsedSeconds))
            {
                break;
            }

            pendingCpuCycles += elapsedSeconds * StarWarsHardware.CpuClockHz;

            // Do not let a debugger pause turn into several seconds of catch-up.
            pendingCpuCycles = Math.Min(pendingCpuCycles, StarWarsHardware.CpuClockHz / 4.0);
            int cycles = (int)pendingCpuCycles;
            if (cycles > 0)
            {
                machine.RunCpuCycles(cycles);
                pendingCpuCycles -= cycles;
            }

            display.QueueAudio(machine.Audio.DrainSamples());
            display.Render(machine.VectorGenerator.Vectors);
        }
    }
    finally
    {
        machine.SaveNovram();
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}
