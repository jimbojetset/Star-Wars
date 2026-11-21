namespace StarWars.Hardware;

[Flags]
public enum MatrixStrobe : byte
{
    LoadAccumulator = 0x01,
    StoreAccumulator = 0x02,
    Halt = 0x04,
    IncrementBlockIndex = 0x08,
    ClearAccumulator = 0x10,
    LoadCAndMultiply = 0x20,
    LoadB = 0x40,
    LoadA = 0x80
}

public readonly record struct MatrixMicroInstruction(
    MatrixStrobe Strobes,
    bool DirectAddress,
    byte Address);

public sealed class StarWarsMathbox(AddressSpace memory)
{
    private const ushort MathRamBase = 0x5000;
    private const int PromSize = 0x400;
    private const int MaximumMicroInstructions = 100_000;

    private readonly AddressSpace _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly MatrixMicroInstruction[] _microcode = new MatrixMicroInstruction[PromSize];
    private bool _microcodeLoaded;
    private uint _prng;
    private bool _prngReset;
    private ushort _dividend;
    private ushort _divisor;
    private ushort _dividendShift;
    private ushort _quotientShift;

    public ushort ProgramAddress { get; private set; }
    public ushort BlockIndex { get; private set; }
    public short A { get; private set; }
    public short B { get; private set; }
    public short C { get; private set; }
    public int Accumulator { get; private set; }
    public bool IsBusy { get; private set; }
    public int RemainingMasterClocks { get; private set; }
    public int LastRunMasterClocks { get; private set; }
    public long RunCount { get; private set; }
    public IReadOnlyList<MatrixMicroInstruction> Microcode => _microcode;

    public void LoadMicrocode(
        ReadOnlySpan<byte> prom110,
        ReadOnlySpan<byte> prom111,
        ReadOnlySpan<byte> prom112,
        ReadOnlySpan<byte> prom113)
    {
        ValidateProm(prom110, nameof(prom110));
        ValidateProm(prom111, nameof(prom111));
        ValidateProm(prom112, nameof(prom112));
        ValidateProm(prom113, nameof(prom113));

        for (int address = 0; address < PromSize; address++)
        {
            ushort instruction = (ushort)(
                ((prom110[address] & 0x0f) << 12) |
                ((prom111[address] & 0x0f) << 8) |
                ((prom112[address] & 0x0f) << 4) |
                (prom113[address] & 0x0f));

            _microcode[address] = new MatrixMicroInstruction(
                (MatrixStrobe)(instruction >> 8),
                (instruction & 0x0080) != 0,
                (byte)(instruction & 0x007f));
        }

        _microcodeLoaded = true;
    }

    public void Reset()
    {
        ProgramAddress = 0;
        BlockIndex = 0;
        A = 0;
        B = 0;
        C = 0;
        Accumulator = 0;
        IsBusy = false;
        RemainingMasterClocks = 0;
        LastRunMasterClocks = 0;
        RunCount = 0;
        _dividend = 0;
        _divisor = 0;
        _dividendShift = 0;
        _quotientShift = 0;
        _prng = 0;
        _prngReset = true;
    }

    public byte ReadRegister(int offset) => (offset & 0x07) switch
    {
        0 => (byte)(_quotientShift >> 8),
        1 => (byte)_quotientShift,
        3 => (byte)(_prng >> 8),
        _ => 0xff
    };

    public void WriteRegister(int offset, byte value)
    {
        switch (offset & 0x07)
        {
            case 0:
                ProgramAddress = (ushort)(value << 2);
                RunMatrixProcessor();
                break;
            case 1:
                BlockIndex = (ushort)((BlockIndex & 0x00ff) | ((value & 0x01) << 8));
                break;
            case 2:
                BlockIndex = (ushort)((BlockIndex & 0x0100) | value);
                break;
            case 4:
                _divisor = (ushort)((_divisor & 0x00ff) | (value << 8));
                _dividendShift = _dividend;
                _quotientShift = 0;
                break;
            case 5:
                _divisor = (ushort)((_divisor & 0xff00) | value);
                Divide();
                break;
            case 6:
                _dividend = (ushort)((_dividend & 0x00ff) | (value << 8));
                break;
            case 7:
                _dividend = (ushort)((_dividend & 0xff00) | value);
                break;
        }
    }

