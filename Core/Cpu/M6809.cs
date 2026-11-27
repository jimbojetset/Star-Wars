using StarWars.Hardware;

namespace StarWars.Cpu;

[Flags]
public enum ConditionCode : byte
{
    Carry = 0x01,
    Overflow = 0x02,
    Zero = 0x04,
    Negative = 0x08,
    IrqMask = 0x10,
    HalfCarry = 0x20,
    FirqMask = 0x40,
    EntireState = 0x80
}

public sealed class M6809
{
    private readonly AddressSpace _memory;
    private ushort _s;
    private bool _nmiArmed;
    private bool _nmiPending;
    private bool _nmiLine;
    private bool _irqLine;
    private bool _firqLine;
    private bool _syncWaiting;
    private bool _cwaiWaiting;
    private int _indexedCycles;

    public M6809(AddressSpace memory, int clockHz = StarWarsHardware.CpuClockHz)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockHz);
        ClockHz = clockHz;
    }

    public int ClockHz { get; }
    public long TotalCycles { get; private set; }

    public byte A { get; set; }
    public byte B { get; set; }
    public byte DirectPage { get; set; }
    public ushort X { get; set; }
    public ushort Y { get; set; }
    public ushort U { get; set; }

    public ushort S
    {
        get => _s;
        set
        {
            _s = value;
            _nmiArmed = true;
        }
    }

    public ushort ProgramCounter { get; set; }
    public byte ConditionCodes { get; set; }

    public ushort D
    {
        get => (ushort)((A << 8) | B);
        set
        {
            A = (byte)(value >> 8);
            B = (byte)value;
        }
    }

    public bool CarryFlag { get => Flag(ConditionCode.Carry); set => SetFlag(ConditionCode.Carry, value); }
    public bool OverflowFlag { get => Flag(ConditionCode.Overflow); set => SetFlag(ConditionCode.Overflow, value); }
    public bool ZeroFlag { get => Flag(ConditionCode.Zero); set => SetFlag(ConditionCode.Zero, value); }
    public bool NegativeFlag { get => Flag(ConditionCode.Negative); set => SetFlag(ConditionCode.Negative, value); }
    public bool IrqMasked { get => Flag(ConditionCode.IrqMask); set => SetFlag(ConditionCode.IrqMask, value); }
    public bool HalfCarryFlag { get => Flag(ConditionCode.HalfCarry); set => SetFlag(ConditionCode.HalfCarry, value); }
    public bool FirqMasked { get => Flag(ConditionCode.FirqMask); set => SetFlag(ConditionCode.FirqMask, value); }
    public bool EntireStateFlag { get => Flag(ConditionCode.EntireState); set => SetFlag(ConditionCode.EntireState, value); }
    public bool IsWaiting => _syncWaiting || _cwaiWaiting;

    public void Reset()
    {
        A = 0;
        B = 0;
        DirectPage = 0;
        X = 0;
        Y = 0;
        U = 0;
        _s = 0;
        ConditionCodes = (byte)(ConditionCode.IrqMask | ConditionCode.FirqMask);
        ProgramCounter = _memory.ReadWord(0xfffe);
        _nmiArmed = false;
        _nmiPending = false;
        _syncWaiting = false;
        _cwaiWaiting = false;
        TotalCycles = 7;
    }

    internal void ResetPreservingCycles()
    {
        long cycles = TotalCycles;
        Reset();
        TotalCycles = cycles;
    }

    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    public void SetFirqLine(bool asserted) => _firqLine = asserted;

    public void SetNmiLine(bool asserted)
    {
        if (asserted && !_nmiLine && _nmiArmed)
        {
            _nmiPending = true;
        }

        _nmiLine = asserted;
    }

    public int Step()
    {
        if (_syncWaiting)
        {
            if (!_nmiPending && !_firqLine && !_irqLine)
            {
                return AddCycles(1);
            }
            _syncWaiting = false;
        }

        int interruptCycles = ServiceInterrupt();
        if (interruptCycles != 0)
        {
            return AddCycles(interruptCycles);
        }

        if (_cwaiWaiting)
        {
            return AddCycles(1);
        }

        ushort instructionAddress = ProgramCounter;
        byte opcode = FetchByte();
        int cycles = ExecutePageOne(opcode, instructionAddress);
        return AddCycles(cycles);
    }

    private int ExecutePageOne(byte opcode, ushort instructionAddress)
    {
        if (opcode <= 0x0f)
        {
            ushort address = DirectAddress();
            return ExecuteMemoryUnary(opcode, address, opcode == 0x0e ? 3 : 6);
        }

        if (opcode is >= 0x20 and <= 0x2f)
        {
            sbyte offset = unchecked((sbyte)FetchByte());
            if (BranchCondition(opcode & 0x0f))
            {
                ProgramCounter = AddOffset(ProgramCounter, offset);
            }

            return 3;
        }

        if (opcode is >= 0x40 and <= 0x5f)
        {
            bool accumulatorB = (opcode & 0x10) != 0;
            byte operation = (byte)(opcode & 0x0f);
            byte value = accumulatorB ? B : A;
            byte result = ExecuteUnary(operation, value, out bool writeResult);
            if (writeResult)
            {
                if (accumulatorB)
                {
                    B = result;
                }
                else
                {
                    A = result;
                }
            }

            return 2;
        }

        if (opcode is >= 0x60 and <= 0x6f)
        {
            ushort address = IndexedAddress();
            return ExecuteMemoryUnary(opcode, address, opcode == 0x6e ? 3 + _indexedCycles : 6 + _indexedCycles);
        }

        if (opcode is >= 0x70 and <= 0x7f)
        {
            ushort address = FetchWord();
            return ExecuteMemoryUnary(opcode, address, opcode == 0x7e ? 4 : 7);
        }

        if (opcode >= 0x80 && opcode <= 0xbf)
        {
            return ExecuteAccumulatorAGroup(opcode);
        }

        if (opcode >= 0xc0)
        {
            return ExecuteAccumulatorBGroup(opcode);
        }

        return opcode switch
        {
            0x10 => ExecutePageTwo(FetchByte(), instructionAddress),
            0x11 => ExecutePageThree(FetchByte(), instructionAddress),
            0x12 => 2,
            0x13 => EnterSync(),
            0x16 => LongBranch(true, 5, addCycleWhenTaken: false),
            0x17 => LongBranchToSubroutine(),
            0x19 => DecimalAdjustA(),
            0x1a => OrConditionCodes(),
            0x1c => AndConditionCodes(),
            0x1d => SignExtendB(),
            0x1e => ExchangeRegisters(),
            0x1f => TransferRegister(),
            0x30 => LoadEffectiveAddress(RegisterCode.X),
            0x31 => LoadEffectiveAddress(RegisterCode.Y),
            0x32 => LoadEffectiveAddress(RegisterCode.S),
            0x33 => LoadEffectiveAddress(RegisterCode.U),
            0x34 => PushRegisters(useSystemStack: true),
            0x35 => PullRegisters(useSystemStack: true),
            0x36 => PushRegisters(useSystemStack: false),
            0x37 => PullRegisters(useSystemStack: false),
            0x39 => ReturnFromSubroutine(),
            0x3a => AddBToX(),
            0x3b => ReturnFromInterrupt(),
            0x3c => WaitForInterrupt(),
            0x3d => Multiply(),
            0x3f => SoftwareInterrupt(0xfffa, setMasks: true, 19),
            _ => IllegalOpcode(instructionAddress, opcode)
        };
    }

    private int ExecutePageTwo(byte opcode, ushort instructionAddress)
    {
        if (opcode is >= 0x21 and <= 0x2f)
        {
            return LongBranch(BranchCondition(opcode & 0x0f), 5);
        }

        if (opcode == 0x3f)
        {
            return SoftwareInterrupt(0xfff4, setMasks: false, 20);
        }

        int mode = opcode >> 4;
        int operation = opcode & 0x0f;
        if (mode is >= 8 and <= 11)
        {
            if (operation == 0x03)
            {
                _ = Subtract16(D, ReadOperand16(mode));
                return WordArithmeticCycles(mode) + 1;
            }

            if (operation == 0x0c)
            {
                _ = Subtract16(Y, ReadOperand16(mode));
                return WordArithmeticCycles(mode) + 1;
            }

            if (operation == 0x0e)
            {
                Y = ReadOperand16(mode);
                SetLoadFlags16(Y);
                return WordLoadCycles(mode) + 1;
            }

            if (operation == 0x0f && mode != 8)
            {
                ushort address = OperandAddress(mode);
                _memory.WriteWord(address, Y);
                SetLoadFlags16(Y);
                return WordStoreCycles(mode) + 1;
            }
        }

        if (mode is >= 12 and <= 15 && operation is 0x0e or 0x0f)
        {
            if (operation == 0x0e)
            {
                S = ReadOperand16(mode);
                SetLoadFlags16(S);
                return WordLoadCycles(mode) + 1;
            }

            if (mode != 12)
            {
                ushort address = OperandAddress(mode);
                _memory.WriteWord(address, S);
                SetLoadFlags16(S);
                return WordStoreCycles(mode) + 1;
            }
        }

        return IllegalOpcode(instructionAddress, opcode, 0x10);
    }

    private int ExecutePageThree(byte opcode, ushort instructionAddress)
    {
        if (opcode == 0x3f)
        {
            return SoftwareInterrupt(0xfff2, setMasks: false, 20);
        }

        int mode = opcode >> 4;
        int operation = opcode & 0x0f;
        if (mode is >= 8 and <= 11 && operation is 0x03 or 0x0c)
        {
            ushort left = operation == 0x03 ? U : S;
            _ = Subtract16(left, ReadOperand16(mode));
            return WordArithmeticCycles(mode) + 1;
        }

        return IllegalOpcode(instructionAddress, opcode, 0x11);
    }

    private int ExecuteAccumulatorAGroup(byte opcode)
    {
        int mode = opcode >> 4;
        int operation = opcode & 0x0f;

        switch (operation)
        {
            case 0x0: A = Subtract8(A, ReadOperand8(mode), withBorrow: false); return ByteCycles(mode);
            case 0x1: _ = Subtract8(A, ReadOperand8(mode), withBorrow: false); return ByteCycles(mode);
            case 0x2: A = Subtract8(A, ReadOperand8(mode), withBorrow: true); return ByteCycles(mode);
            case 0x3: D = Subtract16(D, ReadOperand16(mode)); return WordArithmeticCycles(mode);
            case 0x4: A = Logical8((byte)(A & ReadOperand8(mode))); return ByteCycles(mode);
            case 0x5: _ = Logical8((byte)(A & ReadOperand8(mode))); return ByteCycles(mode);
            case 0x6: A = ReadOperand8(mode); SetLoadFlags8(A); return ByteCycles(mode);
            case 0x7:
                if (mode == 8) break;
                ushort storeA = OperandAddress(mode);
                _memory.WriteByte(storeA, A);
                SetLoadFlags8(A);
                return ByteCycles(mode);
            case 0x8: A = Logical8((byte)(A ^ ReadOperand8(mode))); return ByteCycles(mode);
            case 0x9: A = Add8(A, ReadOperand8(mode), withCarry: true); return ByteCycles(mode);
            case 0xa: A = Logical8((byte)(A | ReadOperand8(mode))); return ByteCycles(mode);
            case 0xb: A = Add8(A, ReadOperand8(mode), withCarry: false); return ByteCycles(mode);
            case 0xc: _ = Subtract16(X, ReadOperand16(mode)); return WordArithmeticCycles(mode);
            case 0xd:
                if (mode == 8)
                {
                    sbyte offset = unchecked((sbyte)FetchByte());
                    PushWordSystem(ProgramCounter);
                    ProgramCounter = AddOffset(ProgramCounter, offset);
                    return 7;
                }

                ushort subroutine = OperandAddress(mode);
                PushWordSystem(ProgramCounter);
                ProgramCounter = subroutine;
                return mode switch { 9 => 7, 10 => 7 + _indexedCycles, _ => 8 };
            case 0xe: X = ReadOperand16(mode); SetLoadFlags16(X); return WordLoadCycles(mode);
            case 0xf:
                if (mode == 8) break;
                ushort storeX = OperandAddress(mode);
                _memory.WriteWord(storeX, X);
                SetLoadFlags16(X);
                return WordStoreCycles(mode);
        }

        return IllegalOpcode(unchecked((ushort)(ProgramCounter - 1)), opcode);
    }

    private int ExecuteAccumulatorBGroup(byte opcode)
    {
        int mode = opcode >> 4;
        int operation = opcode & 0x0f;

        switch (operation)
        {
            case 0x0: B = Subtract8(B, ReadOperand8(mode), withBorrow: false); return ByteCycles(mode);
            case 0x1: _ = Subtract8(B, ReadOperand8(mode), withBorrow: false); return ByteCycles(mode);
            case 0x2: B = Subtract8(B, ReadOperand8(mode), withBorrow: true); return ByteCycles(mode);
            case 0x3: D = Add16(D, ReadOperand16(mode)); return WordArithmeticCycles(mode);
            case 0x4: B = Logical8((byte)(B & ReadOperand8(mode))); return ByteCycles(mode);
            case 0x5: _ = Logical8((byte)(B & ReadOperand8(mode))); return ByteCycles(mode);
            case 0x6: B = ReadOperand8(mode); SetLoadFlags8(B); return ByteCycles(mode);
            case 0x7:
                if (mode == 12) break;
                ushort storeB = OperandAddress(mode);
                _memory.WriteByte(storeB, B);
                SetLoadFlags8(B);
                return ByteCycles(mode);
            case 0x8: B = Logical8((byte)(B ^ ReadOperand8(mode))); return ByteCycles(mode);
            case 0x9: B = Add8(B, ReadOperand8(mode), withCarry: true); return ByteCycles(mode);
            case 0xa: B = Logical8((byte)(B | ReadOperand8(mode))); return ByteCycles(mode);
            case 0xb: B = Add8(B, ReadOperand8(mode), withCarry: false); return ByteCycles(mode);
            case 0xc: D = ReadOperand16(mode); SetLoadFlags16(D); return WordLoadCycles(mode);
            case 0xd:
                if (mode == 12) break;
                ushort storeD = OperandAddress(mode);
                _memory.WriteWord(storeD, D);
                SetLoadFlags16(D);
                return WordStoreCycles(mode);
            case 0xe: U = ReadOperand16(mode); SetLoadFlags16(U); return WordLoadCycles(mode);
            case 0xf:
                if (mode == 12) break;
                ushort storeU = OperandAddress(mode);
                _memory.WriteWord(storeU, U);
                SetLoadFlags16(U);
                return WordStoreCycles(mode);
        }

        return IllegalOpcode(unchecked((ushort)(ProgramCounter - 1)), opcode);
    }

    private byte ReadOperand8(int mode) => mode switch
    {
        8 or 12 => FetchByte(),
        9 or 13 => _memory.ReadByte(DirectAddress()),
        10 or 14 => _memory.ReadByte(IndexedAddress()),
        11 or 15 => _memory.ReadByte(FetchWord()),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private ushort ReadOperand16(int mode) => mode switch
    {
        8 or 12 => FetchWord(),
        9 or 13 => _memory.ReadWord(DirectAddress()),
        10 or 14 => _memory.ReadWord(IndexedAddress()),
        11 or 15 => _memory.ReadWord(FetchWord()),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private ushort OperandAddress(int mode) => mode switch
    {
        9 or 13 => DirectAddress(),
        10 or 14 => IndexedAddress(),
        11 or 15 => FetchWord(),
        _ => throw new InvalidOperationException("Immediate operands do not have an address.")
    };

    private int ByteCycles(int mode) => mode switch
    {
        8 or 12 => 2,
        9 or 13 => 4,
        10 or 14 => 4 + _indexedCycles,
        11 or 15 => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private int WordArithmeticCycles(int mode) => mode switch
    {
        8 or 12 => 4,
        9 or 13 => 6,
        10 or 14 => 6 + _indexedCycles,
        11 or 15 => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private int WordLoadCycles(int mode) => mode switch
    {
        8 or 12 => 3,
        9 or 13 => 5,
        10 or 14 => 5 + _indexedCycles,
        11 or 15 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private int WordStoreCycles(int mode) => mode switch
    {
        9 or 13 => 5,
        10 or 14 => 5 + _indexedCycles,
        11 or 15 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private ushort IndexedAddress()
    {
        byte postByte = FetchByte();
        int register = (postByte >> 5) & 0x03;
        ushort baseValue = GetIndexRegister(register);
        _indexedCycles = 0;

        if ((postByte & 0x80) == 0)
        {
            int offset = postByte & 0x1f;
            if ((offset & 0x10) != 0)
            {
                offset -= 0x20;
            }

            _indexedCycles = 1;
            return AddOffset(baseValue, offset);
        }

        bool indirect = (postByte & 0x10) != 0;
        int mode = postByte & 0x0f;
        ushort address;

        switch (mode)
        {
            case 0x0:
                address = baseValue;
                SetIndexRegister(register, unchecked((ushort)(baseValue + 1)));
                _indexedCycles = 2;
                break;
            case 0x1:
                address = baseValue;
                SetIndexRegister(register, unchecked((ushort)(baseValue + 2)));
                _indexedCycles = 3;
                break;
            case 0x2:
                baseValue = unchecked((ushort)(baseValue - 1));
                SetIndexRegister(register, baseValue);
                address = baseValue;
                _indexedCycles = 2;
                break;
            case 0x3:
                baseValue = unchecked((ushort)(baseValue - 2));
                SetIndexRegister(register, baseValue);
                address = baseValue;
                _indexedCycles = 3;
                break;
            case 0x4:
                address = baseValue;
                break;
            case 0x5:
                address = AddOffset(baseValue, unchecked((sbyte)B));
                _indexedCycles = 1;
                break;
            case 0x6:
                address = AddOffset(baseValue, unchecked((sbyte)A));
                _indexedCycles = 1;
                break;
            case 0x8:
                address = AddOffset(baseValue, unchecked((sbyte)FetchByte()));
                _indexedCycles = 1;
                break;
            case 0x9:
                address = AddOffset(baseValue, unchecked((short)FetchWord()));
                _indexedCycles = 4;
                break;
            case 0xb:
                address = AddOffset(baseValue, unchecked((short)D));
                _indexedCycles = 4;
                break;
            case 0xc:
                sbyte pcOffset8 = unchecked((sbyte)FetchByte());
                address = AddOffset(ProgramCounter, pcOffset8);
                _indexedCycles = 1;
                break;
            case 0xd:
                short pcOffset16 = unchecked((short)FetchWord());
                address = AddOffset(ProgramCounter, pcOffset16);
                _indexedCycles = 5;
                break;
            case 0xf when indirect:
                address = FetchWord();
                _indexedCycles = 2;
                break;
            default:
                throw new InvalidOperationException($"Illegal 6809 indexed post-byte 0x{postByte:X2}.");
        }

        if (indirect)
        {
            address = _memory.ReadWord(address);
            _indexedCycles += 3;
        }

        return address;
    }

    private int ExecuteMemoryUnary(byte opcode, ushort address, int cycles)
    {
        int operation = opcode & 0x0f;
        if (operation == 0x0e)
        {
            ProgramCounter = address;
            return cycles;
        }

        byte value = _memory.ReadByte(address);
        byte result = ExecuteUnary(operation, value, out bool writeResult);
        if (writeResult)
        {
            _memory.WriteByte(address, result);
        }

        return cycles;
    }

    private byte ExecuteUnary(int operation, byte value, out bool writeResult)
    {
        writeResult = true;
        byte result;

        switch (operation)
        {
            case 0x0:
                result = unchecked((byte)-value);
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, value == 0x80);
                SetFlag(ConditionCode.Carry, value != 0);
                return result;
            case 0x3:
                result = (byte)~value;
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, false);
                SetFlag(ConditionCode.Carry, true);
                return result;
            case 0x4:
                SetFlag(ConditionCode.Carry, (value & 0x01) != 0);
                result = (byte)(value >> 1);
                SetNz8(result);
                SetFlag(ConditionCode.Negative, false);
                SetFlag(ConditionCode.Overflow, NegativeFlag ^ CarryFlag);
                return result;
            case 0x6:
                bool rorCarry = CarryFlag;
                SetFlag(ConditionCode.Carry, (value & 0x01) != 0);
                result = (byte)((value >> 1) | (rorCarry ? 0x80 : 0));
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, NegativeFlag ^ CarryFlag);
                return result;
            case 0x7:
                SetFlag(ConditionCode.Carry, (value & 0x01) != 0);
                result = (byte)((value >> 1) | (value & 0x80));
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, NegativeFlag ^ CarryFlag);
                return result;
            case 0x8:
                SetFlag(ConditionCode.Carry, (value & 0x80) != 0);
                result = (byte)(value << 1);
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, NegativeFlag ^ CarryFlag);
                return result;
            case 0x9:
                bool rolCarry = CarryFlag;
                SetFlag(ConditionCode.Carry, (value & 0x80) != 0);
                result = (byte)((value << 1) | (rolCarry ? 1 : 0));
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, NegativeFlag ^ CarryFlag);
                return result;
            case 0xa:
                result = unchecked((byte)(value - 1));
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, value == 0x80);
                return result;
            case 0xc:
                result = unchecked((byte)(value + 1));
                SetNz8(result);
                SetFlag(ConditionCode.Overflow, value == 0x7f);
                return result;
            case 0xd:
                SetNz8(value);
                SetFlag(ConditionCode.Overflow, false);
                writeResult = false;
                return value;
            case 0xf:
                SetFlag(ConditionCode.Negative, false);
                SetFlag(ConditionCode.Zero, true);
                SetFlag(ConditionCode.Overflow, false);
                SetFlag(ConditionCode.Carry, false);
                return 0;
            default:
                throw new InvalidOperationException($"Illegal 6809 unary operation 0x{operation:X1}.");
        }
    }

    private byte Add8(byte left, byte right, bool withCarry)
    {
        int carry = withCarry && CarryFlag ? 1 : 0;
        int sum = left + right + carry;
        byte result = (byte)sum;
        SetFlag(ConditionCode.HalfCarry, ((left & 0x0f) + (right & 0x0f) + carry) > 0x0f);
        SetNz8(result);
        SetFlag(ConditionCode.Overflow, (~(left ^ right) & (left ^ result) & 0x80) != 0);
        SetFlag(ConditionCode.Carry, sum > 0xff);
        return result;
    }

    private byte Subtract8(byte left, byte right, bool withBorrow)
    {
        int borrow = withBorrow && CarryFlag ? 1 : 0;
        int difference = left - right - borrow;
        byte result = (byte)difference;
        SetNz8(result);
        SetFlag(ConditionCode.Overflow, ((left ^ right) & (left ^ result) & 0x80) != 0);
        SetFlag(ConditionCode.Carry, difference < 0);
        return result;
    }

    private ushort Add16(ushort left, ushort right)
    {
        int sum = left + right;
        ushort result = (ushort)sum;
        SetNz16(result);
        SetFlag(ConditionCode.Overflow, (~(left ^ right) & (left ^ result) & 0x8000) != 0);
        SetFlag(ConditionCode.Carry, sum > 0xffff);
        return result;
    }

    private ushort Subtract16(ushort left, ushort right)
    {
        int difference = left - right;
        ushort result = (ushort)difference;
        SetNz16(result);
        SetFlag(ConditionCode.Overflow, ((left ^ right) & (left ^ result) & 0x8000) != 0);
        SetFlag(ConditionCode.Carry, difference < 0);
        return result;
    }

    private byte Logical8(byte result)
    {
        SetLoadFlags8(result);
        return result;
    }

    private int DecimalAdjustA()
    {
        int correction = 0;
        int lowNibble = A & 0x0f;
        int highNibble = A & 0xf0;

        if (lowNibble > 9 || HalfCarryFlag)
        {
            correction |= 0x06;
        }

        if (highNibble > 0x90 || CarryFlag || (highNibble > 0x80 && lowNibble > 9))
        {
            correction |= 0x60;
        }

        int adjusted = A + correction;
        A = (byte)adjusted;
        SetNz8(A);
        SetFlag(ConditionCode.Carry, CarryFlag || adjusted > 0xff);
        return 2;
    }

    private int SignExtendB()
    {
        A = (B & 0x80) == 0 ? (byte)0x00 : (byte)0xff;
        SetNz16(D);
        SetFlag(ConditionCode.Overflow, false);
        return 2;
    }

    private int Multiply()
    {
        D = (ushort)(A * B);
        SetFlag(ConditionCode.Zero, D == 0);
        SetFlag(ConditionCode.Carry, (B & 0x80) != 0);
        return 11;
    }

    private int EnterSync()
    {
        _syncWaiting = true;
        return 4;
    }

    private int WaitForInterrupt()
    {
        ConditionCodes &= FetchByte();
        EntireStateFlag = true;
        PushEntireState();
        _cwaiWaiting = true;
        return 20;
    }

    private int OrConditionCodes()
    {
        ConditionCodes |= FetchByte();
        return 3;
    }

    private int AndConditionCodes()
    {
        ConditionCodes &= FetchByte();
        return 3;
    }

    private int LoadEffectiveAddress(RegisterCode register)
    {
        ushort address = IndexedAddress();
        WriteRegister(register, address, 16);
        if (register is RegisterCode.X or RegisterCode.Y)
        {
            SetFlag(ConditionCode.Zero, address == 0);
        }

        return 4 + _indexedCycles;
    }

    private int ExchangeRegisters()
    {
        byte postByte = FetchByte();
        RegisterValue first = ReadRegister((RegisterCode)(postByte >> 4));
        RegisterValue second = ReadRegister((RegisterCode)(postByte & 0x0f));
        WriteRegister((RegisterCode)(postByte >> 4), ConvertRegister(second, first.Bits), first.Bits);
        WriteRegister((RegisterCode)(postByte & 0x0f), ConvertRegister(first, second.Bits), second.Bits);
        return 8;
    }

    private int TransferRegister()
    {
        byte postByte = FetchByte();
        RegisterCode source = (RegisterCode)(postByte >> 4);
        RegisterCode destination = (RegisterCode)(postByte & 0x0f);
        RegisterValue value = ReadRegister(source);
        int destinationBits = ReadRegister(destination).Bits;
        WriteRegister(destination, ConvertRegister(value, destinationBits), destinationBits);
        return 6;
    }

    private static ushort ConvertRegister(RegisterValue value, int destinationBits) =>
        value.Bits == destinationBits ? value.Value :
        destinationBits == 16 ? (ushort)(0xff00 | (value.Value & 0xff)) :
        (ushort)(value.Value & 0xff);

    private RegisterValue ReadRegister(RegisterCode register) => register switch
    {
        RegisterCode.D => new(D, 16),
        RegisterCode.X => new(X, 16),
        RegisterCode.Y => new(Y, 16),
        RegisterCode.U => new(U, 16),
        RegisterCode.S => new(S, 16),
        RegisterCode.Pc => new(ProgramCounter, 16),
        RegisterCode.A => new(A, 8),
        RegisterCode.B => new(B, 8),
        RegisterCode.Cc => new(ConditionCodes, 8),
        RegisterCode.Dp => new(DirectPage, 8),
        _ => throw new InvalidOperationException($"Illegal 6809 transfer register code 0x{(int)register:X1}.")
    };

    private void WriteRegister(RegisterCode register, ushort value, int bits)
    {
        if (ReadRegister(register).Bits != bits)
        {
            throw new InvalidOperationException("Internal 6809 register-width mismatch.");
        }

        switch (register)
        {
            case RegisterCode.D: D = value; break;
            case RegisterCode.X: X = value; break;
            case RegisterCode.Y: Y = value; break;
            case RegisterCode.U: U = value; break;
            case RegisterCode.S: S = value; break;
            case RegisterCode.Pc: ProgramCounter = value; break;
            case RegisterCode.A: A = (byte)value; break;
            case RegisterCode.B: B = (byte)value; break;
            case RegisterCode.Cc: ConditionCodes = (byte)value; break;
            case RegisterCode.Dp: DirectPage = (byte)value; break;
            default: throw new InvalidOperationException($"Illegal 6809 transfer register code 0x{(int)register:X1}.");
        }
    }

    private int PushRegisters(bool useSystemStack)
    {
        byte mask = FetchByte();
        int bytes = 0;
        if ((mask & 0x80) != 0) { PushWord(refStack: useSystemStack, ProgramCounter); bytes += 2; }
        if ((mask & 0x40) != 0) { PushWord(refStack: useSystemStack, useSystemStack ? U : S); bytes += 2; }
        if ((mask & 0x20) != 0) { PushWord(refStack: useSystemStack, Y); bytes += 2; }
        if ((mask & 0x10) != 0) { PushWord(refStack: useSystemStack, X); bytes += 2; }
        if ((mask & 0x08) != 0) { PushByte(refStack: useSystemStack, DirectPage); bytes++; }
        if ((mask & 0x04) != 0) { PushByte(refStack: useSystemStack, B); bytes++; }
        if ((mask & 0x02) != 0) { PushByte(refStack: useSystemStack, A); bytes++; }
        if ((mask & 0x01) != 0) { PushByte(refStack: useSystemStack, ConditionCodes); bytes++; }
        return 5 + bytes;
    }

    private int PullRegisters(bool useSystemStack)
    {
        byte mask = FetchByte();
        int bytes = 0;
        if ((mask & 0x01) != 0) { ConditionCodes = PullByte(useSystemStack); bytes++; }
        if ((mask & 0x02) != 0) { A = PullByte(useSystemStack); bytes++; }
        if ((mask & 0x04) != 0) { B = PullByte(useSystemStack); bytes++; }
        if ((mask & 0x08) != 0) { DirectPage = PullByte(useSystemStack); bytes++; }
        if ((mask & 0x10) != 0) { X = PullWord(useSystemStack); bytes += 2; }
        if ((mask & 0x20) != 0) { Y = PullWord(useSystemStack); bytes += 2; }
        if ((mask & 0x40) != 0)
        {
            ushort value = PullWord(useSystemStack);
            if (useSystemStack) U = value; else S = value;
            bytes += 2;
        }
        if ((mask & 0x80) != 0) { ProgramCounter = PullWord(useSystemStack); bytes += 2; }
        return 5 + bytes;
    }

    private int ReturnFromSubroutine()
    {
        ProgramCounter = PullWord(useSystemStack: true);
        return 5;
    }

    private int AddBToX()
    {
        X = unchecked((ushort)(X + B));
        return 3;
    }

    private int ReturnFromInterrupt()
    {
        ConditionCodes = PullByte(useSystemStack: true);
        bool entireState = EntireStateFlag;
        if (entireState)
        {
            A = PullByte(true);
            B = PullByte(true);
            DirectPage = PullByte(true);
            X = PullWord(true);
            Y = PullWord(true);
            U = PullWord(true);
        }

        ProgramCounter = PullWord(true);
        return entireState ? 15 : 6;
    }

    private int LongBranch(bool take, int cycles, bool addCycleWhenTaken = true)
    {
        short offset = unchecked((short)FetchWord());
        if (take)
        {
            ProgramCounter = AddOffset(ProgramCounter, offset);
            if (addCycleWhenTaken)
            {
                cycles++;
            }
        }

        return cycles;
    }

    private int LongBranchToSubroutine()
    {
        short offset = unchecked((short)FetchWord());
        PushWordSystem(ProgramCounter);
        ProgramCounter = AddOffset(ProgramCounter, offset);
        return 9;
    }

    private bool BranchCondition(int condition) => condition switch
    {
        0x0 => true,
        0x1 => false,
        0x2 => !CarryFlag && !ZeroFlag,
        0x3 => CarryFlag || ZeroFlag,
        0x4 => !CarryFlag,
        0x5 => CarryFlag,
        0x6 => !ZeroFlag,
        0x7 => ZeroFlag,
        0x8 => !OverflowFlag,
        0x9 => OverflowFlag,
        0xa => !NegativeFlag,
        0xb => NegativeFlag,
        0xc => NegativeFlag == OverflowFlag,
        0xd => NegativeFlag != OverflowFlag,
        0xe => !ZeroFlag && NegativeFlag == OverflowFlag,
        0xf => ZeroFlag || NegativeFlag != OverflowFlag,
        _ => throw new ArgumentOutOfRangeException(nameof(condition))
    };

    private int ServiceInterrupt()
    {
        if (_nmiPending)
        {
            _nmiPending = false;
            return EnterHardwareInterrupt(0xfffc, entireState: true, maskFirq: true);
        }

        if (_firqLine && !FirqMasked)
        {
            return EnterHardwareInterrupt(0xfff6, entireState: false, maskFirq: true);
        }

        if (_irqLine && !IrqMasked)
        {
            return EnterHardwareInterrupt(0xfff8, entireState: true, maskFirq: false);
        }

        return 0;
    }

    private int EnterHardwareInterrupt(ushort vector, bool entireState, bool maskFirq)
    {
        bool alreadyStacked = _cwaiWaiting;
        _cwaiWaiting = false;
        _syncWaiting = false;

        if (!alreadyStacked)
        {
            EntireStateFlag = entireState;
            if (entireState)
            {
                PushEntireState();
            }
            else
            {
                PushWordSystem(ProgramCounter);
                PushByteSystem(ConditionCodes);
            }
        }

        IrqMasked = true;
        if (maskFirq)
        {
            FirqMasked = true;
        }

        ProgramCounter = _memory.ReadWord(vector);
        if (alreadyStacked)
        {
            return 7;
        }

        return entireState ? 19 : 10;
    }

    private int SoftwareInterrupt(ushort vector, bool setMasks, int cycles)
    {
        EntireStateFlag = true;
        PushEntireState();
        if (setMasks)
        {
            IrqMasked = true;
            FirqMasked = true;
        }

        ProgramCounter = _memory.ReadWord(vector);
        return cycles;
    }

    private void PushEntireState()
    {
        PushWordSystem(ProgramCounter);
        PushWordSystem(U);
        PushWordSystem(Y);
        PushWordSystem(X);
        PushByteSystem(DirectPage);
        PushByteSystem(B);
        PushByteSystem(A);
        PushByteSystem(ConditionCodes);
    }

    private void PushWordSystem(ushort value) => PushWord(refStack: true, value);
    private void PushByteSystem(byte value) => PushByte(refStack: true, value);

    private void PushWord(bool refStack, ushort value)
    {
        PushByte(refStack, (byte)value);
        PushByte(refStack, (byte)(value >> 8));
    }

    private void PushByte(bool refStack, byte value)
    {
        if (refStack)
        {
            S = unchecked((ushort)(S - 1));
            _memory.WriteByte(S, value);
        }
        else
        {
            U = unchecked((ushort)(U - 1));
            _memory.WriteByte(U, value);
        }
    }

    private ushort PullWord(bool useSystemStack)
    {
        byte high = PullByte(useSystemStack);
        byte low = PullByte(useSystemStack);
        return (ushort)((high << 8) | low);
    }

    private byte PullByte(bool useSystemStack)
    {
        if (useSystemStack)
        {
            byte value = _memory.ReadByte(S);
            S = unchecked((ushort)(S + 1));
            return value;
        }
        else
        {
            byte value = _memory.ReadByte(U);
            U = unchecked((ushort)(U + 1));
            return value;
        }
    }

    private ushort DirectAddress() => (ushort)((DirectPage << 8) | FetchByte());

    private byte FetchByte()
    {
        byte value = _memory.ReadByte(ProgramCounter);
        ProgramCounter = unchecked((ushort)(ProgramCounter + 1));
        return value;
    }

    private ushort FetchWord()
    {
        byte high = FetchByte();
        byte low = FetchByte();
        return (ushort)((high << 8) | low);
    }

    private ushort GetIndexRegister(int register) => register switch
    {
        0 => X,
        1 => Y,
        2 => U,
        3 => S,
        _ => throw new ArgumentOutOfRangeException(nameof(register))
    };

    private void SetIndexRegister(int register, ushort value)
    {
        switch (register)
        {
            case 0: X = value; break;
            case 1: Y = value; break;
            case 2: U = value; break;
            case 3: S = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(register));
        }
    }

    private void SetLoadFlags8(byte value)
    {
        SetNz8(value);
        SetFlag(ConditionCode.Overflow, false);
    }

    private void SetLoadFlags16(ushort value)
    {
        SetNz16(value);
        SetFlag(ConditionCode.Overflow, false);
    }

    private void SetNz8(byte value)
    {
        SetFlag(ConditionCode.Negative, (value & 0x80) != 0);
        SetFlag(ConditionCode.Zero, value == 0);
    }

    private void SetNz16(ushort value)
    {
        SetFlag(ConditionCode.Negative, (value & 0x8000) != 0);
        SetFlag(ConditionCode.Zero, value == 0);
    }

    private bool Flag(ConditionCode flag) => (ConditionCodes & (byte)flag) != 0;

    private void SetFlag(ConditionCode flag, bool set)
    {
        if (set)
        {
            ConditionCodes |= (byte)flag;
        }
        else
        {
            ConditionCodes &= unchecked((byte)~(byte)flag);
        }
    }

    private int AddCycles(int cycles)
    {
        TotalCycles += cycles;
        return cycles;
    }

    private static ushort AddOffset(ushort address, int offset) => unchecked((ushort)(address + offset));

    private static int IllegalOpcode(ushort address, byte opcode, byte? prefix = null)
    {
        string encoded = prefix.HasValue ? $"0x{prefix:X2} 0x{opcode:X2}" : $"0x{opcode:X2}";
        throw new InvalidOperationException($"Illegal 6809 opcode {encoded} at 0x{address:X4}.");
    }

    private readonly record struct RegisterValue(ushort Value, int Bits);

    private enum RegisterCode
    {
        D = 0x0,
        X = 0x1,
        Y = 0x2,
        U = 0x3,
        S = 0x4,
        Pc = 0x5,
        A = 0x8,
        B = 0x9,
        Cc = 0xa,
        Dp = 0xb
    }
}
