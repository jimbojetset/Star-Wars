namespace StarWars.Hardware;

public sealed class StarWarsAudio
{
    public const int SampleRate = 48_000;

    private readonly AtariPokey[] _pokeys =
    [
        new(),
        new(),
        new(),
        new()
    ];
    private readonly List<float> _pendingSamples = new(SampleRate / 10);
    private long _sampleClockAccumulator;

    public StarWarsAudio()
    {
        Speech = new Tms5220();
    }

    public IReadOnlyList<AtariPokey> Pokeys => _pokeys;
    public Tms5220 Speech { get; }
    public int PendingSampleCount => _pendingSamples.Count;

    public void Reset()
    {
        ResetChips();
        _pendingSamples.Clear();
        _sampleClockAccumulator = 0;
    }

    internal void ResetChips()
    {
        foreach (AtariPokey pokey in _pokeys)
        {
            pokey.Reset();
        }

        Speech.Reset();
    }

    public void WritePokey(int pokey, int register, byte value)
    {
        if ((uint)pokey >= _pokeys.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(pokey));
        }

        _pokeys[pokey].Write(register, value);
    }

    public void TickCpuCycles(int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        Speech.TickCpuCycles(cycles);
        _sampleClockAccumulator += (long)cycles * SampleRate;
        while (_sampleClockAccumulator >= StarWarsHardware.CpuClockHz)
        {
            _sampleClockAccumulator -= StarWarsHardware.CpuClockHz;

            float sample = Speech.GenerateSample(SampleRate) * 0.50f;
            foreach (AtariPokey pokey in _pokeys)
            {
                sample += pokey.GenerateSample(SampleRate) * 0.20f;
            }

            _pendingSamples.Add(MathF.Tanh(sample));
        }
    }

    public float[] DrainSamples()
    {
        if (_pendingSamples.Count == 0)
        {
            return [];
        }

        float[] samples = _pendingSamples.ToArray();
        _pendingSamples.Clear();
        return samples;
    }
}