    public void TickCpuCycles(int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        if (IsBusy)
        {
            RemainingMasterClocks -= cycles * 8;
            if (RemainingMasterClocks <= 0)
            {
                RemainingMasterClocks = 0;
                IsBusy = false;
            }
        }

        if (_prngReset)
        {
            _prng = 0;
            return;
        }

        // The PRNG clock is master/4 while the 6809 E clock is master/8.
        for (int clock = 0; clock < cycles * 2; clock++)
        {
            uint feedback = ~((_prng >> 4) ^ (_prng >> 22)) & 1;
            _prng = ((_prng << 1) | feedback) & 0x7fffff;
        }
    }

    public void SetPrngReset(bool asserted)
    {
        _prngReset = asserted;
        if (asserted)
        {
            _prng = 0;
        }
    }

    private void RunMatrixProcessor()
    {
        if (!_microcodeLoaded)
        {
            throw new InvalidOperationException("Matrix Processor PROMs have not been loaded.");
        }

        int masterClocks = 0;
        bool halted = false;

        for (int instructionCount = 0; instructionCount < MaximumMicroInstructions; instructionCount++)
        {
            MatrixMicroInstruction instruction = _microcode[ProgramAddress];
            masterClocks += 5;

            int wordAddress = instruction.DirectAddress
                ? instruction.Address
                : (instruction.Address & 0x03) | ((BlockIndex & 0x01ff) << 2);
            ushort ramWord = ReadMathRamWord(wordAddress);
            MatrixStrobe strobes = instruction.Strobes;

            if ((strobes & MatrixStrobe.ClearAccumulator) != 0)
            {
                Accumulator = 0;
            }

            if ((strobes & MatrixStrobe.LoadAccumulator) != 0)
            {
                Accumulator = unchecked((int)((uint)ramWord << 16));
            }

            if ((strobes & MatrixStrobe.StoreAccumulator) != 0)
            {
                WriteMathRamWord(wordAddress, (ushort)((uint)Accumulator >> 16));
            }

            if ((strobes & MatrixStrobe.Halt) != 0)
            {
                halted = true;
            }

            if ((strobes & MatrixStrobe.IncrementBlockIndex) != 0)
            {
                BlockIndex = (ushort)((BlockIndex + 1) & 0x01ff);
            }

            if ((strobes & MatrixStrobe.LoadCAndMultiply) != 0)
            {
                C = unchecked((short)ramWord);
                int difference = A - B;
                Accumulator = unchecked(Accumulator + difference * C * 4);
                A = A < 0 ? (short)-1 : (short)0;
                B = B < 0 ? (short)-1 : (short)0;
                masterClocks += 33;
            }

            if ((strobes & MatrixStrobe.LoadB) != 0)
            {
                B = unchecked((short)ramWord);
            }

            if ((strobes & MatrixStrobe.LoadA) != 0)
            {
                A = unchecked((short)ramWord);
            }

            int nextAddress = ProgramAddress + 1;
            ProgramAddress = (ushort)((ProgramAddress & 0x0300) | (nextAddress & 0x00ff));

            if (halted)
            {
                LastRunMasterClocks = masterClocks;
                RemainingMasterClocks = masterClocks;
                IsBusy = true;
                RunCount++;
                return;
            }
        }

        throw new InvalidOperationException(
            $"Matrix Processor did not halt within {MaximumMicroInstructions} microinstructions.");
    }

    private ushort ReadMathRamWord(int wordAddress)
    {
        ushort byteAddress = (ushort)(MathRamBase + (wordAddress << 1));
        return _memory.ReadWord(byteAddress);
    }

    private void WriteMathRamWord(int wordAddress, ushort value)
    {
        ushort byteAddress = (ushort)(MathRamBase + (wordAddress << 1));
        _memory.WriteWord(byteAddress, value);
    }

    private void Divide()
    {
        for (int bit = 1; bit < 16; bit++)
        {
            _quotientShift = unchecked((ushort)(_quotientShift << 1));
            int difference = _dividendShift + (_divisor ^ 0xffff) + 1;
            if ((difference & 0x10000) != 0)
            {
                _quotientShift |= 1;
                _dividendShift = unchecked((ushort)(difference << 1));
            }
            else
            {
                _dividendShift = unchecked((ushort)(_dividendShift << 1));
            }
        }
    }

    private static void ValidateProm(ReadOnlySpan<byte> prom, string parameterName)
    {
        if (prom.Length != PromSize)
        {
            throw new ArgumentException("Each Matrix Processor PROM must be exactly 1 KiB.", parameterName);
        }
    }
}
