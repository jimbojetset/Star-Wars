namespace StarWars.Hardware;

public static class StarWarsHardware
{
    public const int MasterClockHz = 12_096_000;
    public const int CpuClockHz = MasterClockHz / 8;
}

public sealed class StarWarsMainMemory
{
    private const int BankSize = 0x2000;
    private readonly byte[][] _romBanks = [new byte[BankSize], new byte[BankSize]];
    private int _selectedRomBank;

    public StarWarsMainMemory()
    {
        foreach (byte[] bank in _romBanks)
        {
            Array.Fill(bank, (byte)0xff);
        }

        AddressSpace.MapRam(0x0000, 0x2fff);
        AddressSpace.MapRom(0x3000, 0x3fff);

        // 0x4000-0x47ff is decoded by the main board's input, latch, AVG,
        // watchdog, ADC, NOVRAM and Matrix Processor hardware.
        AddressSpace.MapRead(0x4000, 0x47ff, _ => AddressSpace.UnmappedValue);
        AddressSpace.MapWrite(0x4000, 0x47ff, (_, _) => { });

        AddressSpace.MapRead(0x4500, 0x45ff, address => Novram.Read(address - 0x4500));
        AddressSpace.MapWrite(0x4500, 0x45ff, (address, value) => Novram.Write(address - 0x4500, value));

        AddressSpace.MapRam(0x4800, 0x4fff);
        AddressSpace.MapRam(0x5000, 0x5fff);
        AddressSpace.MapRead(0x6000, 0x7fff, ReadBankedRom);
        AddressSpace.MapRom(0x8000, 0xffff);
    }

    public AddressSpace AddressSpace { get; } = new();
    public X2212 Novram { get; } = new();

    public int SelectedRomBank
    {
        get => _selectedRomBank;
        set
        {
            if ((uint)value >= _romBanks.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _selectedRomBank = value;
        }
    }

    public void LoadVectorRom(ReadOnlySpan<byte> data) => LoadExact(0x3000, 0x1000, data);

    public void LoadFixedRom(ushort address, ReadOnlySpan<byte> data)
    {
        if (address < 0x8000 || data.Length > 0x10000 - address)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "Main program ROM must occupy 0x8000-0xffff.");
        }

        AddressSpace.Load(address, data);
    }

    public void LoadBankedRom(int bank, ReadOnlySpan<byte> data)
    {
        if ((uint)bank >= _romBanks.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bank));
        }

        if (data.Length != BankSize)
        {
            throw new ArgumentException("Each Star Wars banked ROM image must be exactly 8 KiB.", nameof(data));
        }

        data.CopyTo(_romBanks[bank]);
    }

    public void MapHardwareRead(ushort start, ushort end, Func<ushort, byte> read)
    {
        ValidateHardwareRange(start, end);
        AddressSpace.MapRead(start, end, read);
    }

    public void MapHardwareWrite(ushort start, ushort end, Action<ushort, byte> write)
    {
        ValidateHardwareRange(start, end);
        AddressSpace.MapWrite(start, end, write);
    }

    private byte ReadBankedRom(ushort address) => _romBanks[_selectedRomBank][address - 0x6000];

    private void LoadExact(ushort address, int expectedLength, ReadOnlySpan<byte> data)
    {
        if (data.Length != expectedLength)
        {
            throw new ArgumentException($"ROM image must be exactly {expectedLength} bytes.", nameof(data));
        }

        AddressSpace.Load(address, data);
    }

    private static void ValidateHardwareRange(ushort start, ushort end)
    {
        if (start < 0x4000 || end > 0x47ff || end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Main-board hardware is decoded in 0x4000-0x47ff.");
        }
    }
}

public sealed class StarWarsSoundMemory
{
    public StarWarsSoundMemory()
    {
        // The latch ranges are deliberately handler-backed: reads and writes
        // have different meanings on the original sound board.
        AddressSpace.MapRead(0x0000, 0x0fff, _ => AddressSpace.UnmappedValue);
        AddressSpace.MapWrite(0x0000, 0x0fff, (_, _) => { });

        AddressSpace.MapRam(0x1000, 0x107f);
        AddressSpace.MapRead(0x1080, 0x109f, _ => AddressSpace.UnmappedValue);
        AddressSpace.MapWrite(0x1080, 0x109f, (_, _) => { });
        AddressSpace.MapWrite(0x1800, 0x183f, (_, _) => { });
        AddressSpace.MapRam(0x2000, 0x27ff);
        AddressSpace.MapRom(0x4000, 0x7fff);
        AddressSpace.MapRom(0xb000, 0xffff);
    }

    public AddressSpace AddressSpace { get; } = new();

    public void LoadRom(ushort address, ReadOnlySpan<byte> data)
    {
        bool firstWindow = address >= 0x4000 && data.Length <= 0x8000 - address;
        bool secondWindow = address >= 0xb000 && data.Length <= 0x10000 - address;
        if (!firstWindow && !secondWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "Sound ROM must occupy 0x4000-0x7fff or 0xb000-0xffff.");
        }

        AddressSpace.Load(address, data);
    }

    public void MapMainLatchWrite(Action<byte> write) =>
        AddressSpace.MapWrite(0x0000, 0x07ff, (_, value) => write(value));

    public void MapSoundLatchRead(Func<byte> read) =>
        AddressSpace.MapRead(0x0800, 0x0fff, _ => read());

    public void MapRiot(Mos6532 riot)
    {
        ArgumentNullException.ThrowIfNull(riot);
        AddressSpace.MapRead(0x1000, 0x107f, riot.ReadRam);
        AddressSpace.MapWrite(0x1000, 0x107f, riot.WriteRam);
        AddressSpace.MapRead(0x1080, 0x109f, riot.ReadRegister);
        AddressSpace.MapWrite(0x1080, 0x109f, riot.WriteRegister);
    }

    public void MapPokeyWrite(Action<ushort, byte> write) =>
        AddressSpace.MapWrite(0x1800, 0x183f, write);
}
