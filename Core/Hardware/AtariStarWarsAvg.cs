using System.Numerics;

namespace StarWars.Hardware;

public readonly record struct VectorSegment(
    int StartX,
    int StartY,
    int EndX,
    int EndY,
    byte Red,
    byte Green,
    byte Blue,
    byte Intensity);

public sealed class AtariStarWarsAvg(AddressSpace memory)
{
    public const int FieldWidth = 250;
    public const int FieldHeight = 280;

    private const int StatePromSize = 0x100;
    private const int MaximumStateClocks = 10_000_000;
    private const int MaximumVectors = 10_000;
    private const int OverdriveIntensityFloor = 192;
    private const int XCenter = (FieldWidth / 2) << 16;
    private const int YCenter = (FieldHeight / 2) << 16;

    private readonly AddressSpace _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly byte[] _stateProm = new byte[StatePromSize];
    private readonly ushort[] _stack = new ushort[4];
    private readonly List<VectorSegment> _vectors = new(MaximumVectors);
    private bool _statePromLoaded;
    private ushort _pc;
    private byte _stackPointer;
    private ushort _deltaX;
    private ushort _deltaY;
    private byte _stateLatch;
    private byte _scale;
    private byte _binaryScale;
    private byte _operation;
    private byte _intensity;
    private byte _color;
    private byte _intensityLatch;
    private byte _deltaYBit12;
    private ushort _timer;
    private int _xPosition;
    private int _yPosition;
    private int _beamX;
    private int _beamY;
    private bool _halted;
    private int _overdriveBeamCharge;

    public IReadOnlyList<VectorSegment> Vectors => _vectors;
    public bool Done { get; private set; } = true;
    public int RemainingMasterClocks { get; private set; }
    public int LastRunMasterClocks { get; private set; }
    public int LastOverdriveBeamClocks { get; private set; }
    public long RunCount { get; private set; }

    public void LoadStateProm(ReadOnlySpan<byte> prom)
    {
        if (prom.Length != StatePromSize)
        {
            throw new ArgumentException("The AVG state PROM must be exactly 256 bytes.", nameof(prom));
        }

        for (int address = 0; address < prom.Length; address++)
        {
            _stateProm[address] = (byte)(prom[address] & 0x0f);
        }

        _statePromLoaded = true;
    }

    public void Reset()
    {
        _pc = 0;
        _stackPointer = 0;
        _deltaX = 0;
        _deltaY = 0;
        Array.Clear(_stack);
        _operation = 0;
        _intensity = 0;
        _intensityLatch = 0;
        _deltaYBit12 = 0;
        _timer = 0;
        _xPosition = 0;
        _yPosition = 0;
        _beamX = 0;
        _beamY = 0;
        LastRunMasterClocks = 0;
        LastOverdriveBeamClocks = 0;
        RunCount = 0;
        _vectors.Clear();
        ResetGenerator();
    }

    public void ResetGenerator()
    {
        _stateLatch = 0;
        _binaryScale = 0;
        _scale = 0;
        _color = 0;
        _halted = true;
        Done = true;
        RemainingMasterClocks = 0;
    }

    public void Go()
    {
        if (!_statePromLoaded)
        {
            throw new InvalidOperationException("The AVG state PROM has not been loaded.");
        }

        _pc = 0;
        _stackPointer = 0;
        _halted = false;
        Done = false;
        _vectors.Clear();
        _beamX = _xPosition;
        _beamY = _yPosition;
        _overdriveBeamCharge = 0;

        int masterClocks = RunStateMachine();
        LastRunMasterClocks = masterClocks;
        LastOverdriveBeamClocks = _overdriveBeamCharge / (byte.MaxValue - OverdriveIntensityFloor);
        RemainingMasterClocks = masterClocks;
        RunCount++;
    }

    public void TickCpuCycles(int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        if (Done)
        {
            return;
        }

        RemainingMasterClocks -= cycles * 8;
        if (RemainingMasterClocks <= 0)
        {
            RemainingMasterClocks = 0;
            Done = true;
        }
    }

    private int RunStateMachine()
    {
        int masterClocks = 0;

        while (masterClocks < MaximumStateClocks)
        {
            int stateAddress = ((((_stateLatch >> 4) ^ 1) & 1) << 7)
                | (_operation << 4)
                | (_stateLatch & 0x0f);
            _stateLatch = (byte)((_stateLatch & 0x10) | _stateProm[stateAddress]);

            int vectorClocks = 0;
            if ((_stateLatch & 0x08) != 0)
            {
                byte data = _memory.ReadByte(_pc);
                vectorClocks = (_stateLatch & 0x07) switch
                {
                    0 => LatchDeltaYLow(data),
                    1 => LatchOperation(data),
                    2 => LatchDeltaXLow(data),
                    3 => LatchDeltaXHigh(data),
                    4 => NormalizeVector(),
                    5 => ApplyScaleOrStack(),
                    6 => ApplyColorScaleOrJump(),
                    7 => DrawVectorOrHalt(),
                    _ => 0
                };
            }

            masterClocks += vectorClocks;
            int haltClock = masterClocks;
            _stateLatch = (byte)((_halted ? 0x10 : 0) | (_stateLatch & 0x0f));
            masterClocks += 8;

            if (_halted)
            {
                return haltClock;
            }
        }

        throw new InvalidOperationException(
            $"AVG did not halt within {MaximumStateClocks} master clocks (PC=${_pc:X4}, OP={_operation}).");
    }

