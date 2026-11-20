namespace StarWars.Hardware;

public sealed class Tms5220
{
    private const int FifoSize = 16;
    private const int SamplesPerFrame = 200;
    private const int WriteCycleCpuClocks = 36;

    private static readonly int[] EnergyTable =
        [0, 1, 2, 3, 4, 6, 8, 11, 16, 23, 33, 47, 63, 85, 114, 0];

    private static readonly int[] PitchTable =
    [
        0, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 44, 46, 48,
        50, 52, 53, 56, 58, 60, 62, 65, 68, 70, 72, 76, 78, 80, 84, 86,
        91, 94, 98, 101, 105, 109, 114, 118, 122, 127, 132, 137, 142, 148, 153, 159
    ];

    private static readonly int[][] KTables =
    [
        [-501, -498, -497, -495, -493, -491, -488, -482, -478, -474, -469, -464, -459, -452, -445, -437,
         -412, -380, -339, -288, -227, -158, -81, -1, 80, 157, 226, 287, 337, 379, 411, 436],
        [-328, -303, -274, -244, -211, -175, -138, -99, -59, -18, 24, 64, 105, 143, 180, 215,
         248, 278, 306, 331, 354, 374, 392, 408, 422, 435, 445, 455, 463, 470, 476, 506],
        [-441, -387, -333, -279, -225, -171, -117, -63, -9, 45, 98, 152, 206, 260, 314, 368],
        [-328, -273, -217, -161, -106, -50, 5, 61, 116, 172, 228, 283, 339, 394, 450, 506],
        [-328, -282, -235, -189, -142, -96, -50, -3, 43, 90, 136, 182, 229, 275, 322, 368],
        [-256, -212, -168, -123, -79, -35, 10, 54, 98, 143, 187, 232, 276, 320, 365, 409],
        [-308, -260, -212, -164, -117, -69, -21, 27, 75, 122, 170, 218, 266, 314, 361, 409],
        [-256, -161, -66, 29, 124, 219, 314, 409],
        [-256, -176, -96, -15, 65, 146, 226, 307],
        [-205, -132, -59, 14, 87, 160, 234, 307]
    ];

    private static readonly sbyte[] ChirpTable =
    [
        0, 3, 15, 40, 76, 108, 113, 80, 37, 38, 76, 68, 26, 50, 59, 19,
        55, 26, 37, 31, 29, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0
    ];

    private static readonly int[] KBitWidths = [5, 5, 4, 4, 4, 4, 4, 3, 3, 3];
    private static readonly int[] InterpolationShifts = [3, 3, 3, 2, 2, 1, 1, 0];

    private readonly byte[] _fifo = new byte[FifoSize];
    private readonly int[] _currentK = new int[10];
    private readonly int[] _targetK = new int[10];
    private readonly int[] _latticeTop = new int[11];
    private readonly int[] _latticeBottom = new int[10];
    private int _fifoHead;
    private int _fifoTail;
    private int _fifoCount;
    private int _fifoBit;
    private int _busyCpuCycles;
    private int _frameSample;
    private int _currentEnergy;
    private int _targetEnergy;
    private int _previousEnergy;
    private int _currentPitch;
    private int _targetPitch;
    private int _pitchCounter;
    private uint _noise;
    private byte _dataBus;
    private byte _pendingWriteData;
    private byte _readLatch;
    private bool _writeQ;
    private bool _readQ;
    private bool _pendingWrite;
    private bool _pendingRead;
    private bool _externalMode;
    private bool _stopAfterFrame;
    private double _nativeSamplePhase;
    private float _nativeSample;

