namespace StarWars.Hardware;

public sealed class AddressSpace
{
    private const int Size = 0x10000;

    private readonly byte[] _memory = new byte[Size];
    private readonly bool[] _mapped = new bool[Size];
    private readonly bool[] _writable = new bool[Size];
    private readonly Func<ushort, byte>?[] _readHandlers = new Func<ushort, byte>?[Size];
    private readonly Action<ushort, byte>?[] _writeHandlers = new Action<ushort, byte>?[Size];

    public byte UnmappedValue { get; set; } = 0xff;

    public byte ReadByte(ushort address)
    {
        Func<ushort, byte>? handler = _readHandlers[address];
        return handler is not null ? handler(address) : _mapped[address] ? _memory[address] : UnmappedValue;
    }

    public ushort ReadWord(ushort address)
    {
        byte high = ReadByte(address);
        byte low = ReadByte(unchecked((ushort)(address + 1)));
        return (ushort)((high << 8) | low);
    }

    public void WriteByte(ushort address, byte value)
    {
        Action<ushort, byte>? handler = _writeHandlers[address];
        if (handler is not null)
        {
            handler(address, value);
        }
        else if (_writable[address])
        {
            _memory[address] = value;
        }
    }

    public void WriteWord(ushort address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(unchecked((ushort)(address + 1)), (byte)value);
    }

    public void MapRam(ushort start, ushort end)
    {
        ValidateRange(start, end);
        for (int address = start; address <= end; address++)
        {
            _memory[address] = 0;
            _mapped[address] = true;
            _writable[address] = true;
            _readHandlers[address] = null;
            _writeHandlers[address] = null;
        }
    }

    public void MapRom(ushort start, ushort end)
    {
        ValidateRange(start, end);
        for (int address = start; address <= end; address++)
        {
            _memory[address] = UnmappedValue;
            _mapped[address] = true;
            _writable[address] = false;
            _readHandlers[address] = null;
            _writeHandlers[address] = null;
        }
    }

    public void MapRead(ushort start, ushort end, Func<ushort, byte> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ValidateRange(start, end);
        for (int address = start; address <= end; address++)
        {
            _readHandlers[address] = read;
            _mapped[address] = true;
        }
    }

    public void MapWrite(ushort start, ushort end, Action<ushort, byte> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateRange(start, end);
        for (int address = start; address <= end; address++)
        {
            _writeHandlers[address] = write;
        }
    }

    public void Load(ushort start, ReadOnlySpan<byte> data)
    {
        if (data.Length > Size - start)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Data extends beyond the 6809 address space.");
        }

        data.CopyTo(_memory.AsSpan(start));
        _mapped.AsSpan(start, data.Length).Fill(true);
    }

    public byte Peek(ushort address) => _memory[address];

    private static void ValidateRange(ushort start, ushort end)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "The end address must not precede the start address.");
        }
    }
}
