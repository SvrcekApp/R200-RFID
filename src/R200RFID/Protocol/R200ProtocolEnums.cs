namespace R200RFID;

/// <summary>Určuje směr a účel rámce R200.</summary>
public enum R200FrameType : byte
{
    /// <summary>Příkaz odeslaný z hostitele do modulu.</summary>
    Command = 0x00,
    /// <summary>Přímá odpověď odeslaná z modulu do hostitele.</summary>
    Response = 0x01,
    /// <summary>Asynchronní oznámení, které obvykle obsahuje načtený tag.</summary>
    Notification = 0x02
}

/// <summary>Obsahuje všechny kódy příkazů zdokumentované protokolem R200 V2.3.3.</summary>
public enum R200Command : byte
{
    /// <summary>Načte informace o hardwaru, softwaru nebo výrobci.</summary>
    GetModuleInformation = 0x03,
    /// <summary>Zapne nebo ukončí režim RF IDLE.</summary>
    IdleMode = 0x04,
    /// <summary>Nastaví regulační region.</summary>
    SetRegion = 0x07,
    /// <summary>Načte regulační region.</summary>
    GetRegion = 0x08,
    /// <summary>Načte aktuální parametry Select.</summary>
    GetSelect = 0x0B,
    /// <summary>Nastaví parametry Select.</summary>
    SetSelect = 0x0C,
    /// <summary>Načte parametry Gen2 Query.</summary>
    GetQuery = 0x0D,
    /// <summary>Nastaví parametry Gen2 Query.</summary>
    SetQuery = 0x0E,
    /// <summary>Změní přenosovou rychlost sériové linky.</summary>
    SetBaudRate = 0x11,
    /// <summary>Řídí, kdy se odesílá Select.</summary>
    SetSelectMode = 0x12,
    /// <summary>Uvede modul do úsporného režimu spánku.</summary>
    Sleep = 0x17,
    /// <summary>Nastaví nebo obslouží vstupy a výstupy IO1 až IO4.</summary>
    ControlIo = 0x1A,
    /// <summary>Nastaví prodlevu automatického uspání.</summary>
    SetIdleSleepTime = 0x1D,
    /// <summary>Provede jednu operaci inventory.</summary>
    InventoryOnce = 0x22,
    /// <summary>Provede požadovaný počet operací inventory.</summary>
    InventoryMultiple = 0x27,
    /// <summary>Zastaví vícenásobné inventory.</summary>
    StopInventory = 0x28,
    /// <summary>Načte wordy z paměťové banky tagu.</summary>
    ReadTag = 0x39,
    /// <summary>Zapíše wordy do paměťové banky tagu.</summary>
    WriteTag = 0x49,
    /// <summary>Trvale zničí tag.</summary>
    KillTag = 0x65,
    /// <summary>Změní stav uzamčení paměti tagu.</summary>
    LockTag = 0x82,
    /// <summary>Nahradí nebo vymaže vlastní seznam kanálů pro přeskakování frekvencí.</summary>
    InsertWorkingChannels = 0xA9,
    /// <summary>Načte index pevného pracovního kanálu.</summary>
    GetWorkingChannel = 0xAA,
    /// <summary>Nastaví index pevného pracovního kanálu.</summary>
    SetWorkingChannel = 0xAB,
    /// <summary>Zapne nebo vypne automatické přeskakování frekvencí.</summary>
    SetAutomaticFrequencyHopping = 0xAD,
    /// <summary>Zapne nebo vypne nepřetržitý výstup RF nosné.</summary>
    SetContinuousCarrier = 0xB0,
    /// <summary>Nastaví vysílací výkon RF.</summary>
    SetTransmitPower = 0xB6,
    /// <summary>Načte vysílací výkon RF.</summary>
    GetTransmitPower = 0xB7,
    /// <summary>Načte nebo zapíše data BlockPermalock.</summary>
    BlockPermalock = 0xD3,
    /// <summary>Kód odpovědi použitý po úspěšném uzamčení pomocí BlockPermalock.</summary>
    BlockPermalockLockResponse = 0xD4,
    /// <summary>Načte nebo změní NXP Config-Word.</summary>
    NxpChangeConfig = 0xE0,
    /// <summary>Provede NXP ReadProtect nebo Reset ReadProtect.</summary>
    NxpReadProtect = 0xE1,
    /// <summary>Kód odpovědi použitý po NXP Reset ReadProtect.</summary>
    NxpResetReadProtectResponse = 0xE2,
    /// <summary>Změní bit NXP EAS PSF.</summary>
    NxpChangeEas = 0xE3,
    /// <summary>Vyžádá 64bitový kód alarmu NXP EAS.</summary>
    NxpEasAlarm = 0xE4,
    /// <summary>Provede operaci Impinj Monza QT.</summary>
    ImpinjMonzaQt = 0xE5,
    /// <summary>Kód odpovědi používaný operací Impinj Monza QT.</summary>
    ImpinjMonzaQtResponse = 0xE6,
    /// <summary>Nastaví parametry demodulátoru přijímače.</summary>
    SetDemodulator = 0xF0,
    /// <summary>Načte parametry demodulátoru přijímače.</summary>
    GetDemodulator = 0xF1,
    /// <summary>Prohledá jednotlivé kanály a změří výkon RF rušení.</summary>
    ScanJammer = 0xF2,
    /// <summary>Prohledá jednotlivé kanály a změří vstupní RSSI.</summary>
    ScanRssi = 0xF3,
    /// <summary>Společný kód odpovědi pro neúspěšný příkaz.</summary>
    Error = 0xFF
}

