using StarWars.Cpu;
using StarWars.Hardware;

namespace StarWars;

public enum StarWarsDifficulty
{
    Easy,
    Moderate,
    Hard,
    Hardest
}

public enum StarWarsCoinage : byte
{
    FreePlay = 0x00,
    OneCoinTwoCredits = 0x01,
    OneCoinOneCredit = 0x02,
    TwoCoinsOneCredit = 0x03
}

public sealed class StarWarsMachine
{
    public const int MainIrqPeriodCycles = 6144;

    private readonly bool[] _mainOutputLatch = new bool[8];
    private int _cyclesUntilMainIrq = MainIrqPeriodCycles;
    private byte _soundCommand;
    private bool _soundCommandPending;
    private byte _soundResponse;
    private bool _soundResponsePending;
    private int _selectedAdcChannel;

    public StarWarsMachine()
    {
        MainCpu = new M6809(MainMemory.AddressSpace, StarWarsHardware.CpuClockHz);
        SoundCpu = new M6809(SoundMemory.AddressSpace, StarWarsHardware.CpuClockHz);
        Mathbox = new StarWarsMathbox(MainMemory.AddressSpace);
        VectorGenerator = new AtariStarWarsAvg(MainMemory.AddressSpace);
        Audio = new StarWarsAudio();

        SoundRiot.IrqChanged = SoundCpu.SetIrqLine;
        SoundRiot.PortAOutputChanged = WriteSpeechControl;
        SoundRiot.PortBOutputChanged = (output, _) => Audio.Speech.SetDataBus(output);
        SoundRiot.PortAInput = 0x10; // Sound self-test inactive; /READY is supplied by the TMS5220.
        Mathbox.SetPrngReset(true);

        MapMainBoard();
        MapSoundBoard();
        UpdateSoundHandshake();
    }

    public StarWarsMainMemory MainMemory { get; } = new();
    public StarWarsSoundMemory SoundMemory { get; } = new();
    public Mos6532 SoundRiot { get; } = new();
    public StarWarsMathbox Mathbox { get; }
    public AtariStarWarsAvg VectorGenerator { get; }
    public StarWarsAudio Audio { get; }
    public M6809 MainCpu { get; }
    public M6809 SoundCpu { get; }

    public byte Input0 { get; set; } = 0xdf;
    public byte Input1 { get; set; } = 0x34;
    public byte DipSwitch0 { get; set; } = 0x98;
    public byte DipSwitch1 { get; set; } = 0x02;
    public byte AdcData { get; set; } = 0x80;
    public byte YokePitch { get; set; } = 0x80;
    public byte YokeYaw { get; set; } = 0x80;
    public bool MainIrqAsserted { get; private set; }
    public bool SoundCommandPending => _soundCommandPending;
    public bool SoundResponsePending => _soundResponsePending;

    public bool LeftCoinPressed
    {
        get => IsActiveLow(Input0, 0x02);
        set => Input0 = SetActiveLow(Input0, 0x02, value);
    }

    public bool RightCoinPressed
    {
        get => IsActiveLow(Input0, 0x01);
        set => Input0 = SetActiveLow(Input0, 0x01, value);
    }

    public bool AuxiliaryCoinPressed
    {
        get => IsActiveLow(Input0, 0x04);
        set => Input0 = SetActiveLow(Input0, 0x04, value);
    }

    public bool TiltPressed
    {
        get => IsActiveLow(Input0, 0x08);
        set => Input0 = SetActiveLow(Input0, 0x08, value);
    }

    public bool SelfTestEnabled
    {
        get => IsActiveLow(Input0, 0x10);
        set => Input0 = SetActiveLow(Input0, 0x10, value);
    }

    public bool LeftTriggerPressed
    {
        get => IsActiveLow(Input0, 0x80);
        set => Input0 = SetActiveLow(Input0, 0x80, value);
    }

    public bool RightTriggerPressed
    {
        get => IsActiveLow(Input1, 0x20);
        set => Input1 = SetActiveLow(Input1, 0x20, value);
    }

    public bool LeftThumbPressed
    {
        get => IsActiveLow(Input1, 0x10);
        set => Input1 = SetActiveLow(Input1, 0x10, value);
    }

