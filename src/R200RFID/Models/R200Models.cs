using System.Collections.ObjectModel;

namespace R200RFID;

/// <summary>Představuje rámec protokolu R200 s ověřeným kontrolním součtem.</summary>
/// <param name="Type">Směr a účel rámce.</param>
/// <param name="Command">Původní bajt příkazu.</param>
/// <param name="Payload">Data rámce bez ohraničujících bajtů a kontrolního součtu.</param>
public sealed record R200Frame(R200FrameType Type, byte Command, byte[] Payload)
{
    /// <summary>Získá typovaný příkaz, pokud je bajt příkazu známý.</summary>
    public R200Command? KnownCommand => Enum.IsDefined(typeof(R200Command), Command)
        ? (R200Command)Command
        : null;

    /// <summary>Serializuje rámec a znovu vypočítá PL i kontrolní součet.</summary>
    public byte[] ToByteArray() => R200ProtocolCodec.BuildFrame(Type, Command, Payload);
}

/// <summary>Obsahuje jeden informační řetězec ASCII vrácený modulem.</summary>
/// <param name="Type">Požadovaná kategorie informací.</param>
/// <param name="Value">Dekódovaná hodnota ASCII.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200ModuleInformation(
    R200ModuleInformationType Type,
    string Value,
    R200Frame RawFrame);

/// <summary>Identifikuje tag pomocí jeho řídicího slova protokolu a bajtů EPC.</summary>
/// <param name="ProtocolControl">Slovo PC protokolu Gen2.</param>
/// <param name="Epc">Bajty EPC v pořadí přenosu.</param>
public sealed record R200TagIdentity(ushort ProtocolControl, byte[] Epc)
{
    /// <summary>Získá EPC jako hexadecimální řetězec psaný velkými písmeny.</summary>
    public string EpcHex => Convert.ToHexString(Epc);
}

/// <summary>Představuje jedno oznámení o tagu z inventory.</summary>
/// <param name="RssiDbm">Hodnota RSSI se znaménkem v dBm.</param>
/// <param name="Tag">Identita tagu.</param>
/// <param name="Crc">CRC vrácené tagem.</param>
/// <param name="RawFrame">Původní rámec oznámení.</param>
public sealed record R200InventoryTag(
    sbyte RssiDbm,
    R200TagIdentity Tag,
    ushort Crc,
    R200Frame RawFrame);

/// <summary>Popisuje selhání příkazu vrácené prostřednictvím kódu příkazu 0xFF.</summary>
/// <param name="ErrorCode">Původní kód chyby modulu.</param>
/// <param name="Description">Srozumitelný popis chyby protokolu.</param>
/// <param name="Tag">Identita tagu, pokud neúspěšná operace už získala EPC.</param>
/// <param name="RawFrame">Původní chybový rámec.</param>
public sealed record R200Failure(
    byte ErrorCode,
    string Description,
    R200TagIdentity? Tag,
    R200Frame RawFrame);

/// <summary>Obsahuje tagy a případnou koncovou chybu získané během jednoho volání inventory.</summary>
/// <param name="Tags">Všechna přijatá platná oznámení o tazích.</param>
/// <param name="Failure">Volitelná chyba inventory, obvykle 0x15, pokud nebyl nalezen žádný tag.</param>
/// <param name="RawFrames">Všechny rámce zachycené během operace.</param>
public sealed record R200InventoryBatch(
    IReadOnlyList<R200InventoryTag> Tags,
    R200Failure? Failure,
    IReadOnlyList<R200Frame> RawFrames)
{
    /// <summary>Vytvoří neměnný výsledek inventory z měnitelných kolekcí.</summary>
    public static R200InventoryBatch Create(
        IEnumerable<R200InventoryTag> tags,
        R200Failure? failure,
        IEnumerable<R200Frame> rawFrames) =>
        new(
            new ReadOnlyCollection<R200InventoryTag>(tags.ToList()),
            failure,
            new ReadOnlyCollection<R200Frame>(rawFrames.ToList()));
}

/// <summary>Definuje bitovou operaci Gen2 Select používanou k výběru tagů.</summary>
/// <param name="Target">Tříbitové pole Target příkazu Select.</param>
/// <param name="Action">Tříbitové pole Action příkazu Select.</param>
/// <param name="MemoryBank">Paměťová banka porovnávaná příkazem Select.</param>
/// <param name="BitPointer">Bitová adresa, na které začíná porovnání.</param>
/// <param name="MaskBitLength">Počet významových bitů masky.</param>
/// <param name="Truncate">Určuje, zda je zapnuté zkrácení Select.</param>
/// <param name="Mask">Bajty masky.</param>
/// <param name="RawFrame">Původní rámec při načtení parametrů z modulu.</param>
public sealed record R200SelectParameters(
    byte Target,
    byte Action,
    R200MemoryBank MemoryBank,
    uint BitPointer,
    byte MaskBitLength,
    bool Truncate,
    byte[] Mask,
    R200Frame? RawFrame = null);