    private int LatchDeltaYLow(byte data)
    {
        _deltaY = (ushort)((_deltaY & 0x1f00) | data);
        _pc++;
        return 0;
    }

    private int LatchOperation(byte data)
    {
        _deltaYBit12 = (byte)((data >> 4) & 1);
        _operation = (byte)(data >> 5);
        _intensityLatch = 0;
        _deltaY = (ushort)((_deltaYBit12 << 12) | ((data & 0x0f) << 8));
        _deltaX = 0;
        _pc++;
        return 0;
    }

    private int LatchDeltaXLow(byte data)
    {
        _deltaX = (ushort)((_deltaX & 0x1f00) | data);
        _pc++;
        return 0;
    }

    private int LatchDeltaXHigh(byte data)
    {
        _intensityLatch = (byte)(data >> 4);
        _deltaX = (ushort)(((_intensityLatch & 1) << 12) | ((data & 0x0f) << 8) | (_deltaX & 0xff));
        _pc++;
        return 0;
    }

    private int NormalizeVector()
    {
        if (OperationBit(0))
        {
            _stack[_stackPointer & 3] = _pc;
            return 0;
        }

        int shifts = 0;
        while (((_deltaY ^ (_deltaY << 1)) & 0x1000) == 0
            && ((_deltaX ^ (_deltaX << 1)) & 0x1000) == 0
            && shifts++ < 16)
        {
            _deltaY = (ushort)((_deltaY & 0x1000) | ((_deltaY << 1) & 0x1fff));
            _deltaX = (ushort)((_deltaX & 0x1000) | ((_deltaX << 1) & 0x1fff));
            _timer >>= 1;
            _timer = (ushort)(_timer | 0x4000 | (OperationBit(1) ? 0x80 : 0));
        }

        if (OperationBit(1))
        {
            _timer &= 0x00ff;
        }

        return 0;
    }

    private int ApplyScaleOrStack()
    {
        if (!OperationBit(2))
        {
            for (int shift = _binaryScale; shift > 0; shift--)
            {
                _timer >>= 1;
                _timer = (ushort)(_timer | 0x4000 | (OperationBit(1) ? 0x80 : 0));
            }

            if (OperationBit(1))
            {
                _timer &= 0x00ff;
            }
        }
        else
        {
            _stackPointer = (byte)((_stackPointer + (OperationBit(1) ? -1 : 1)) & 0x0f);
        }

        return 0;
    }

    private int ApplyColorScaleOrJump()
    {
        if (OperationBit(2))
        {
            _pc = OperationBit(0) ? (ushort)(_deltaY << 1) : _stack[_stackPointer & 3];
        }
        else if (_deltaYBit12 != 0)
        {
            _scale = (byte)_deltaY;
            _binaryScale = (byte)((_deltaY >> 8) & 7);
        }
        else
        {
            _intensity = (byte)_deltaY;
            _color = (byte)((_deltaY >> 8) & 0x0f);
        }

        return 0;
    }

    private int DrawVectorOrHalt()
    {
        _halted = OperationBit(0);
        int vectorClocks = 0;

        if (!OperationBit(0) && !OperationBit(2))
        {
            vectorClocks = OperationBit(1) ? 0x100 - (_timer & 0xff) : 0x8000 - _timer;
            _timer = 0;

            int deltaX = unchecked(((((int)(_deltaX >> 3) ^ 0x200) - 0x200)
                * vectorClocks * (_scale ^ 0xff)) >> 4);
            int deltaY = unchecked(((((int)(_deltaY >> 3) ^ 0x200) - 0x200)
                * vectorClocks * (_scale ^ 0xff)) >> 4);
            _xPosition = unchecked(_xPosition + deltaX);
            _yPosition = unchecked(_yPosition - deltaY);

            byte intensity = CalculateStarWarsIntensity();
            if (intensity > OverdriveIntensityFloor)
            {
                int guns = BitOperations.PopCount((uint)_color & 7u);
                _overdriveBeamCharge += vectorClocks * guns * (intensity - OverdriveIntensityFloor);
            }

            AddPoint(_xPosition, _yPosition, intensity);
        }
        else if (OperationBit(2))
        {
            vectorClocks = 0x8000 - _timer;
            _timer = 0;
            _xPosition = XCenter;
            _yPosition = YCenter;
            AddPoint(_xPosition, _yPosition, 0);
        }

        return vectorClocks;
    }

    private byte CalculateStarWarsIntensity()
    {
        const double fullScale = 1.0 + 0.5 + (12_000.0 / 47_000.0);
        double dac = ((_intensityLatch & 0x08) != 0 ? 1.0 : 0.0)
            + ((_intensityLatch & 0x04) != 0 ? 0.5 : 0.0)
            + ((_intensityLatch & 0x02) != 0 ? 12_000.0 / 47_000.0 : 0.0);
        return (byte)Math.Clamp((int)((dac / fullScale) * _intensity + 0.5), 0, 255);
    }

    private void AddPoint(int x, int y, byte intensity)
    {
        if (intensity != 0 && _vectors.Count < MaximumVectors)
        {
            byte red = (_color & 0x04) != 0 ? (byte)255 : (byte)0;
            byte green = (_color & 0x02) != 0 ? (byte)255 : (byte)0;
            byte blue = (_color & 0x01) != 0 ? (byte)255 : (byte)0;
            _vectors.Add(new VectorSegment(_beamX, _beamY, x, y, red, green, blue, intensity));
        }

        _beamX = x;
        _beamY = y;
    }

    private bool OperationBit(int bit) => (_operation & (1 << bit)) != 0;
}