/// <summary>Volí informační řetězec modulu vracený příkazem 0x03.</summary>
public enum R200ModuleInformationType : byte
{
    /// <summary>Verze hardwaru.</summary>
    HardwareVersion = 0x00,
    /// <summary>Verze firmwaru nebo softwaru.</summary>
    SoftwareVersion = 0x01,
    /// <summary>Název výrobce.</summary>
    Manufacturer = 0x02
}

/// <summary>Určuje paměťovou banku tagu Gen2.</summary>
public enum R200MemoryBank : byte
{
    /// <summary>Vyhrazená banka obsahující hesla.</summary>
    Reserved = 0x00,
    /// <summary>Banka EPC.</summary>
    Epc = 0x01,
    /// <summary>Banka identifikátoru tagu.</summary>
    Tid = 0x02,
    /// <summary>Uživatelská paměťová banka.</summary>
    User = 0x03
}

/// <summary>Určuje jeden z regulačních regionů zdokumentovaných modulem.</summary>
public enum R200Region : byte
{
    /// <summary>Čínské pásmo 900 MHz.</summary>
    China900MHz = 0x01,
    /// <summary>Pásmo Spojených států.</summary>
    Usa = 0x02,
    /// <summary>Evropské pásmo.</summary>
    Europe = 0x03,
    /// <summary>Čínské pásmo 800 MHz.</summary>
    China800MHz = 0x04,
    /// <summary>Korejské pásmo.</summary>
    Korea = 0x06
}

/// <summary>Určuje, kterým operacím s tagy předchází příkaz Select.</summary>
public enum R200SelectMode : byte
{
    /// <summary>Odeslat Select před každou operací s tagem.</summary>
    BeforeEveryTagOperation = 0x00,
    /// <summary>Neodesílat Select automaticky.</summary>
    Disabled = 0x01,
    /// <summary>Odeslat Select pouze před operacemi s tagem mimo inventory.</summary>
    BeforeNonInventoryTagOperations = 0x02
}

/// <summary>Pole poměru dělení příkazu Gen2 Query.</summary>
public enum R200QueryDivideRatio : byte
{
    /// <summary>Poměr dělení 8; dokumentace jej uvádí jako podporovaný modulem.</summary>
    Dr8 = 0,
    /// <summary>Poměr dělení 64/3; protokol jej kóduje, ale dokumentace uvádí, že není podporovaný.</summary>
    Dr64Over3 = 1
}

