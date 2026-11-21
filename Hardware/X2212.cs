namespace StarWars.Hardware;

public sealed class X2212
{
    public const int Size = 0x100;

    private readonly byte[] _sram = new byte[Size];
    private readonly byte[] _eeprom = new byte[Size];
    private string? _path;
    private bool _storeLine;
    private bool _recallLine;

    public X2212()
    {
        Array.Fill(_sram, (byte)0x0f);
        Array.Fill(_eeprom, (byte)0x0f);
    }

    public byte Read(int address) => (byte)(0xf0 | _sram[address]);

    public void Write(int address, byte value) => _sram[address] = (byte)(value & 0x0f);

    public void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;

        if (File.Exists(path))
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length != Size)
            {
                throw new InvalidDataException($"The X2212 image must be exactly {Size} bytes.");
            }

            for (int address = 0; address < Size; address++)
            {
                _eeprom[address] = (byte)(data[address] & 0x0f);
            }
        }

        Recall();
    }

    public void SetStoreLine(bool asserted)
    {
        if (asserted && !_storeLine)
        {
            Array.Copy(_sram, _eeprom, Size);
        }

        _storeLine = asserted;
    }

    public void SetRecallLine(bool asserted)
    {
        if (asserted && !_recallLine)
        {
            Recall();
        }

        _recallLine = asserted;
    }

    public void Recall() => Array.Copy(_eeprom, _sram, Size);

    public void Save()
    {
        if (_path is null)
        {
            return;
        }

        // The original board stores only when the STORE line is pulsed. MAME
        // auto-saves the live SRAM on exit so calibration changes are not lost.
        Array.Copy(_sram, _eeprom, Size);
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(_path, _eeprom);
    }
}
