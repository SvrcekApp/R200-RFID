using System.Globalization;
using System.Text;
using R200RFID;

namespace R200Example;

/// <summary>
/// Interaktivní ukázky nejběžnějších postupů s R200: nepřetržité načítání tagů,
/// zápis do paměti User a nastavení pevného kanálu v rámci zvoleného regionu.
/// </summary>
internal static class Program
{
    /// <summary>Výchozí rychlost sériové linky použitá pouze tehdy, když uživatel nezadá druhý argument.</summary>
    private const int DefaultBaudRate = 115200;

    /// <summary>Vstupní bod aplikace.</summary>
    /// <param name="args">Volitelné argumenty: název sériového portu a přenosová rychlost.</param>
    /// <returns>Nulu při běžném ukončení; nenulovou hodnotu při chybě spuštění nebo komunikace.</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        PrintBanner();

        // Prvním argumentem lze zadat COM4 (nebo jiný název sériového portu dané platformy).
        var portName = args.Length > 0 ? args[0] : PromptRequired("Název sériového portu (např. COM4)");
        var baudRate = args.Length > 1 && int.TryParse(args[1], out var parsedBaudRate)
            ? parsedBaudRate
            : DefaultBaudRate;

        // Parametry sériové linky, které PDF neurčuje, zde zůstávají výslovně uvedené a nastavitelné.
        var options = new R200SerialOptions
        {
            PortName = portName,
            BaudRate = baudRate,
            CommandTimeout = TimeSpan.FromSeconds(2),
            InventoryQuietPeriod = TimeSpan.FromMilliseconds(300)
        };

        try
        {
            // R200Reader vlastní SerialPort a při ukončení aplikace jej uvolní.
            await using var reader = new R200Reader(options);
            reader.Open();
            Console.WriteLine($"Připojeno k {reader.PortName} rychlostí {reader.BaudRate} bit/s.\n");

            // Port zůstává otevřený po celou dobu výběru jednotlivých ukázek z nabídky.
            await RunMenuAsync(reader);
            return 0;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Port nelze otevřít (může jej používat jiná aplikace): {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Komunikace s R200 selhala: {exception.Message}");
            return 3;
        }
    }