    public bool RightThumbPressed
    {
        get => IsActiveLow(Input0, 0x40);
        set => Input0 = SetActiveLow(Input0, 0x40, value);
    }

    public bool DiagnosticStepPressed
    {
        get => IsActiveLow(Input1, 0x04);
        set => Input1 = SetActiveLow(Input1, 0x04, value);
    }

    public int StartingShields
    {
        get => 6 + (DipSwitch0 & 0x03);
        set
        {
            if (value is < 6 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Starting shields must be between 6 and 9.");
            }

            DipSwitch0 = (byte)((DipSwitch0 & ~0x03) | (value - 6));
        }
    }

    public StarWarsDifficulty Difficulty
    {
        get => (StarWarsDifficulty)((DipSwitch0 >> 2) & 0x03);
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            DipSwitch0 = (byte)((DipSwitch0 & ~0x0c) | ((int)value << 2));
        }
    }

    public int BonusShields
    {
        get => (DipSwitch0 >> 4) & 0x03;
        set
        {
            if (value is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Bonus shields must be between 0 and 3.");
            }

            DipSwitch0 = (byte)((DipSwitch0 & ~0x30) | (value << 4));
        }
    }

    public bool DemoSoundsEnabled
    {
        get => (DipSwitch0 & 0x40) == 0;
        set => DipSwitch0 = value ? (byte)(DipSwitch0 & ~0x40) : (byte)(DipSwitch0 | 0x40);
    }

    public bool FreezeModeEnabled
    {
        get => (DipSwitch0 & 0x80) == 0;
        set => DipSwitch0 = value ? (byte)(DipSwitch0 & ~0x80) : (byte)(DipSwitch0 | 0x80);
    }

    public StarWarsCoinage Coinage
    {
        get => (StarWarsCoinage)(DipSwitch1 & 0x03);
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            DipSwitch1 = (byte)((DipSwitch1 & ~0x03) | (byte)value);
        }
    }

    public Action<int, int, byte>? PokeyWrite { get; set; }

    public void LoadRoms(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        byte[] bankedRom = ReadRom(directory, "136021.214", 0x4000);
        MainMemory.LoadBankedRom(0, bankedRom.AsSpan(0x0000, 0x2000));
        MainMemory.LoadBankedRom(1, bankedRom.AsSpan(0x2000, 0x2000));
        MainMemory.LoadFixedRom(0x8000, ReadRom(directory, "136021.102", 0x2000));
        MainMemory.LoadFixedRom(0xa000, ReadRom(directory, "136021.203", 0x2000));
        MainMemory.LoadFixedRom(0xc000, ReadRom(directory, "136021.104", 0x2000));
        MainMemory.LoadFixedRom(0xe000, ReadRom(directory, "136021.206", 0x2000));
        MainMemory.LoadVectorRom(ReadRom(directory, "136021.105", 0x1000));

        byte[] soundRom0 = ReadRom(directory, "136021.107", 0x2000);
        byte[] soundRom1 = ReadRom(directory, "136021.208", 0x2000);
        SoundMemory.LoadRom(0x4000, soundRom0);
        SoundMemory.LoadRom(0xc000, soundRom0);
        SoundMemory.LoadRom(0x6000, soundRom1);
        SoundMemory.LoadRom(0xe000, soundRom1);

        Mathbox.LoadMicrocode(
            ReadRom(directory, "136021.110", 0x0400),
            ReadRom(directory, "136021.111", 0x0400),
            ReadRom(directory, "136021.112", 0x0400),
            ReadRom(directory, "136021.113", 0x0400));
        VectorGenerator.LoadStateProm(ReadRom(directory, "136021-105.1l", 0x0100));
    }

    public void LoadNovram(string path) => MainMemory.Novram.Load(path);

    public void SaveNovram() => MainMemory.Novram.Save();

    public void Reset()
    {
        Array.Clear(_mainOutputLatch);
        MainMemory.SelectedRomBank = 0;
        MainMemory.Novram.Recall();
        _selectedAdcChannel = 0;
        _cyclesUntilMainIrq = MainIrqPeriodCycles;
        SetMainIrq(false);
        Mathbox.Reset();
        VectorGenerator.Reset();
        Audio.Reset();

        _soundCommand = 0;
        _soundCommandPending = false;
        _soundResponse = 0;
        _soundResponsePending = false;

        SoundCpu.SetIrqLine(false);
        SoundCpu.SetFirqLine(false);
        SoundCpu.SetNmiLine(false);
        SoundRiot.Reset();
        SoundRiot.PortAInput = 0x10;
        UpdateSoundHandshake();
        UpdateSpeechBus();

        MainCpu.Reset();
        SoundCpu.Reset();
    }

    public int StepMainCpu()
    {
        int cycles = MainCpu.Step();
        Mathbox.TickCpuCycles(cycles);
        VectorGenerator.TickCpuCycles(cycles);
        _cyclesUntilMainIrq -= cycles;
        while (_cyclesUntilMainIrq <= 0)
        {
            _cyclesUntilMainIrq += MainIrqPeriodCycles;
            SetMainIrq(true);
        }

        return cycles;
    }

    public int StepSoundCpu()
    {
        int cycles = SoundCpu.Step();
        SoundRiot.Tick(cycles);
        Audio.TickCpuCycles(cycles);
        UpdateSpeechBus();
        return cycles;
    }

    public void RunCpuCycles(int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        long mainTarget = MainCpu.TotalCycles + cycles;
        long soundTarget = SoundCpu.TotalCycles + cycles;
        while (MainCpu.TotalCycles < mainTarget || SoundCpu.TotalCycles < soundTarget)
        {
            if (MainCpu.TotalCycles < mainTarget
                && (SoundCpu.TotalCycles >= soundTarget || MainCpu.TotalCycles <= SoundCpu.TotalCycles))
            {
                StepMainCpu();
            }
            else
            {
                StepSoundCpu();
            }
        }
    }

    private void MapMainBoard()
    {
        MainMemory.MapHardwareRead(0x4300, 0x431f, _ => Input0);
        MainMemory.MapHardwareRead(0x4320, 0x433f, _ => ReadInput1());
        MainMemory.MapHardwareRead(0x4340, 0x435f, _ => DipSwitch0);
        MainMemory.MapHardwareRead(0x4360, 0x437f, _ => DipSwitch1);
        MainMemory.MapHardwareRead(0x4380, 0x439f, _ => ReadAdc());
        MainMemory.MapHardwareRead(0x4400, 0x4400, _ => ReadSoundResponse());
        MainMemory.MapHardwareWrite(0x4400, 0x4400, (_, value) => WriteSoundCommand(value));
        MainMemory.MapHardwareRead(0x4401, 0x4401, _ => ReadSoundStatus());

        MainMemory.MapHardwareWrite(0x4600, 0x461f, (_, _) => VectorGenerator.Go());
        MainMemory.MapHardwareWrite(0x4620, 0x463f, (_, _) => VectorGenerator.ResetGenerator());

        MainMemory.MapHardwareRead(0x4700, 0x4707, address => Mathbox.ReadRegister(address - 0x4700));
        MainMemory.MapHardwareWrite(0x4700, 0x4707, (address, value) =>
            Mathbox.WriteRegister(address - 0x4700, value));

        MainMemory.MapHardwareWrite(0x4660, 0x467f, (_, _) => SetMainIrq(false));
        MainMemory.MapHardwareWrite(0x4680, 0x469f, WriteMainOutputLatch);
        MainMemory.MapHardwareWrite(0x46a0, 0x46bf, (_, _) =>
        {
            MainMemory.Novram.SetStoreLine(false);
            MainMemory.Novram.SetStoreLine(true);
            MainMemory.Novram.SetStoreLine(false);
        });
        MainMemory.MapHardwareWrite(0x46c0, 0x46c3, (address, _) =>
            _selectedAdcChannel = address & 0x03);
        MainMemory.MapHardwareWrite(0x46e0, 0x46e0, (_, _) => ResetSoundBoard());
    }

    private void MapSoundBoard()
    {
        SoundMemory.MapMainLatchWrite(WriteSoundResponse);
        SoundMemory.MapSoundLatchRead(ReadSoundCommand);
        SoundMemory.MapRiot(SoundRiot);
        SoundMemory.MapPokeyWrite(WritePokey);
    }

    private byte ReadSoundResponse()
    {
        _soundResponsePending = false;
        UpdateSoundHandshake();
        return _soundResponse;
    }

    private void WriteSoundCommand(byte value)
    {
        _soundCommand = value;
        _soundCommandPending = true;
        UpdateSoundHandshake();
    }

    private byte ReadSoundCommand()
    {
        _soundCommandPending = false;
        UpdateSoundHandshake();
        return _soundCommand;
    }

    private void WriteSoundResponse(byte value)
    {
        _soundResponse = value;
        _soundResponsePending = true;
        UpdateSoundHandshake();
    }

    private byte ReadSoundStatus() =>
        (byte)((_soundCommandPending ? 0x80 : 0) | (_soundResponsePending ? 0x40 : 0));

    private byte ReadInput1() =>
        (byte)((Input1 & 0x3f)
            | (VectorGenerator.Done ? 0x40 : 0)
            | (Mathbox.IsBusy ? 0x80 : 0));

    private byte ReadAdc() => _selectedAdcChannel switch
    {
        0 => YokePitch,
        1 => YokeYaw,
        2 => 0,
        _ => AdcData
    };

    private void UpdateSoundHandshake()
    {
        SoundRiot.SetPortAInputBit(6, _soundResponsePending);
        SoundRiot.SetPortAInputBit(7, _soundCommandPending);
    }

    private void WriteMainOutputLatch(ushort address, byte value)
    {
        int output = (address - 0x4680) & 0x07;
        bool high = (value & 0x80) != 0;
        _mainOutputLatch[output] = high;

        if (output == 4)
        {
            MainMemory.SelectedRomBank = high ? 1 : 0;
        }
        else if (output == 5)
        {
            // Q5 drives the active-low PRNGCLR signal. Power-on zero holds
            // the LFSR clear until the main program writes a one to Q5.
            Mathbox.SetPrngReset(!high);
        }
        else if (output == 7)
        {
            MainMemory.Novram.SetRecallLine(!high);
        }
    }

    private void WritePokey(ushort address, byte value)
    {
        int offset = address - 0x1800;
        int pokey = (offset >> 3) & 0x03;
        int register = (offset & 0x07) | ((offset & 0x20) >> 2);
        Audio.WritePokey(pokey, register, value);
        PokeyWrite?.Invoke(pokey, register, value);
    }

    private void WriteSpeechControl(byte output, byte direction)
    {
        // Inputs float high on the board, leaving both active-low TMS5220
        // strobes inactive until the RIOT programs those pins as outputs.
        bool writeQ = (direction & 0x01) == 0 || (output & 0x01) != 0;
        bool readQ = (direction & 0x02) == 0 || (output & 0x02) != 0;
        Audio.Speech.SetControlLines(writeQ, readQ);
        UpdateSpeechBus();
    }

    private void UpdateSpeechBus()
    {
        SoundRiot.SetPortAInputBit(2, Audio.Speech.ReadyQ);
        SoundRiot.PortBInput = Audio.Speech.DataBusOutput;
    }

    private void ResetSoundBoard()
    {
        _soundCommandPending = false;
        _soundResponsePending = false;
        UpdateSoundHandshake();

        SoundCpu.SetIrqLine(false);
        SoundCpu.SetFirqLine(false);
        SoundCpu.SetNmiLine(false);
        Audio.Reset();
        SoundRiot.Reset();
        SoundRiot.PortAInput = 0x10;
        UpdateSoundHandshake();
        UpdateSpeechBus();
        SoundCpu.Reset();
    }

    private void SetMainIrq(bool asserted)
    {
        MainIrqAsserted = asserted;
        MainCpu.SetIrqLine(asserted);
    }

    private static bool IsActiveLow(byte port, byte mask) => (port & mask) == 0;

    private static byte SetActiveLow(byte port, byte mask, bool active) =>
        active ? (byte)(port & ~mask) : (byte)(port | mask);

    private static byte[] ReadRom(string directory, string fileName, int expectedLength)
    {
        string path = Path.Combine(directory, fileName);
        byte[] data = File.ReadAllBytes(path);
        if (data.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"ROM '{fileName}' is {data.Length} bytes; expected {expectedLength} bytes.");
        }

        return data;
    }
}
