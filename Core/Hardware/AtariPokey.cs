namespace StarWars.Hardware;

public sealed class AtariPokey
{
    private const int ChannelCount = 4;
    private const byte VolumeOnly = 0x10;
    private const byte PureTone = 0x20;
    private const byte Poly4Noise = 0x40;
    private const byte BypassPoly5 = 0x80;

    private readonly byte[] _registers = new byte[16];
    private readonly double[] _timerPhase = new double[ChannelCount];
    private readonly bool[] _output = new bool[ChannelCount];
    private uint _poly4;
    private uint _poly5;
    private uint _poly9;
    private uint _poly17;
    private float _dcLevel;

    public AtariPokey(int clockHz = StarWarsHardware.CpuClockHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);

        ClockHz = clockHz;
        Reset();
    }

    public int ClockHz { get; }
    public byte AudioControl => _registers[8];
    public IReadOnlyList<byte> Registers => _registers;

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_timerPhase);
        Array.Clear(_output);
        ResetPolynomials();
        _dcLevel = 0;
    }

    public void Write(int register, byte value)
    {
        if ((uint)register >= _registers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(register));
        }

        _registers[register] = value;
        switch (register)
        {
            case 9: // STIMER reloads all four audio dividers.
                Array.Clear(_timerPhase);
                Array.Clear(_output);
                break;
            case 15: // With both SKCTL reset bits low, the polynomial counters are held reset.
                if ((value & 0x03) == 0)
                {
                    ResetPolynomials();
                }
                break;
        }
    }

    public float GenerateSample(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        byte audctl = _registers[8];
        bool join12 = (audctl & 0x10) != 0;
        bool join34 = (audctl & 0x08) != 0;

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            if ((channel == 0 && join12) || (channel == 2 && join34))
            {
                continue;
            }

            int period = TimerPeriod(channel, audctl, join12, join34);
            _timerPhase[channel] += ClockHz / (double)(sampleRate * period);
            int timerEvents = (int)_timerPhase[channel];
            _timerPhase[channel] -= timerEvents;

            for (int timerEvent = 0; timerEvent < timerEvents; timerEvent++)
            {
                ClockChannel(channel, audctl);
            }
        }

        float raw = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            byte control = _registers[channel * 2 + 1];
            int volume = control & 0x0f;
            if ((control & VolumeOnly) != 0 || _output[channel])
            {
                raw += volume;
            }
        }

        raw /= 60f;
        _dcLevel += (raw - _dcLevel) * 0.001f;
        return (raw - _dcLevel) * 0.8f;
    }

    private int TimerPeriod(int channel, byte audctl, bool join12, bool join34)
    {
        int baseDivider = (audctl & 0x01) != 0 ? 114 : 28;
        if ((channel == 0 || channel == 1) && (audctl & 0x40) != 0)
        {
            baseDivider = 1;
        }
        else if ((channel == 2 || channel == 3) && (audctl & 0x20) != 0)
        {
            baseDivider = 1;
        }

        if (channel == 1 && join12)
        {
            return Math.Max(1, ((_registers[2] << 8) | _registers[0]) + 1) * baseDivider;
        }

        if (channel == 3 && join34)
        {
            return Math.Max(1, ((_registers[6] << 8) | _registers[4]) + 1) * baseDivider;
        }

        return Math.Max(1, (_registers[channel * 2] + 1) * baseDivider);
    }

    private void ClockChannel(int channel, byte audctl)
    {
        byte control = _registers[channel * 2 + 1];
        if ((control & VolumeOnly) == 0)
        {
            bool poly5AllowsChange = (control & BypassPoly5) != 0 || StepPoly5();
            if (poly5AllowsChange)
            {
                if ((control & PureTone) != 0)
                {
                    _output[channel] = !_output[channel];
                }
                else if ((control & Poly4Noise) != 0)
                {
                    _output[channel] = StepPoly4();
                }
                else
                {
                    _output[channel] = (audctl & 0x80) != 0 ? StepPoly9() : StepPoly17();
                }
            }
        }

        // The high-pass switches discharge channel 1 from channel 3's timer,
        // and channel 2 from channel 4's timer.
        if (channel == 2 && (audctl & 0x04) != 0)
        {
            _output[0] = false;
        }
        else if (channel == 3 && (audctl & 0x02) != 0)
        {
            _output[1] = false;
        }
    }

    private void ResetPolynomials()
    {
        _poly4 = 0x0f;
        _poly5 = 0x1f;
        _poly9 = 0x1ff;
        _poly17 = 0x1ffff;
    }

    private bool StepPoly4() => StepPolynomial(ref _poly4, 4, 1);
    private bool StepPoly5() => StepPolynomial(ref _poly5, 5, 2);
    private bool StepPoly9() => StepPolynomial(ref _poly9, 9, 4);
    private bool StepPoly17() => StepPolynomial(ref _poly17, 17, 5);

    private static bool StepPolynomial(ref uint shiftRegister, int bits, int tap)
    {
        uint feedback = (shiftRegister ^ (shiftRegister >> tap)) & 1;
        shiftRegister = (shiftRegister >> 1) | (feedback << (bits - 1));
        return (shiftRegister & 1) != 0;
    }
}