    /// <summary>Zobrazuje interaktivní nabídku ukázek, dokud uživatel nezvolí ukončení.</summary>
    private static async Task RunMenuAsync(R200Reader reader)
    {
        while (true)
        {
            Console.WriteLine("Vyber příklad:");
            Console.WriteLine("  1 - Nekonečné načítání tagů (ukončení Ctrl+C)");
            Console.WriteLine("  2 - Zápis UTF-8 textu do User memory konkrétního tagu");
            Console.WriteLine("  3 - Nastavení regionu a pevné pracovní frekvence");
            Console.WriteLine("  4 - Zobrazení aktuálního regionu, kanálu a výkonu");
            Console.WriteLine("  0 - Konec");
            Console.Write("Volba: ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            try
            {
                switch (choice)
                {
                    case "1":
                        await RunContinuousInventoryAsync(reader);
                        break;
                    case "2":
                        await WriteTextToTagAsync(reader);
                        break;
                    case "3":
                        await ConfigureRegionAndFrequencyAsync(reader);
                        break;
                    case "4":
                        await ShowRadioConfigurationAsync(reader);
                        break;
                    case "0":
                        return;
                    default:
                        Console.WriteLine("Neznámá volba.\n");
                        break;
                }
            }
            catch (R200CommandException exception)
            {
                // Chyby zařízení obsahují původní kód chyby a případně také EPC dotčeného tagu.
                Console.Error.WriteLine(
                    $"R200 vrátil chybu 0x{exception.ErrorCode:X2}: {exception.Failure.Description}");
                if (exception.Tag is not null)
                {
                    Console.Error.WriteLine($"Dotčený EPC: {exception.Tag.EpcHex}");
                }

                Console.WriteLine();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or IOException)
            {
                Console.Error.WriteLine($"Operaci nelze provést: {exception.Message}\n");
            }
        }
    }

    /// <summary>
    /// Spustí nejvyšší zdokumentovaný počet cyklů inventory a čte rámce až do stisku Ctrl+C.
    /// Po dokončení konečného příkazu s 65 535 cykly jej ukázka ihned spustí znovu.
    /// </summary>
    private static async Task RunContinuousInventoryAsync(R200Reader reader)
    {
        using var cancellation = new CancellationTokenSource();

        // V této ukázce Ctrl+C zruší inventory namísto ukončení celého procesu.
        void CancelInventory(object? _, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += CancelInventory;
        var readCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("Spouštím nepřetržitý inventory. Pro návrat do menu stiskni Ctrl+C.\n");

        try
        {
            // Hodnota 65 535 je nejvyšší počet dotazů povolený zdokumentovaným dvoubajtovým polem CNT.
            await reader.StartMultipleInventoryAsync(ushort.MaxValue, cancellation.Token);

            while (!cancellation.IsCancellationRequested)
            {
                R200Frame frame;
                try
                {
                    // Vypršení času čtení pouze znamená, že nepřišel tag ani rámec; inventory může stále běžet.
                    frame = await reader.ReadFrameAsync(cancellation.Token);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (frame.Type == R200FrameType.Notification)
                {
                    var tag = R200Reader.ParseInventoryNotification(frame);
                    var epc = tag.Tag.EpcHex;
                    readCounts[epc] = readCounts.GetValueOrDefault(epc) + 1;

                    Console.WriteLine(
                        $"{DateTime.Now:HH:mm:ss.fff}  EPC={epc}  " +
                        $"RSSI={tag.RssiDbm,4} dBm  PC=0x{tag.Tag.ProtocolControl:X4}  " +
                        $"počet={readCounts[epc]}");
                    continue;
                }

                if (frame.Type == R200FrameType.Response && frame.Command == (byte)R200Command.Error)
                {
                    var failure = R200Reader.ParseErrorResponse(frame);
                    if (failure.ErrorCode != 0x15)
                    {
                        Console.Error.WriteLine(
                            $"Inventory chyba 0x{failure.ErrorCode:X2}: {failure.Description}");
                    }

                    // Chyba 0x15 označuje také dokončený pokus bez tagu, proto se dlouhé načítání spustí znovu.
                    await reader.StartMultipleInventoryAsync(ushort.MaxValue, cancellation.Token);
                    continue;
                }

                if (frame.Type == R200FrameType.Response)
                {
                    // PDF nedefinuje rámec úspěchu či dokončení, ale případný takový rámec obsloužíme restartem.
                    await reader.StartMultipleInventoryAsync(ushort.MaxValue, cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Očekávaný průchod po stisku Ctrl+C.
        }
        finally
        {
            Console.CancelKeyPress -= CancelInventory;

            // Použije se nový token, protože token pro inventory už byl zrušen.
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await reader.StopMultipleInventoryAsync(stopTimeout.Token);
            }
            catch (Exception exception) when (exception is TimeoutException or R200CommandException)
            {
                Console.Error.WriteLine($"Stop inventory nebyl potvrzen: {exception.Message}");
            }

            Console.WriteLine($"\nInventory ukončen. Unikátních EPC: {readCounts.Count}.\n");
        }
    }

    /// <summary>Vybere jedno EPC a zapíše řetězec UTF-8 do jeho paměti User.</summary>
    private static async Task WriteTextToTagAsync(R200Reader reader)
    {
        var epcBytes = PromptHexBytes("EPC tagu bez 0x (např. 30751FEB705C5904E3D50D70)");
        if (epcBytes.Length * 8 > byte.MaxValue)
        {
            throw new ArgumentException("Select MaskLength je jeden byte; EPC maska proto může mít nejvýše 255 bitů.");
        }

        var wordAddress = PromptUInt16("Počáteční word adresa v User memory", defaultValue: 0);
        var accessPassword = PromptUInt32Hex("Access Password", defaultValue: 0x00000000);
        var text = PromptRequired("Text k zápisu (UTF-8, maximálně 64 bajtů)");
        var data = Encoding.UTF8.GetBytes(text);

        if (data.Length > 64)
        {
            throw new ArgumentException($"Text má {data.Length} bajtů; jeden Write příkaz dovoluje nejvýše 64 bajtů.");
        }

        // R200 zapisuje celé 16bitové wordy. Lichý počet bajtů UTF-8 se doplní jedním nulovým bajtem.
        if ((data.Length & 1) != 0)
        {
            Array.Resize(ref data, data.Length + 1);
        }

        // Podle příkladu v protokolu začíná EPC v paměťové bance EPC na bitové adrese 0x20.
        var select = new R200SelectParameters(
            Target: 0,
            Action: 0,
            MemoryBank: R200MemoryBank.Epc,
            BitPointer: 0x20,
            MaskBitLength: checked((byte)(epcBytes.Length * 8)),
            Truncate: false,
            Mask: epcBytes);

        await reader.SetSelectAsync(select);

        // Režim 0x02 použije Select před zápisem, čtením, zamknutím a zničením, ale inventory neovlivní.
        await reader.SetSelectModeAsync(R200SelectMode.BeforeNonInventoryTagOperations);

        var result = await reader.WriteTagAsync(
            accessPassword,
            R200MemoryBank.User,
            wordAddress,
            data);

        Console.WriteLine(
            $"Zápis potvrzen tagem {result.Tag.EpcHex}; " +
            $"zapsáno {data.Length / 2} wordů od adresy {wordAddress}.\n");
    }

    /// <summary>Nastaví regulační region, vypne přeskakování frekvencí a vybere přesný zdokumentovaný kanál.</summary>
    private static async Task ConfigureRegionAndFrequencyAsync(R200Reader reader)
    {
        Console.WriteLine("Regiony:");
        Console.WriteLine("  1 - Europe");
        Console.WriteLine("  2 - USA");
        Console.WriteLine("  3 - China 900 MHz");
        Console.WriteLine("  4 - China 800 MHz");
        Console.WriteLine("  5 - Korea");
        Console.Write("Volba regionu: ");

        var region = Console.ReadLine()?.Trim() switch
        {
            "1" => R200Region.Europe,
            "2" => R200Region.Usa,
            "3" => R200Region.China900MHz,
            "4" => R200Region.China800MHz,
            "5" => R200Region.Korea,
            _ => throw new ArgumentException("Neplatná volba regionu.")
        };

        var frequencyMHz = PromptDecimal("Požadovaná frekvence v MHz (musí přesně ležet na rastru regionu)");
        var channelIndex = R200Reader.GetChannelIndex(region, frequencyMHz);

        Console.WriteLine(
            $"Nastavuji {region}, kanál {channelIndex}, " +
            $"frekvenci {R200Reader.GetChannelFrequencyMHz(region, channelIndex)} MHz...");

        // Region je nutné nastavit jako první, protože mění výpočet frekvence i tabulku platných kanálů.
        await reader.SetRegionAsync(region);

        // Pevná pracovní frekvence vyžaduje před výběrem kanálu vypnout přeskakování frekvencí.
        await reader.SetAutomaticFrequencyHoppingAsync(false);
        await reader.SetWorkingChannelAsync(channelIndex);

        // Obě hodnoty se znovu načtou, aby ukázka ověřila přijetí konfigurace modulem.
        var confirmedRegion = await reader.GetRegionAsync();
        var confirmedChannel = await reader.GetWorkingChannelAsync();
        var confirmedFrequency = R200Reader.GetChannelFrequencyMHz(confirmedRegion, confirmedChannel);

        Console.WriteLine(
            $"Potvrzeno: {confirmedRegion}, kanál {confirmedChannel}, {confirmedFrequency} MHz.\n");
        Console.WriteLine("Používej pouze frekvence a výkon povolené místními předpisy.\n");
    }

    /// <summary>Načte a zobrazí aktuální region, pevný kanál, frekvenci a vysílací výkon.</summary>
    private static async Task ShowRadioConfigurationAsync(R200Reader reader)
    {
        var region = await reader.GetRegionAsync();
        var channel = await reader.GetWorkingChannelAsync();
        var frequency = R200Reader.GetChannelFrequencyMHz(region, channel);
        var power = await reader.GetTransmitPowerDbmAsync();

        Console.WriteLine($"Region:     {region}");
        Console.WriteLine($"Kanál:      {channel}");
        Console.WriteLine($"Frekvence:  {frequency} MHz");
        Console.WriteLine($"Výkon:      {power:F2} dBm\n");
    }

    /// <summary>Vypíše účel programu a syntaxi příkazového řádku.</summary>
    private static void PrintBanner()
    {
        Console.WriteLine("R200Example - příklady použití R200RFID pro .NET 10");
        Console.WriteLine("Spuštění: dotnet run --project R200Example -- COM4 115200\n");
    }

    /// <summary>Načte z konzole neprázdný řetězec.</summary>
    private static string PromptRequired(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var value = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            Console.WriteLine("Hodnota nesmí být prázdná.");
        }
    }

    /// <summary>Načte hexadecimální bajty a odstraní z nich vizuální oddělovače.</summary>
    private static byte[] PromptHexBytes(string label)
    {
        var text = PromptRequired(label)
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (text.Length == 0 || (text.Length & 1) != 0)
        {
            throw new FormatException("Hexadecimální hodnota musí obsahovat celý počet bajtů.");
        }

        return Convert.FromHexString(text);
    }

    /// <summary>Načte desetinnou 16bitovou hodnotu bez znaménka; prázdný vstup nahradí výchozí hodnotou.</summary>
    private static ushort PromptUInt16(string label, ushort defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var text = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return defaultValue;
        }

        return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            ? value
            : throw new FormatException("Očekává se celé číslo 0-65535.");
    }

    /// <summary>Načte hexadecimální 32bitovou hodnotu bez znaménka; prázdný vstup nahradí výchozí hodnotou.</summary>
    private static uint PromptUInt32Hex(string label, uint defaultValue)
    {
        Console.Write($"{label} v hex [{defaultValue:X8}]: ");
        var text = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return defaultValue;
        }

        text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException("Očekává se nejvýše osm hexadecimálních číslic.");
    }

    /// <summary>Načte desetinnou hodnotu nejprve podle aktuální a poté podle invariantní jazykové verze.</summary>
    private static decimal PromptDecimal(string label)
    {
        var text = PromptRequired(label);
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var currentCultureValue))
        {
            return currentCultureValue;
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var invariantValue))
        {
            return invariantValue;
        }

        throw new FormatException("Očekává se desetinné číslo, například 865,7 nebo 865.7.");
    }
}