    public Tms5220(int clockHz = StarWarsHardware.MasterClockHz / 18)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);

        ClockHz = clockHz;
        NativeSampleRate = clockHz / 80;
        Reset();
    }

    public int ClockHz { get; }
    public int NativeSampleRate { get; }
    public bool Talking { get; private set; }
    public bool ReadyQ => _busyCpuCycles > 0 || (_externalMode && _fifoCount >= FifoSize);
    public byte DataBusOutput => _readLatch;
    public int FifoCount => _fifoCount;

    public byte Status => (byte)((Talking ? 0x80 : 0)
        | (_fifoCount <= 8 ? 0x40 : 0)
        | (_fifoCount == 0 ? 0x20 : 0));

    public void Reset()
    {
        Array.Clear(_fifo);
        Array.Clear(_currentK);
        Array.Clear(_targetK);
        Array.Clear(_latticeTop);
        Array.Clear(_latticeBottom);
        _fifoHead = 0;
        _fifoTail = 0;
        _fifoCount = 0;
        _fifoBit = 0;
        _busyCpuCycles = 0;
        _frameSample = SamplesPerFrame;
        _currentEnergy = 0;
        _targetEnergy = 0;
        _previousEnergy = 0;
        _currentPitch = 0;
        _targetPitch = 0;
        _pitchCounter = 0;
        _noise = 0x1fff;
        _dataBus = 0;
        _pendingWriteData = 0;
        _readLatch = 0xff;
        _writeQ = true;
        _readQ = true;
        _pendingWrite = false;
        _pendingRead = false;
        _externalMode = false;
        _stopAfterFrame = false;
        _nativeSamplePhase = 0;
        _nativeSample = 0;
        Talking = false;
    }

    public void SetDataBus(byte value) => _dataBus = value;

    public void SetControlLines(bool writeQ, bool readQ)
    {
        bool writeFalling = _writeQ && !writeQ;
        bool readFalling = _readQ && !readQ;
        _writeQ = writeQ;
        _readQ = readQ;

        if (!writeQ && !readQ)
        {
            return;
        }

        if (writeFalling)
        {
            _pendingWriteData = _dataBus;
            _pendingWrite = true;
            _busyCpuCycles = WriteCycleCpuClocks;
        }
        else if (readFalling)
        {
            _pendingRead = true;
            _busyCpuCycles = WriteCycleCpuClocks;
        }
    }

    public void TickCpuCycles(int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        if (_busyCpuCycles == 0)
        {
            return;
        }

        _busyCpuCycles = Math.Max(0, _busyCpuCycles - cycles);
        if (_busyCpuCycles != 0)
        {
            return;
        }

        if (_pendingWrite)
        {
            _pendingWrite = false;
            Write(_pendingWriteData);
        }

        if (_pendingRead)
        {
            _pendingRead = false;
            _readLatch = Status;
        }
    }

    public float GenerateSample(int outputSampleRate)
    {
        _nativeSamplePhase += NativeSampleRate / (double)outputSampleRate;
        while (_nativeSamplePhase >= 1)
        {
            _nativeSamplePhase--;
            _nativeSample = GenerateNativeSample();
        }

        return _nativeSample;
    }

    private void Write(byte value)
    {
        if (_externalMode)
        {
            if (_fifoCount < FifoSize)
            {
                _fifo[_fifoTail] = value;
                _fifoTail = (_fifoTail + 1) % FifoSize;
                _fifoCount++;
                if (!Talking && _fifoCount >= 9)
                {
                    Talking = true;
                    _frameSample = SamplesPerFrame;
                }
            }

            return;
        }

        switch (value & 0x70)
        {
            case 0x60: // SPEAK EXTERNAL
                ClearFifo();
                _externalMode = true;
                _stopAfterFrame = false;
                break;
            case 0x70: // RESET
                Reset();
                break;
        }
    }

    private float GenerateNativeSample()
    {
        if (!Talking)
        {
            return 0;
        }

        if (_frameSample >= SamplesPerFrame)
        {
            if (_stopAfterFrame)
            {
                StopSpeaking();
                return 0;
            }

            if (!LoadFrame())
            {
                StopSpeaking();
                return 0;
            }

            _frameSample = 0;
        }

        if ((_frameSample % 25) == 0)
        {
            InterpolateParameters(_frameSample / 25);
        }

        int excitation;
        if (_currentPitch == 0)
        {
            excitation = (_noise & 1) != 0 ? -64 : 64;
        }
        else
        {
            excitation = _pitchCounter < ChirpTable.Length ? ChirpTable[_pitchCounter] : 0;
        }

        for (int clock = 0; clock < 20; clock++)
        {
            uint feedback = ((_noise >> 12) ^ (_noise >> 3) ^ (_noise >> 2) ^ _noise) & 1;
            _noise = ((_noise << 1) | feedback) & 0x1fff;
        }

        int sample = LatticeFilter(excitation);
        _pitchCounter++;
        if (_currentPitch == 0 || _pitchCounter >= _currentPitch)
        {
            _pitchCounter = 0;
        }

        _frameSample++;
        sample = WrapSigned(sample, 14);
        return Math.Clamp(sample / 16384f, -1f, 1f);
    }

    private bool LoadFrame()
    {
        if (!TryReadBits(4, out int energyIndex))
        {
            return false;
        }

        if (energyIndex == 15)
        {
            _targetEnergy = 0;
            _stopAfterFrame = true;
            return true;
        }

        if (energyIndex == 0)
        {
            _targetEnergy = 0;
            _targetPitch = 0;
            Array.Clear(_targetK);
            return true;
        }

        if (!TryReadBits(1, out int repeat) || !TryReadBits(6, out int pitchIndex))
        {
            return false;
        }

        _targetEnergy = EnergyTable[energyIndex];
        _targetPitch = PitchTable[pitchIndex];
        if (repeat != 0)
        {
            return true;
        }

        int coefficientCount = pitchIndex == 0 ? 4 : 10;
        for (int coefficient = 0; coefficient < coefficientCount; coefficient++)
        {
            if (!TryReadBits(KBitWidths[coefficient], out int index))
            {
                return false;
            }

            _targetK[coefficient] = KTables[coefficient][index];
        }

        if (coefficientCount == 4)
        {
            Array.Clear(_targetK, 4, 6);
        }

        return true;
    }

    private void InterpolateParameters(int period)
    {
        int shift = InterpolationShifts[period];
        if (shift == 0)
        {
            _currentEnergy = _targetEnergy;
            _currentPitch = _targetPitch;
            _targetK.CopyTo(_currentK, 0);
            return;
        }

        _currentEnergy += (_targetEnergy - _currentEnergy) >> shift;
        _currentPitch += (_targetPitch - _currentPitch) >> shift;
        for (int coefficient = 0; coefficient < _currentK.Length; coefficient++)
        {
            _currentK[coefficient] += (_targetK[coefficient] - _currentK[coefficient]) >> shift;
        }
    }

    private int LatticeFilter(int excitation)
    {
        _latticeTop[10] = MatrixMultiply(_previousEnergy, excitation << 6);
        for (int stage = 9; stage >= 0; stage--)
        {
            _latticeTop[stage] = _latticeTop[stage + 1] - MatrixMultiply(_currentK[stage], _latticeBottom[stage]);
        }

        for (int stage = 9; stage >= 1; stage--)
        {
            _latticeBottom[stage] = _latticeBottom[stage - 1]
                + MatrixMultiply(_currentK[stage - 1], _latticeTop[stage - 1]);
        }

        _latticeBottom[0] = _latticeTop[0];
        _previousEnergy = _currentEnergy;
        return _latticeTop[0];
    }

    private static int MatrixMultiply(int coefficient, int value)
    {
        coefficient = WrapSigned(coefficient, 10);
        value = WrapSigned(value, 15);
        return coefficient * value >> 9;
    }

    private bool TryReadBits(int count, out int value)
    {
        if (_fifoCount * 8 - _fifoBit < count)
        {
            value = 0;
            return false;
        }

        value = 0;
        for (int bit = 0; bit < count; bit++)
        {
            value = (value << 1) | ((_fifo[_fifoHead] >> _fifoBit) & 1);
            _fifoBit++;
            if (_fifoBit == 8)
            {
                _fifoBit = 0;
                _fifoHead = (_fifoHead + 1) % FifoSize;
                _fifoCount--;
            }
        }

        return true;
    }

    private void StopSpeaking()
    {
        Talking = false;
        _externalMode = false;
        _stopAfterFrame = false;
        _currentEnergy = 0;
        _targetEnergy = 0;
        _previousEnergy = 0;
        _nativeSample = 0;
    }

    private void ClearFifo()
    {
        Array.Clear(_fifo);
        _fifoHead = 0;
        _fifoTail = 0;
        _fifoCount = 0;
        _fifoBit = 0;
        Talking = false;
    }

    private static int WrapSigned(int value, int bits)
    {
        int mask = (1 << bits) - 1;
        value &= mask;
        int sign = 1 << (bits - 1);
        return (value ^ sign) - sign;
    }
}