/// <summary>Hodnota kódování přenosu z tagu do čtečky v Gen2 Query.</summary>
public enum R200QueryM : byte
{
    /// <summary>FM0/M=1; dokumentace jej uvádí jako podporovaný modulem.</summary>
    M1 = 0,
    /// <summary>M=2.</summary>
    M2 = 1,
    /// <summary>M=4.</summary>
    M4 = 2,
    /// <summary>M=8.</summary>
    M8 = 3
}

/// <summary>Pole výběru v Gen2 Query.</summary>
public enum R200QuerySelection : byte
{
    /// <summary>První kódování označující všechny tagy.</summary>
    All0 = 0,
    /// <summary>Druhé kódování označující všechny tagy.</summary>
    All1 = 1,
    /// <summary>Tagy, jejichž příznak SL není aktivní.</summary>
    NotSelected = 2,
    /// <summary>Tagy, jejichž příznak SL je aktivní.</summary>
    Selected = 3
}

/// <summary>Relace inventory Gen2.</summary>
public enum R200QuerySession : byte
{
    /// <summary>Relace 0.</summary>
    S0 = 0,
    /// <summary>Relace 1.</summary>
    S1 = 1,
    /// <summary>Relace 2.</summary>
    S2 = 2,
    /// <summary>Relace 3.</summary>
    S3 = 3
}

/// <summary>Cílový příznak inventory Gen2.</summary>
public enum R200QueryTarget : byte
{
    /// <summary>Cílový příznak A.</summary>
    A = 0,
    /// <summary>Cílový příznak B.</summary>
    B = 1
}

/// <summary>Dvoubitová akce použitá v datech příkazu Gen2 Lock.</summary>
public enum R200LockAction : byte
{
    /// <summary>Otevřené nebo zapisovatelné v otevřeném i zabezpečeném stavu.</summary>
    Open = 0,
    /// <summary>Trvale otevřené.</summary>
    PermanentlyOpen = 1,
    /// <summary>Uzamčené v otevřeném stavu, ale přístupné v zabezpečeném stavu.</summary>
    Locked = 2,
    /// <summary>Trvale uzamčené.</summary>
    PermanentlyLocked = 3
}

/// <summary>Operace vstupu či výstupu kódovaná příkazem 0x1A.</summary>
public enum R200IoOperation : byte
{
    /// <summary>Nastaví pin IO jako vstup nebo výstup.</summary>
    SetDirection = 0x00,
    /// <summary>Nastaví na výstupním pinu nízkou nebo vysokou úroveň.</summary>
    SetLevel = 0x01,
    /// <summary>Načte úroveň vstupního pinu.</summary>
    ReadLevel = 0x02
}

/// <summary>Volí režim čtení nebo zápisu u příkazů podporujících oba režimy.</summary>
public enum R200ReadWrite : byte
{
    /// <summary>Načíst aktuální data.</summary>
    Read = 0x00,
    /// <summary>Zapsat nová data.</summary>
    Write = 0x01
}

/// <summary>Volí dočasné nebo trvalé úložiště Impinj QT.</summary>
public enum R200Persistence : byte
{
    /// <summary>Zapsat pouze dočasný stav.</summary>
    Volatile = 0x00,
    /// <summary>Zapsat trvalý stav.</summary>
    NonVolatile = 0x01
}

/// <summary>Hodnoty typů přenosové rychlosti uvedené v dokumentu V2.3.3.</summary>
public enum R200BaudRateType : byte
{
    /// <summary>9600 bit/s.</summary>
    Baud9600 = 0xB0,
    /// <summary>19200 bit/s.</summary>
    Baud19200 = 0xB1,
    /// <summary>28800 bit/s.</summary>
    Baud28800 = 0xB2,
    /// <summary>38400 bit/s.</summary>
    Baud38400 = 0xB3,
    /// <summary>57600 bit/s.</summary>
    Baud57600 = 0xB4,
    /// <summary>115200 bit/s.</summary>
    Baud115200 = 0xB5
}