/// <summary>Představuje sbalená pole Gen2 Query podporovaná protokolem modulu.</summary>
/// <param name="DivideRatio">Poměr dělení při přenosu ze čtečky do tagu.</param>
/// <param name="M">Kódování přenosu z tagu do čtečky.</param>
/// <param name="UsePilotTone">Určuje, zda TRext požaduje pilotní tón.</param>
/// <param name="Selection">Kritérium výběru tagů.</param>
/// <param name="Session">Relace inventory.</param>
/// <param name="Target">Cílový příznak inventory.</param>
/// <param name="Q">Čtyřbitová hodnota Q.</param>
/// <param name="RawFrame">Původní rámec při načtení parametrů z modulu.</param>
public sealed record R200QueryParameters(
    R200QueryDivideRatio DivideRatio,
    R200QueryM M,
    bool UsePilotTone,
    R200QuerySelection Selection,
    R200QuerySession Session,
    R200QueryTarget Target,
    byte Q,
    R200Frame? RawFrame = null)
{
    /// <summary>Sbalí všechna pole Query do zdokumentované 16bitové hodnoty přenášené po lince.</summary>
    public ushort ToUInt16()
    {
        if (Q > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(Q), "Q must be between 0 and 15.");
        }

        // Dolní tři bity jsou vyhrazené, a proto zůstávají nulové.
        return (ushort)(
            ((ushort)DivideRatio << 15) |
            ((ushort)M << 13) |
            (UsePilotTone ? 1 << 12 : 0) |
            ((ushort)Selection << 10) |
            ((ushort)Session << 8) |
            ((ushort)Target << 7) |
            (Q << 3));
    }

    /// <summary>Rozbalí 16bitovou hodnotu z linky do typovaných polí Query.</summary>
    public static R200QueryParameters FromUInt16(ushort value, R200Frame? rawFrame = null) => new(
        (R200QueryDivideRatio)((value >> 15) & 0x01),
        (R200QueryM)((value >> 13) & 0x03),
        ((value >> 12) & 0x01) != 0,
        (R200QuerySelection)((value >> 10) & 0x03),
        (R200QuerySession)((value >> 8) & 0x03),
        (R200QueryTarget)((value >> 7) & 0x01),
        (byte)((value >> 3) & 0x0F),
        rawFrame);
}

/// <summary>Obsahuje identitu tagu, stav a volitelná koncová data vrácená měnící operací.</summary>
/// <param name="Tag">Dotčený tag.</param>
/// <param name="Status">Stavový bajt operace.</param>
/// <param name="AdditionalData">Případné bajty specifické pro firmware následující za stavem.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200TagOperationResult(
    R200TagIdentity Tag,
    byte Status,
    byte[] AdditionalData,
    R200Frame RawFrame);

/// <summary>Obsahuje bajty načtené z vybraného tagu.</summary>
/// <param name="Tag">Tag, který poskytl data.</param>
/// <param name="Data">Původní bajty paměti.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200TagReadResult(
    R200TagIdentity Tag,
    byte[] Data,
    R200Frame RawFrame);

/// <summary>Představuje zesílení směšovače, zesílení mezifrekvence a práh demodulace.</summary>
/// <param name="MixerGainCode">Zdokumentovaný kód zesílení směšovače.</param>
/// <param name="IfGainCode">Zdokumentovaný kód zesílení mezifrekvenčního zesilovače.</param>
/// <param name="Threshold">Práh demodulace signálu.</param>
public sealed record R200DemodulatorParameters(
    byte MixerGainCode,
    byte IfGainCode,
    ushort Threshold)
{
    /// <summary>Získá zesílení směšovače reprezentované hodnotou <see cref="MixerGainCode"/>.</summary>
    public int MixerGainDb => MixerGainCode switch
    {
        0 => 0,
        1 => 3,
        2 => 6,
        3 => 9,
        4 => 12,
        5 => 15,
        6 => 16,
        _ => throw new InvalidOperationException($"Unknown mixer gain code 0x{MixerGainCode:X2}.")
    };

    /// <summary>Získá zesílení mezifrekvenčního zesilovače reprezentované hodnotou <see cref="IfGainCode"/>.</summary>
    public int IfGainDb => IfGainCode switch
    {
        0 => 12,
        1 => 18,
        2 => 21,
        3 => 24,
        4 => 27,
        5 => 30,
        6 => 36,
        7 => 40,
        _ => throw new InvalidOperationException($"Unknown IF gain code 0x{IfGainCode:X2}.")
    };
}

/// <summary>Obsahuje jedno měření RF se znaménkem pro daný kanál.</summary>
/// <param name="ChannelIndex">Index kanálu specifický pro region.</param>
/// <param name="SignalDbm">Úroveň signálu se znaménkem v dBm.</param>
public sealed record R200ChannelSample(byte ChannelIndex, sbyte SignalDbm);

