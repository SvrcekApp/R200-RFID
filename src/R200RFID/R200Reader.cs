using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace R200RFID;

/// <summary>
/// Komunikuje s UHF RFID modulem R200 prostřednictvím sériového protokolu V2.3.3.
/// </summary>
public sealed class R200Reader : IDisposable, IAsyncDisposable
{
    private readonly SerialPort? _serialPort;
    private readonly Stream? _providedStream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    /// <summary>Vytvoří čtečku, která vlastní a spravuje sériový port.</summary>
    public R200Reader(R200SerialOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.PortName))
        {
            throw new ArgumentException("A serial port name is required.", nameof(options));
        }

        if (options.BaudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Baud rate must be positive.");
        }

        ValidateTimeout(options.CommandTimeout, nameof(options.CommandTimeout));
        ValidateTimeout(options.InventoryQuietPeriod, nameof(options.InventoryQuietPeriod));

        _serialPort = new SerialPort(
            options.PortName,
            options.BaudRate,
            options.Parity,
            options.DataBits,
            options.StopBits)
        {
            Handshake = options.Handshake,
            DtrEnable = options.DtrEnable,
            RtsEnable = options.RtsEnable
        };

        CommandTimeout = options.CommandTimeout;
        InventoryQuietPeriod = options.InventoryQuietPeriod;
    }

    /// <summary>
    /// Vytvoří čtečku nad obousměrným proudem poskytnutým volajícím, což je vhodné pro vlastní přenosy a testy.
    /// </summary>
    public R200Reader(
        Stream stream,
        bool leaveOpen = false,
        TimeSpan? commandTimeout = null,
        TimeSpan? inventoryQuietPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The transport stream must be readable and writable.", nameof(stream));
        }

        _providedStream = stream;
        _leaveOpen = leaveOpen;
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(2);
        InventoryQuietPeriod = inventoryQuietPeriod ?? TimeSpan.FromMilliseconds(250);
        ValidateTimeout(CommandTimeout, nameof(commandTimeout));
        ValidateTimeout(InventoryQuietPeriod, nameof(inventoryQuietPeriod));
    }

    /// <summary>Získá nebo nastaví maximální dobu čekání na běžný rámec odpovědi.</summary>
    public TimeSpan CommandTimeout { get; set; }

    /// <summary>
    /// Získá nebo nastaví interval bez rámce, po kterém se shromážděná dávka inventory považuje za dokončenou.
    /// </summary>
    public TimeSpan InventoryQuietPeriod { get; set; }

    /// <summary>Získá informaci, zda je nastavený přenos dostupný pro komunikaci.</summary>
    public bool IsOpen => _serialPort?.IsOpen ?? !_disposed;

    /// <summary>Získá název sériového portu, nebo hodnotu null při použití vlastního proudu.</summary>
    public string? PortName => _serialPort?.PortName;

    /// <summary>Získá aktuální přenosovou rychlost sériového portu, nebo hodnotu null při použití vlastního proudu.</summary>
    public int? BaudRate => _serialPort?.BaudRate;

    /// <summary>Otevře vlastněný sériový port. Při použití dodaného proudu neprovede žádnou akci.</summary>
    public void Open()
    {
        ThrowIfDisposed();
        if (_serialPort is { IsOpen: false })
        {
            _serialPort.Open();
        }
    }

    /// <summary>Zavře vlastněný sériový port, aniž by uvolnila čtečku.</summary>
    public void Close()
    {
        if (_serialPort is { IsOpen: true })
        {
            _serialPort.Close();
        }
    }

    /// <summary>Odešle zdokumentovaný příkaz s libovolnými daty a počká na odpověď se stejným kódem příkazu.</summary>
    public async Task<R200Frame> SendCommandAsync(
        R200Command command,
        ReadOnlyMemory<byte> parameters = default,
        CancellationToken cancellationToken = default) =>
        await ExecuteCommandAsync((byte)command, parameters, [(byte)command], cancellationToken).ConfigureAwait(false);

    /// <summary>Odešle příkaz, aniž by čekala na odpověď.</summary>
    public async Task WriteCommandAsync(
        R200Command command,
        ReadOnlyMemory<byte> parameters = default,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameCoreAsync((byte)command, parameters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Načte a ověří jeden úplný rámec z přenosu.</summary>
    public async Task<R200Frame> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadFrameCoreAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Naparsuje jedno oznámení inventory získané metodou <see cref="ReadFrameAsync"/>.</summary>
    public static R200InventoryTag ParseInventoryNotification(R200Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type != R200FrameType.Notification ||
            (frame.Command != (byte)R200Command.InventoryOnce &&
             frame.Command != (byte)R200Command.InventoryMultiple))
        {
            throw new ArgumentException("The frame is not an R200 inventory notification.", nameof(frame));
        }

        return ParseInventoryTag(frame);
    }

    /// <summary>Naparsuje jednu chybovou odpověď příkazu 0xFF získanou metodou <see cref="ReadFrameAsync"/>.</summary>
    public static R200Failure ParseErrorResponse(R200Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type != R200FrameType.Response || frame.Command != (byte)R200Command.Error)
        {
            throw new ArgumentException("The frame is not an R200 error response.", nameof(frame));
        }

        return ParseFailure(frame);
    }

    /// <summary>Načte požadovaný informační řetězec modulu ve formátu ASCII.</summary>
    public async Task<R200ModuleInformation> GetModuleInformationAsync(
        R200ModuleInformationType informationType,
        CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetModuleInformation,
            new[] { (byte)informationType },
            [(byte)R200Command.GetModuleInformation],
            cancellationToken).ConfigureAwait(false);

        RequirePayloadLength(frame, 1);
        var returnedType = (R200ModuleInformationType)frame.Payload[0];
        if (returnedType != informationType)
        {
            throw new InvalidDataException(
                $"Module information response returned type 0x{(byte)returnedType:X2}; expected 0x{(byte)informationType:X2}.");
        }

        var value = Encoding.ASCII.GetString(frame.Payload, 1, frame.Payload.Length - 1).TrimEnd('\0');
        return new R200ModuleInformation(returnedType, value, frame);
    }

    /// <summary>
    /// Provede jeden dotazovací příkaz a shromažďuje oznámení o tazích, dokud komunikace neutichne.
    /// </summary>
    public Task<R200InventoryBatch> InventoryOnceAsync(
        TimeSpan? quietPeriod = null,
        CancellationToken cancellationToken = default) =>
        InventoryCoreAsync(R200Command.InventoryOnce, ReadOnlyMemory<byte>.Empty, quietPeriod, cancellationToken);

    /// <summary>
    /// Provede požadovaný počet dotazovacích cyklů a shromažďuje oznámení, dokud komunikace neutichne.
    /// </summary>
    public Task<R200InventoryBatch> InventoryMultipleAsync(
        ushort pollingCount,
        TimeSpan? quietPeriod = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[3];
        payload[0] = 0x22;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), pollingCount);
        return InventoryCoreAsync(R200Command.InventoryMultiple, payload, quietPeriod, cancellationToken);
    }

    /// <summary>Spustí vícenásobné inventory bez převzetí čtení následných oznámení.</summary>
    public Task StartMultipleInventoryAsync(
        ushort pollingCount,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[3];
        payload[0] = 0x22;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), pollingCount);
        return WriteCommandAsync(R200Command.InventoryMultiple, payload, cancellationToken);
    }

    /// <summary>Zastaví dříve spuštěnou operaci vícenásobného inventory.</summary>
    public async Task StopMultipleInventoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateTimeout(CommandTimeout, nameof(CommandTimeout));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameCoreAsync(
                (byte)R200Command.StopInventory,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken).ConfigureAwait(false);

            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                var remaining = CommandTimeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No complete R200 frame was received within {CommandTimeout}.");
                }

                var frame = await ReadFrameCoreAsync(remaining, cancellationToken).ConfigureAwait(false);
                if (frame.Type != R200FrameType.Response)
                {
                    continue;
                }

                if (frame.Command == (byte)R200Command.Error)
                {
                    var failure = ParseFailure(frame);
                    if (failure.ErrorCode == 0x15)
                    {
                        // Modul může ještě odeslat výsledek posledního inventory cyklu bez tagu.
                        continue;
                    }

                    throw new R200CommandException(failure);
                }

                if (frame.Command == (byte)R200Command.StopInventory)
                {
                    EnsureStatus(frame, 0x00);
                    return;
                }
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Zapíše úplný vektor Gen2 Select používaný k výběru jednoho nebo více tagů.</summary>
    public async Task SetSelectAsync(
        R200SelectParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateSelect(parameters);

        var payload = new byte[7 + parameters.Mask.Length];
        // SelParam sdružuje Target (3 bity), Action (3 bity) a MemoryBank (2 bity).
        payload[0] = (byte)((parameters.Target << 5) | (parameters.Action << 2) | (byte)parameters.MemoryBank);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), parameters.BitPointer);
        payload[5] = parameters.MaskBitLength;
        payload[6] = parameters.Truncate ? (byte)0x80 : (byte)0x00;
        parameters.Mask.CopyTo(payload, 7);

        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetSelect,
            payload,
            [(byte)R200Command.SetSelect],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Načte aktuálně uložený vektor Select.</summary>
    public async Task<R200SelectParameters> GetSelectAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetSelect,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.GetSelect],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 7);

        var select = frame.Payload[0];
        return new R200SelectParameters(
            (byte)((select >> 5) & 0x07),
            (byte)((select >> 2) & 0x07),
            (R200MemoryBank)(select & 0x03),
            BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(1, 4)),
            frame.Payload[5],
            frame.Payload[6] == 0x80,
            frame.Payload[7..],
            frame);
    }

    /// <summary>Určuje, které operace s tagy automaticky odesílají uložený vektor Select.</summary>
    public async Task SetSelectModeAsync(
        R200SelectMode mode,
        CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetSelectMode,
            new[] { (byte)mode },
            [(byte)R200Command.SetSelectMode, (byte)R200Command.SetSelect],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Načte zadaný počet 16bitových wordů z paměťové banky vybraného tagu.</summary>
    public async Task<R200TagReadResult> ReadTagAsync(
        uint accessPassword,
        R200MemoryBank memoryBank,
        ushort wordAddress,
        ushort wordCount,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(payload, accessPassword);
        payload[4] = (byte)memoryBank;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5, 2), wordAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(7, 2), wordCount);

        var frame = await ExecuteCommandAsync(
            (byte)R200Command.ReadTag,
            payload,
            [(byte)R200Command.ReadTag],
            cancellationToken).ConfigureAwait(false);
        var (tag, tail) = ParseTagAndTail(frame);
        return new R200TagReadResult(tag, tail, frame);
    }

    /// <summary>Zapíše až 32 wordů (64 bajtů) do paměťové banky vybraného tagu.</summary>
    public async Task<R200TagOperationResult> WriteTagAsync(
        uint accessPassword,
        R200MemoryBank memoryBank,
        ushort wordAddress,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.Length == 0 || (data.Length & 1) != 0)
        {
            throw new ArgumentException("Tag write data must contain a positive, even number of bytes.", nameof(data));
        }

        var wordCount = data.Length / 2;
        if (wordCount > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "The protocol limits one write to 32 words (64 bytes).");
        }

        var payload = new byte[9 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload, accessPassword);
        payload[4] = (byte)memoryBank;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5, 2), wordAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(7, 2), (ushort)wordCount);
        data.Span.CopyTo(payload.AsSpan(9));

        var frame = await ExecuteCommandAsync(
            (byte)R200Command.WriteTag,
            payload,
            [(byte)R200Command.WriteTag],
            cancellationToken).ConfigureAwait(false);
        return ParseTagOperation(frame);
    }

    /// <summary>Použije sbalená data Gen2 Lock na vybraný tag.</summary>
    public async Task<R200TagOperationResult> LockTagAsync(
        uint accessPassword,
        R200LockPayload lockPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockPayload);
        var payload = new byte[7];
        BinaryPrimitives.WriteUInt32BigEndian(payload, accessPassword);
        payload[4] = (byte)(lockPayload.Value >> 16);
        payload[5] = (byte)(lockPayload.Value >> 8);
        payload[6] = (byte)lockPayload.Value;

        var frame = await ExecuteCommandAsync(
            (byte)R200Command.LockTag,
            payload,
            [(byte)R200Command.LockTag],
            cancellationToken).ConfigureAwait(false);
        return ParseTagOperation(frame);
    }

    /// <summary>Trvale deaktivuje vybraný tag pomocí jeho hesla Kill Password.</summary>
    public async Task<R200TagOperationResult> KillTagAsync(
        uint killPassword,
        CancellationToken cancellationToken = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, killPassword);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.KillTag,
            payload,
            [(byte)R200Command.KillTag],
            cancellationToken).ConfigureAwait(false);
        return ParseTagOperation(frame);
    }

    /// <summary>
    /// Odešle příkaz 0x11 a znovu připojí vlastněný sériový port s novou přenosovou rychlostí. Příkaz nemá odpověď.
    /// </summary>
    public async Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (baudRate <= 0 || baudRate % 100 != 0 || baudRate / 100 > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baudRate),
                "The V2.3.3 command encodes the baud rate as an unsigned 16-bit baudRate/100 value.");
        }

        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)(baudRate / 100));

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameCoreAsync((byte)R200Command.SetBaudRate, payload, cancellationToken).ConfigureAwait(false);

            if (_serialPort is not null)
            {
                // Příkaz 0x11 nemá odpověď; port se ihned znovu připojí s nově zvolenou rychlostí.
                _serialPort.Close();
                _serialPort.BaudRate = baudRate;
                _serialPort.Open();
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Načte sbalené parametry Gen2 Query.</summary>
    public async Task<R200QueryParameters> GetQueryParametersAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetQuery,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.GetQuery],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 2, exact: true);
        return R200QueryParameters.FromUInt16(BinaryPrimitives.ReadUInt16BigEndian(frame.Payload), frame);
    }

    /// <summary>Zapíše parametry Gen2 Query podporované modulem V2.3.3.</summary>
    public async Task SetQueryParametersAsync(
        R200QueryParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.DivideRatio != R200QueryDivideRatio.Dr8 ||
            parameters.M != R200QueryM.M1 ||
            !parameters.UsePilotTone)
        {
            throw new ArgumentException(
                "The V2.3.3 document states that the module supports only DR=8, M=1 and pilot tone enabled.",
                nameof(parameters));
        }

        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, parameters.ToUInt16());
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetQuery,
            payload,
            [(byte)R200Command.SetQuery],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Nastaví regulační region RF.</summary>
    public async Task SetRegionAsync(R200Region region, CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetRegion,
            new[] { (byte)region },
            [(byte)R200Command.SetRegion],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Načte aktuální regulační region RF.</summary>
    public async Task<R200Region> GetRegionAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetRegion,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.GetRegion],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 1, exact: true);
        return (R200Region)frame.Payload[0];
    }

    /// <summary>Nastaví index pracovního kanálu specifický pro region.</summary>
    public async Task SetWorkingChannelAsync(byte channelIndex, CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetWorkingChannel,
            new[] { channelIndex },
            [(byte)R200Command.SetWorkingChannel],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Načte aktuální index pracovního kanálu specifický pro region.</summary>
    public async Task<byte> GetWorkingChannelAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetWorkingChannel,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.GetWorkingChannel],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 1, exact: true);
        return frame.Payload[0];
    }

    /// <summary>Převede zdokumentovaný region a index kanálu na frekvenci v MHz.</summary>
    public static decimal GetChannelFrequencyMHz(R200Region region, byte channelIndex)
    {
        var (start, step) = GetRegionChannelFormula(region);
        return start + step * channelIndex;
    }

    /// <summary>Převede přesnou zdokumentovanou frekvenci kanálu na její jednobajtový index.</summary>
    public static byte GetChannelIndex(R200Region region, decimal frequencyMHz)
    {
        var (start, step) = GetRegionChannelFormula(region);
        var raw = (frequencyMHz - start) / step;
        if (raw < 0 || raw > byte.MaxValue || raw != decimal.Truncate(raw))
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequencyMHz),
                "Frequency does not map exactly to a one-byte channel index for the selected region.");
        }

        return (byte)raw;
    }

    /// <summary>Převede typ přenosové rychlosti uvedený v PDF na bity za sekundu.</summary>
    public static int GetBaudRate(R200BaudRateType type) => type switch
    {
        R200BaudRateType.Baud9600 => 9600,
        R200BaudRateType.Baud19200 => 19200,
        R200BaudRateType.Baud28800 => 28800,
        R200BaudRateType.Baud38400 => 38400,
        R200BaudRateType.Baud57600 => 57600,
        R200BaudRateType.Baud115200 => 115200,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown documented baud-rate type.")
    };

    /// <summary>Převede jednu z uvedených přenosových rychlostí na její zdokumentovaný bajt typu.</summary>
    public static R200BaudRateType GetBaudRateType(int baudRate) => baudRate switch
    {
        9600 => R200BaudRateType.Baud9600,
        19200 => R200BaudRateType.Baud19200,
        28800 => R200BaudRateType.Baud28800,
        38400 => R200BaudRateType.Baud38400,
        57600 => R200BaudRateType.Baud57600,
        115200 => R200BaudRateType.Baud115200,
        _ => throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, "Baud rate is not listed in the V2.3.3 baud-rate table.")
    };

    /// <summary>Zapne nebo vypne automatické přeskakování frekvencí.</summary>
    public async Task SetAutomaticFrequencyHoppingAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetAutomaticFrequencyHopping,
            new[] { enabled ? (byte)0xFF : (byte)0x00 },
            [(byte)R200Command.SetAutomaticFrequencyHopping],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Nastaví vlastní seznam přeskakování frekvencí, nebo jej vymaže, pokud je kolekce prázdná.</summary>
    public async Task SetFrequencyHoppingChannelsAsync(
        IReadOnlyCollection<byte> channelIndexes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIndexes);
        if (channelIndexes.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndexes), "The channel count is one byte.");
        }

        var payload = new byte[channelIndexes.Count + 1];
        payload[0] = (byte)channelIndexes.Count;
        var channelOffset = 1;
        foreach (var channelIndex in channelIndexes)
        {
            payload[channelOffset++] = channelIndex;
        }
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.InsertWorkingChannels,
            payload,
            [(byte)R200Command.InsertWorkingChannels],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Načte vysílací výkon a převede setiny dBm na dBm.</summary>
    public async Task<decimal> GetTransmitPowerDbmAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetTransmitPower,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.GetTransmitPower],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 2, exact: true);
        return BinaryPrimitives.ReadUInt16BigEndian(frame.Payload) / 100m;
    }

    /// <summary>Nastaví vysílací výkon s přesností na setiny dBm.</summary>
    public async Task SetTransmitPowerDbmAsync(decimal powerDbm, CancellationToken cancellationToken = default)
    {
        var scaled = powerDbm * 100m;
        if (scaled < 0 || scaled > ushort.MaxValue || scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentOutOfRangeException(
                nameof(powerDbm),
                "Transmit power must be representable as an unsigned 16-bit value in hundredths of dBm.");
        }

        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)scaled);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetTransmitPower,
            payload,
            [(byte)R200Command.SetTransmitPower],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Zapne nebo vypne nepřetržitý výstup RF nosné.</summary>
    public async Task SetContinuousCarrierAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetContinuousCarrier,
            new[] { enabled ? (byte)0xFF : (byte)0x00 },
            [(byte)R200Command.SetContinuousCarrier],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Načte zesílení směšovače, zesílení mezifrekvence a práh demodulace.</summary>
    public async Task<R200DemodulatorParameters> GetDemodulatorParametersAsync(
        CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.GetDemodulator,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.GetDemodulator],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 4, exact: true);
        return new R200DemodulatorParameters(
            frame.Payload[0],
            frame.Payload[1],
            BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(2, 2)));
    }

    /// <summary>Nastaví zesílení směšovače, zesílení mezifrekvence a práh demodulace.</summary>
    public async Task SetDemodulatorParametersAsync(
        R200DemodulatorParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.MixerGainCode > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Mixer gain code must be 0x00 through 0x06.");
        }

        if (parameters.IfGainCode > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "IF gain code must be 0x00 through 0x07.");
        }

        var payload = new byte[4];
        payload[0] = parameters.MixerGainCode;
        payload[1] = parameters.IfGainCode;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), parameters.Threshold);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetDemodulator,
            payload,
            [(byte)R200Command.SetDemodulator],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Změří výkon RF rušení na všech kanálech aktuálního regionu.</summary>
    public Task<R200ChannelScanResult> ScanJammerAsync(CancellationToken cancellationToken = default) =>
        ScanChannelsAsync(R200Command.ScanJammer, cancellationToken);

    /// <summary>Změří vstupní RSSI na všech kanálech aktuálního regionu.</summary>
    public Task<R200ChannelScanResult> ScanRssiAsync(CancellationToken cancellationToken = default) =>
        ScanChannelsAsync(R200Command.ScanRssi, cancellationToken);

    /// <summary>Nastaví jeden z pinů IO1 až IO4 jako vstup nebo výstup.</summary>
    public Task<R200IoResult> ConfigureIoDirectionAsync(
        byte port,
        bool output,
        CancellationToken cancellationToken = default) =>
        ExecuteIoAsync(R200IoOperation.SetDirection, port, output, cancellationToken);

    /// <summary>Nastaví na výstupním pinu nízkou nebo vysokou úroveň.</summary>
    public Task<R200IoResult> SetIoLevelAsync(
        byte port,
        bool high,
        CancellationToken cancellationToken = default) =>
        ExecuteIoAsync(R200IoOperation.SetLevel, port, high, cancellationToken);

    /// <summary>Načte, zda je na pinu IO nízká nebo vysoká úroveň.</summary>
    public Task<R200IoResult> ReadIoLevelAsync(byte port, CancellationToken cancellationToken = default) =>
        ExecuteIoAsync(R200IoOperation.ReadLevel, port, false, cancellationToken);

    /// <summary>Uvede modul do úsporného režimu spánku.</summary>
    public async Task SleepAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.Sleep,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.Sleep],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>
    /// Zapíše jediný probouzecí bajt, který spící modul záměrně zahodí.
    /// </summary>
    public async Task WakeAsync(byte wakeByte = 0x00, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = GetStream();
            await stream.WriteAsync(new[] { wakeByte }, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Nastaví automatické uspání po 1 až 30 minutách nečinnosti, nebo jej nulou vypne.</summary>
    public async Task SetIdleSleepTimeAsync(byte minutes, CancellationToken cancellationToken = default)
    {
        if (minutes > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "Idle sleep time must be 0 (disabled) or 1 through 30 minutes.");
        }

        var frame = await ExecuteCommandAsync(
            (byte)R200Command.SetIdleSleepTime,
            new[] { minutes },
            [(byte)R200Command.SetIdleSleepTime],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, minutes);
    }

    /// <summary>Zapne nebo ukončí úsporný režim RF IDLE bez ztráty komunikace.</summary>
    public async Task SetIdleModeAsync(
        bool enterIdleMode,
        byte idleTime,
        CancellationToken cancellationToken = default)
    {
        var payload = new[] { enterIdleMode ? (byte)0x01 : (byte)0x00, (byte)0x01, idleTime };
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.IdleMode,
            payload,
            [(byte)R200Command.IdleMode],
            cancellationToken).ConfigureAwait(false);
        EnsureStatus(frame, 0x00);
    }

    /// <summary>Provede NXP ReadProtect nebo Reset ReadProtect na vybraném tagu.</summary>
    public async Task<R200TagOperationResult> SetNxpReadProtectAsync(
        uint accessPassword,
        bool reset,
        CancellationToken cancellationToken = default)
    {
        var payload = CreatePasswordPayload(accessPassword, 1);
        payload[4] = reset ? (byte)0x01 : (byte)0x00;
        var expected = reset
            ? (byte)R200Command.NxpResetReadProtectResponse
            : (byte)R200Command.NxpReadProtect;
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.NxpReadProtect,
            payload,
            [expected],
            cancellationToken).ConfigureAwait(false);
        return ParseTagOperation(frame);
    }

    /// <summary>Nastaví nebo vynuluje bit NXP EAS PSF na vybraném tagu.</summary>
    public async Task<R200TagOperationResult> ChangeNxpEasAsync(
        uint accessPassword,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var payload = CreatePasswordPayload(accessPassword, 1);
        payload[4] = enabled ? (byte)0x01 : (byte)0x00;
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.NxpChangeEas,
            payload,
            [(byte)R200Command.NxpChangeEas],
            cancellationToken).ConfigureAwait(false);
        return ParseTagOperation(frame);
    }

    /// <summary>Načte 64bitový kód alarmu NXP EAS z tagu s nastaveným bitem PSF.</summary>
    public async Task<R200EasAlarmResult> ReadNxpEasAlarmAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.NxpEasAlarm,
            ReadOnlyMemory<byte>.Empty,
            [(byte)R200Command.NxpEasAlarm],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 8, exact: true);
        return new R200EasAlarmResult(
            BinaryPrimitives.ReadUInt64BigEndian(frame.Payload),
            frame.Payload.ToArray(),
            frame);
    }

    /// <summary>Načte nebo přepne bity v Config-Word vybraného tagu NXP.</summary>
    public async Task<R200ChangeConfigResult> ChangeNxpConfigAsync(
        uint accessPassword,
        ushort configWord,
        CancellationToken cancellationToken = default)
    {
        var payload = CreatePasswordPayload(accessPassword, 2);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), configWord);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.NxpChangeConfig,
            payload,
            [(byte)R200Command.NxpChangeConfig],
            cancellationToken).ConfigureAwait(false);
        var (tag, tail) = ParseTagAndTail(frame);
        if (tail.Length != 2)
        {
            throw new InvalidDataException("ChangeConfig response must contain a two-byte Config-Word.");
        }

        return new R200ChangeConfigResult(tag, BinaryPrimitives.ReadUInt16BigEndian(tail), frame);
    }

    /// <summary>Provede operaci čtení nebo zápisu Impinj Monza 4 QT.</summary>
    public async Task<R200QtResult> ExecuteImpinjMonzaQtAsync(
        uint accessPassword,
        R200ReadWrite operation,
        R200Persistence persistence,
        ushort payloadValue,
        CancellationToken cancellationToken = default)
    {
        var payload = CreatePasswordPayload(accessPassword, 4);
        payload[4] = (byte)operation;
        payload[5] = (byte)persistence;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), payloadValue);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.ImpinjMonzaQt,
            payload,
            [(byte)R200Command.ImpinjMonzaQtResponse],
            cancellationToken).ConfigureAwait(false);
        var (tag, tail) = ParseTagAndTail(frame);
        if (tail.Length == 0)
        {
            throw new InvalidDataException("QT response does not contain a status or data byte.");
        }

        return new R200QtResult(tag, tail[0], tail[1..], frame);
    }

    /// <summary>Načte stav trvalého uzamčení z paměti User vybraného tagu.</summary>
    public async Task<R200BlockPermalockResult> ReadBlockPermalockAsync(
        uint accessPassword,
        ushort blockPointer,
        byte blockRange,
        CancellationToken cancellationToken = default)
    {
        if (blockRange == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockRange), "Block range must be positive.");
        }

        var payload = CreateBlockPermalockPayload(accessPassword, false, blockPointer, blockRange, null);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.BlockPermalock,
            payload,
            [(byte)R200Command.BlockPermalock],
            cancellationToken).ConfigureAwait(false);
        var (tag, tail) = ParseTagAndTail(frame);
        if (tail.Length < 2)
        {
            throw new InvalidDataException("BlockPermalock read response must contain BlockRange and lock status.");
        }

        return new R200BlockPermalockResult(tag, tail[0], tail[1..], null, frame);
    }

    /// <summary>Trvale uzamkne bloky v paměti User vybraného tagu.</summary>
    public async Task<R200BlockPermalockResult> LockBlockPermalockAsync(
        uint accessPassword,
        ushort blockPointer,
        byte blockRange,
        ushort mask,
        CancellationToken cancellationToken = default)
    {
        if (blockRange == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockRange), "Block range must be positive.");
        }

        var payload = CreateBlockPermalockPayload(accessPassword, true, blockPointer, blockRange, mask);
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.BlockPermalock,
            payload,
            [(byte)R200Command.BlockPermalockLockResponse],
            cancellationToken).ConfigureAwait(false);
        var (tag, tail) = ParseTagAndTail(frame);
        if (tail.Length == 0)
        {
            throw new InvalidDataException("BlockPermalock lock response does not contain a status byte.");
        }

        return new R200BlockPermalockResult(tag, blockRange, Array.Empty<byte>(), tail[0], frame);
    }

    /// <summary>Zavře a uvolní vlastněný přenos.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serialPort?.Dispose();
        if (!_leaveOpen)
        {
            _providedStream?.Dispose();
        }

        _ioLock.Dispose();
    }

    /// <summary>Asynchronně uvolní dodaný proud a prostředky přenosu.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _serialPort?.Dispose();
        if (!_leaveOpen && _providedStream is not null)
        {
            await _providedStream.DisposeAsync().ConfigureAwait(false);
        }

        _ioLock.Dispose();
    }

    private async Task<R200InventoryBatch> InventoryCoreAsync(
        R200Command command,
        ReadOnlyMemory<byte> payload,
        TimeSpan? quietPeriod,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var effectiveQuietPeriod = quietPeriod ?? InventoryQuietPeriod;
        ValidateTimeout(effectiveQuietPeriod, nameof(quietPeriod));
        var tags = new List<R200InventoryTag>();
        var frames = new List<R200Frame>();
        R200Failure? failure = null;

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameCoreAsync((byte)command, payload, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                R200Frame frame;
                try
                {
                    frame = await ReadFrameCoreAsync(effectiveQuietPeriod, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    break;
                }

                frames.Add(frame);
                if (frame.Type == R200FrameType.Notification &&
                    (frame.Command == (byte)R200Command.InventoryOnce ||
                     frame.Command == (byte)R200Command.InventoryMultiple))
                {
                    tags.Add(ParseInventoryTag(frame));
                    continue;
                }

                if (frame.Type == R200FrameType.Response && frame.Command == (byte)R200Command.Error)
                {
                    failure = ParseFailure(frame);
                    if (failure.ErrorCode != 0x15)
                    {
                        throw new R200CommandException(failure);
                    }

                    break;
                }

                if (frame.Type == R200FrameType.Response)
                {
                    break;
                }
            }
        }
        finally
        {
            _ioLock.Release();
        }

        return R200InventoryBatch.Create(tags, failure, frames);
    }

    private async Task<R200ChannelScanResult> ScanChannelsAsync(
        R200Command command,
        CancellationToken cancellationToken)
    {
        var frame = await ExecuteCommandAsync(
            (byte)command,
            ReadOnlyMemory<byte>.Empty,
            [(byte)command],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 2);
        var start = frame.Payload[0];
        var end = frame.Payload[1];
        var expectedSamples = end - start + 1;
        if (frame.Payload.Length - 2 != expectedSamples)
        {
            throw new InvalidDataException(
                $"Channel scan declares {expectedSamples} channels but contains {frame.Payload.Length - 2} samples.");
        }

        var samples = new List<R200ChannelSample>(expectedSamples);
        for (var i = 0; i < expectedSamples; i++)
        {
            samples.Add(new R200ChannelSample((byte)(start + i), unchecked((sbyte)frame.Payload[i + 2])));
        }

        return new R200ChannelScanResult(start, end, samples.AsReadOnly(), frame);
    }

    private async Task<R200IoResult> ExecuteIoAsync(
        R200IoOperation operation,
        byte port,
        bool value,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The protocol exposes IO ports 1 through 4.");
        }

        var payload = new[] { (byte)operation, port, value ? (byte)0x01 : (byte)0x00 };
        var frame = await ExecuteCommandAsync(
            (byte)R200Command.ControlIo,
            payload,
            [(byte)R200Command.ControlIo],
            cancellationToken).ConfigureAwait(false);
        RequirePayloadLength(frame, 3, exact: true);
        if (frame.Payload[0] != (byte)operation || frame.Payload[1] != port)
        {
            throw new InvalidDataException("IO response does not match the requested operation and port.");
        }

        return new R200IoResult(operation, port, frame.Payload[2] != 0, frame);
    }

    private async Task<R200Frame> ExecuteCommandAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        byte[] expectedResponseCommands,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateTimeout(CommandTimeout, nameof(CommandTimeout));
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameCoreAsync(command, payload, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var frame = await ReadFrameCoreAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
                if (frame.Type == R200FrameType.Response && frame.Command == (byte)R200Command.Error)
                {
                    throw new R200CommandException(ParseFailure(frame));
                }

                if (frame.Type == R200FrameType.Response && expectedResponseCommands.Contains(frame.Command))
                {
                    return frame;
                }

                // Při čekání na přímou odpověď se nesouvisející rámce oznámení záměrně přeskakují.
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task WriteFrameCoreAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var bytes = R200ProtocolCodec.BuildFrame(R200FrameType.Command, command, payload.Span);
        var stream = GetStream();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<R200Frame> ReadFrameCoreAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_serialPort is not null)
        {
            return await ReadFrameFromSerialPortAsync(
                _serialPort,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await ReadFrameFromStreamAsync(GetStream(), timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No complete R200 frame was received within {timeout}.");
        }
    }

    private static async Task<R200Frame> ReadFrameFromSerialPortAsync(
        SerialPort serialPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!serialPort.IsOpen)
        {
            throw new InvalidOperationException("The serial port is not open. Call Open() first.");
        }

        const int pollingIntervalMilliseconds = 5;
        var started = Stopwatch.GetTimestamp();

        async ValueTask<byte> ReadByteAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"No complete R200 frame was received within {timeout}.");
                }

                if (serialPort.BytesToRead > 0)
                {
                    break;
                }

                var delay = remaining < TimeSpan.FromMilliseconds(pollingIntervalMilliseconds)
                    ? remaining
                    : TimeSpan.FromMilliseconds(pollingIntervalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var value = serialPort.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException("The serial port closed before a complete R200 frame was received.");
            }

            return (byte)value;
        }

        byte header;
        do
        {
            header = await ReadByteAsync().ConfigureAwait(false);
        }
        while (header != R200ProtocolCodec.FrameHeader);

        var prefix = new byte[4];
        for (var index = 0; index < prefix.Length; index++)
        {
            prefix[index] = await ReadByteAsync().ConfigureAwait(false);
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(2, 2));
        var remainder = new byte[payloadLength + 2];
        for (var index = 0; index < remainder.Length; index++)
        {
            remainder[index] = await ReadByteAsync().ConfigureAwait(false);
        }

        var complete = new byte[payloadLength + 7];
        complete[0] = R200ProtocolCodec.FrameHeader;
        prefix.CopyTo(complete, 1);
        remainder.CopyTo(complete, 5);
        return R200ProtocolCodec.ParseFrame(complete);
    }

    private static async Task<R200Frame> ReadFrameFromStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        var oneByte = new byte[1];
        // Šum na lince nebo zbytky dřívějšího neúplného rámce se zahazují, dokud se nenajde nové AA.
        do
        {
            await stream.ReadExactlyAsync(oneByte, cancellationToken).ConfigureAwait(false);
        }
        while (oneByte[0] != R200ProtocolCodec.FrameHeader);

        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(2, 2));
        var remainder = new byte[payloadLength + 2];
        await stream.ReadExactlyAsync(remainder, cancellationToken).ConfigureAwait(false);

        var complete = new byte[payloadLength + 7];
        complete[0] = R200ProtocolCodec.FrameHeader;
        prefix.CopyTo(complete, 1);
        remainder.CopyTo(complete, 5);
        return R200ProtocolCodec.ParseFrame(complete);
    }

    private Stream GetStream()
    {
        ThrowIfDisposed();
        if (_serialPort is not null)
        {
            if (!_serialPort.IsOpen)
            {
                throw new InvalidOperationException("The serial port is not open. Call Open() first.");
            }

            return _serialPort.BaseStream;
        }

        return _providedStream!;
    }

    private static R200InventoryTag ParseInventoryTag(R200Frame frame)
    {
        RequirePayloadLength(frame, 5);
        var epcLength = frame.Payload.Length - 5;
        var rssi = unchecked((sbyte)frame.Payload[0]);
        var pc = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(1, 2));
        var epc = frame.Payload.AsSpan(3, epcLength).ToArray();
        var crc = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(frame.Payload.Length - 2, 2));
        return new R200InventoryTag(rssi, new R200TagIdentity(pc, epc), crc, frame);
    }

    private static R200TagOperationResult ParseTagOperation(R200Frame frame)
    {
        var (tag, tail) = ParseTagAndTail(frame);
        if (tail.Length == 0)
        {
            throw new InvalidDataException("Tag operation response does not contain a status byte.");
        }

        return new R200TagOperationResult(tag, tail[0], tail[1..], frame);
    }

    private static (R200TagIdentity Tag, byte[] Tail) ParseTagAndTail(R200Frame frame)
    {
        RequirePayloadLength(frame, 3);
        var identityLength = frame.Payload[0];
        if (identityLength < 2 || frame.Payload.Length < identityLength + 1)
        {
            throw new InvalidDataException("Invalid UL/PC+EPC length in tag response.");
        }

        var pc = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(1, 2));
        var epc = frame.Payload.AsSpan(3, identityLength - 2).ToArray();
        var tail = frame.Payload.AsSpan(identityLength + 1).ToArray();
        return (new R200TagIdentity(pc, epc), tail);
    }

    private static R200Failure ParseFailure(R200Frame frame)
    {
        RequirePayloadLength(frame, 1);
        var errorCode = frame.Payload[0];
        R200TagIdentity? tag = null;
        if (frame.Payload.Length >= 4)
        {
            // Rozšířené chyby obsahují ErrorCode, UL, PC a EPC; krátké chyby obsahují pouze ErrorCode.
            var identityLength = frame.Payload[1];
            if (identityLength >= 2 && frame.Payload.Length >= identityLength + 2)
            {
                var pc = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(2, 2));
                var epc = frame.Payload.AsSpan(4, identityLength - 2).ToArray();
                tag = new R200TagIdentity(pc, epc);
            }
        }

        return new R200Failure(errorCode, DescribeError(errorCode), tag, frame);
    }

    private static string DescribeError(byte errorCode) => errorCode switch
    {
        0x17 => "Command code error",
        0x20 => "Frequency hopping channel search timed out",
        0x15 => "Inventory failed, no tag returned, or tag CRC failed",
        0x16 => "Tag access failed, possibly because the access password is incorrect",
        0x09 => "Tag read failed",
        0x10 => "Tag write failed",
        0x13 => "Tag lock failed",
        0x12 => "Tag kill failed",
        0x14 => "BlockPermalock failed",
        0x1A => "NXP ChangeConfig failed",
        0x2A => "NXP ReadProtect failed",
        0x2B => "NXP Reset ReadProtect failed",
        0x1B => "NXP Change EAS failed",
        0x1D => "NXP EAS Alarm failed",
        0x2E => "Impinj Monza QT failed",
        >= 0xA0 and <= 0xAF => $"Tag read error: {DescribeTagError((byte)(errorCode & 0x0F))}",
        >= 0xB0 and <= 0xBF => $"Tag write error: {DescribeTagError((byte)(errorCode & 0x0F))}",
        >= 0xC0 and <= 0xCF => $"Tag lock error: {DescribeTagError((byte)(errorCode & 0x0F))}",
        >= 0xD0 and <= 0xDF => $"Tag kill error: {DescribeTagError((byte)(errorCode & 0x0F))}",
        >= 0xE0 and <= 0xEF => $"Tag special-command error: {DescribeTagError((byte)(errorCode & 0x0F))}",
        _ => "Unknown R200 error"
    };

    private static string DescribeTagError(byte errorCode) => errorCode switch
    {
        0x00 => "other error",
        0x03 => "memory overrun",
        0x04 => "memory locked",
        0x0B => "insufficient power",
        0x0F => "non-specific error",
        _ => $"Gen2 error 0x{errorCode:X2}"
    };

    private static void ValidateSelect(R200SelectParameters parameters)
    {
        if (parameters.Target > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Select Target is a 3-bit value.");
        }

        if (parameters.Action > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Select Action is a 3-bit value.");
        }

        var expectedMaskBytes = (parameters.MaskBitLength + 7) / 8;
        if (parameters.Mask.Length != expectedMaskBytes)
        {
            throw new ArgumentException(
                $"MaskBitLength requires exactly {expectedMaskBytes} mask bytes.",
                nameof(parameters));
        }
    }

    private static byte[] CreatePasswordPayload(uint password, int additionalBytes)
    {
        var payload = new byte[4 + additionalBytes];
        BinaryPrimitives.WriteUInt32BigEndian(payload, password);
        return payload;
    }

    private static byte[] CreateBlockPermalockPayload(
        uint accessPassword,
        bool lockBlocks,
        ushort blockPointer,
        byte blockRange,
        ushort? mask)
    {
        var payload = new byte[lockBlocks ? 11 : 9];
        BinaryPrimitives.WriteUInt32BigEndian(payload, accessPassword);
        payload[4] = lockBlocks ? (byte)0x01 : (byte)0x00;
        payload[5] = (byte)R200MemoryBank.User;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), blockPointer);
        payload[8] = blockRange;
        if (lockBlocks)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(9, 2), mask!.Value);
        }

        return payload;
    }

    private static (decimal Start, decimal Step) GetRegionChannelFormula(R200Region region) => region switch
    {
        R200Region.China900MHz => (920.125m, 0.25m),
        R200Region.China800MHz => (840.125m, 0.25m),
        R200Region.Usa => (902.25m, 0.5m),
        R200Region.Europe => (865.1m, 0.2m),
        R200Region.Korea => (917.1m, 0.2m),
        _ => throw new ArgumentOutOfRangeException(nameof(region), region, "Unknown R200 region.")
    };

    private static void EnsureStatus(R200Frame frame, byte expectedStatus)
    {
        RequirePayloadLength(frame, 1, exact: true);
        if (frame.Payload[0] != expectedStatus)
        {
            throw new InvalidDataException(
                $"Command 0x{frame.Command:X2} returned status 0x{frame.Payload[0]:X2}; expected 0x{expectedStatus:X2}.");
        }
    }

    private static void RequirePayloadLength(R200Frame frame, int length, bool exact = false)
    {
        var valid = exact ? frame.Payload.Length == length : frame.Payload.Length >= length;
        if (!valid)
        {
            var comparison = exact ? "exactly" : "at least";
            throw new InvalidDataException(
                $"Command 0x{frame.Command:X2} payload must contain {comparison} {length} bytes; received {frame.Payload.Length}.");
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be positive and no longer than Int32.MaxValue milliseconds.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
