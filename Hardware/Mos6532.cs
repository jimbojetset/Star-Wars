namespace StarWars.Hardware;

public sealed class Mos6532
{
    private readonly byte[] _ram = new byte[0x80];
    private byte _portAOutput;
    private byte _portADirection;
    private byte _portAInput;
    private byte _portBOutput;
    private byte _portBDirection;
    private byte _portBInput;
    private byte _timer;
    private int _timerDivider = 1;
    private int _dividerCycles = 1;
    private bool _timerUnderflowed;
    private bool _timerInterruptEnabled;
    private bool _timerInterruptFlag;
    private bool _pa7InterruptEnabled;
    private bool _pa7PositiveEdge;
    private bool _pa7InterruptFlag;
    private bool _irqAsserted;

    public Action<byte, byte>? PortAOutputChanged { get; set; }
    public Action<byte, byte>? PortBOutputChanged { get; set; }
    public Action<bool>? IrqChanged { get; set; }
    public bool IrqAsserted => _irqAsserted;

    public byte PortAInput
    {
        get => _portAInput;
        set
        {
            bool oldPa7 = (_portAInput & 0x80) != 0;
            bool newPa7 = (value & 0x80) != 0;
            _portAInput = value;

            bool activeEdge = _pa7PositiveEdge ? !oldPa7 && newPa7 : oldPa7 && !newPa7;
            if (activeEdge)
            {
                _pa7InterruptFlag = true;
                UpdateIrq();
            }
        }
    }

    public byte PortBInput
    {
        get => _portBInput;
        set => _portBInput = value;
    }

    public void Reset()
    {
        Array.Clear(_ram);
        _portAOutput = 0;
        _portADirection = 0;
        _portBOutput = 0;
        _portBDirection = 0;
        _timer = 0;
        _timerDivider = 1;
        _dividerCycles = 1;
        _timerUnderflowed = false;
        _timerInterruptEnabled = false;
        _timerInterruptFlag = false;
        _pa7InterruptEnabled = false;
        _pa7PositiveEdge = false;
        _pa7InterruptFlag = false;
        UpdateIrq();
        NotifyPorts();
    }

    public byte ReadRam(ushort address) => _ram[address & 0x7f];

    public void WriteRam(ushort address, byte value) => _ram[address & 0x7f] = value;

    public byte ReadRegister(ushort address)
    {
        int register = address & 0x1f;
        if ((register & 0x04) == 0)
        {
            return (register & 0x03) switch
            {
                0 => ReadPort(_portAOutput, _portADirection, _portAInput),
                1 => _portADirection,
                2 => ReadPort(_portBOutput, _portBDirection, _portBInput),
                _ => _portBDirection
            };
        }

        if ((register & 0x01) == 0)
        {
            _timerInterruptEnabled = (register & 0x08) != 0;
            _timerInterruptFlag = false;
            UpdateIrq();
            return _timer;
        }

        byte flags = (byte)((_timerInterruptFlag ? 0x80 : 0) | (_pa7InterruptFlag ? 0x40 : 0));
        _pa7InterruptFlag = false;
        UpdateIrq();
        return flags;
    }

    public void WriteRegister(ushort address, byte value)
    {
        int register = address & 0x1f;
        if ((register & 0x04) == 0)
        {
            switch (register & 0x03)
            {
                case 0:
                    _portAOutput = value;
                    PortAOutputChanged?.Invoke(_portAOutput, _portADirection);
                    break;
                case 1:
                    _portADirection = value;
                    PortAOutputChanged?.Invoke(_portAOutput, _portADirection);
                    break;
                case 2:
                    _portBOutput = value;
                    PortBOutputChanged?.Invoke(_portBOutput, _portBDirection);
                    break;
                case 3:
                    _portBDirection = value;
                    PortBOutputChanged?.Invoke(_portBOutput, _portBDirection);
                    break;
            }

            return;
        }

        if ((register & 0x10) != 0)
        {
            int[] divisors = [1, 8, 64, 1024];
            _timer = value;
            _timerDivider = divisors[register & 0x03];
            _dividerCycles = _timerDivider;
            _timerUnderflowed = false;
            _timerInterruptEnabled = (register & 0x08) != 0;
            _timerInterruptFlag = false;
            UpdateIrq();
            return;
        }

        _pa7PositiveEdge = (register & 0x01) != 0;
        _pa7InterruptEnabled = (register & 0x02) != 0;
        UpdateIrq();
    }

    public void Tick(int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        while (cycles > 0)
        {
            if (_timerUnderflowed)
            {
                _timer = unchecked((byte)(_timer - cycles));
                return;
            }

            if (cycles < _dividerCycles)
            {
                _dividerCycles -= cycles;
                return;
            }

            cycles -= _dividerCycles;
            _dividerCycles = _timerDivider;
            if (_timer == 0)
            {
                _timer = 0xff;
                _timerUnderflowed = true;
                _timerInterruptFlag = true;
                UpdateIrq();
            }
            else
            {
                _timer--;
            }
        }
    }

    public void SetPortAInputBit(int bit, bool high)
    {
        if ((uint)bit > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(bit));
        }

        byte mask = (byte)(1 << bit);
        PortAInput = high ? (byte)(PortAInput | mask) : (byte)(PortAInput & ~mask);
    }

    private static byte ReadPort(byte output, byte direction, byte input) =>
        (byte)((output & direction) | (input & ~direction));

    private void NotifyPorts()
    {
        PortAOutputChanged?.Invoke(_portAOutput, _portADirection);
        PortBOutputChanged?.Invoke(_portBOutput, _portBDirection);
    }

    private void UpdateIrq()
    {
        bool asserted = (_timerInterruptEnabled && _timerInterruptFlag) ||
                        (_pa7InterruptEnabled && _pa7InterruptFlag);
        if (asserted == _irqAsserted)
        {
            return;
        }

        _irqAsserted = asserted;
        IrqChanged?.Invoke(asserted);
    }
}