/// <summary>Obsahuje všechny vzorky vrácené měřením rušení nebo RSSI.</summary>
/// <param name="StartChannel">Index prvního měřeného kanálu.</param>
/// <param name="EndChannel">Index posledního měřeného kanálu.</param>
/// <param name="Samples">Jeden vzorek pro každý kanál včetně obou krajních hodnot.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200ChannelScanResult(
    byte StartChannel,
    byte EndChannel,
    IReadOnlyList<R200ChannelSample> Samples,
    R200Frame RawFrame);

/// <summary>Obsahuje výsledek jedné operace nastavení, zápisu nebo čtení IO.</summary>
/// <param name="Operation">Operace vrácená modulem.</param>
/// <param name="Port">Číslo portu IO od 1 do 4.</param>
/// <param name="Value">Příznak úspěchu zápisu či nastavení, nebo úroveň pinu při čtení.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200IoResult(
    R200IoOperation Operation,
    byte Port,
    bool Value,
    R200Frame RawFrame)
{
    /// <summary>Získá úspěšnost operace nastavení či zápisu, nebo hodnotu null pro čtení.</summary>
    public bool? Succeeded => Operation == R200IoOperation.ReadLevel ? null : Value;

    /// <summary>Získá vstupní úroveň při čtení, nebo hodnotu null pro nastavení a zápis.</summary>
    public bool? LevelHigh => Operation == R200IoOperation.ReadLevel ? Value : null;
}

/// <summary>Obsahuje 64bitový kód vrácený funkcí NXP EAS Alarm.</summary>
/// <param name="AlarmCode">Kód alarmu jako celé číslo bez znaménka.</param>
/// <param name="AlarmCodeBytes">Bajty kódu alarmu v pořadí přenosu.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200EasAlarmResult(ulong AlarmCode, byte[] AlarmCodeBytes, R200Frame RawFrame);

/// <summary>Obsahuje NXP Config-Word vrácený po operaci ChangeConfig.</summary>
/// <param name="Tag">Dotčený tag.</param>
/// <param name="ConfigWord">Aktuální Config-Word.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200ChangeConfigResult(
    R200TagIdentity Tag,
    ushort ConfigWord,
    R200Frame RawFrame);

/// <summary>Obsahuje zdokumentovaný stav QT a případná doplňková data firmwaru.</summary>
/// <param name="Tag">Dotčený tag Impinj.</param>
/// <param name="Status">Stavový bajt QT uvedený v odpovědi V2.3.3.</param>
/// <param name="ResponseData">Případné doplňkové bajty odpovědi QT.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200QtResult(
    R200TagIdentity Tag,
    byte Status,
    byte[] ResponseData,
    R200Frame RawFrame);

/// <summary>Obsahuje načtený stav nebo výsledek uzamčení BlockPermalock.</summary>
/// <param name="Tag">Dotčený tag.</param>
/// <param name="BlockRange">Počet reprezentovaných rozsahů po 16 blocích.</param>
/// <param name="LockStatus">Maska stavu uzamčení vrácená při čtení.</param>
/// <param name="Status">Stavový bajt operace uzamčení.</param>
/// <param name="RawFrame">Původní rámec odpovědi.</param>
public sealed record R200BlockPermalockResult(
    R200TagIdentity Tag,
    byte BlockRange,
    byte[] LockStatus,
    byte? Status,
    R200Frame RawFrame);

/// <summary>Zapouzdřuje 20bitová data příkazu Gen2 Lock.</summary>
public sealed record R200LockPayload
{
    /// <summary>Vytvoří data uzamčení z již sbalené 20bitové hodnoty.</summary>
    public R200LockPayload(uint value)
    {
        if (value > 0x0F_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Lock payload is limited to 20 bits.");
        }

        Value = value;
    }

    /// <summary>Získá sbalená 20bitová data.</summary>
    public uint Value { get; }

    /// <summary>
    /// Sestaví pole masky a akce. Akce s hodnotou null ponechají příslušnou oblast paměti beze změny.
    /// </summary>
    public static R200LockPayload FromActions(
        R200LockAction? killPassword = null,
        R200LockAction? accessPassword = null,
        R200LockAction? epcMemory = null,
        R200LockAction? tidMemory = null,
        R200LockAction? userMemory = null)
    {
        uint mask = 0;
        uint action = 0;
        // Každé oblasti paměti patří dva bity masky a odpovídající dva bity akce.
        Add(killPassword, 8, ref mask, ref action);
        Add(accessPassword, 6, ref mask, ref action);
        Add(epcMemory, 4, ref mask, ref action);
        Add(tidMemory, 2, ref mask, ref action);
        Add(userMemory, 0, ref mask, ref action);
        return new R200LockPayload((mask << 10) | action);
    }

    private static void Add(R200LockAction? value, int shift, ref uint mask, ref uint action)
    {
        if (value is null)
        {
            return;
        }

        mask |= 0b11u << shift;
        action |= (uint)value.Value << shift;
    }
}
