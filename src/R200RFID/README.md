# R200RFID

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](../../LICENSE)

A strongly typed library for communicating with the R200 UHF RFID module from C# and .NET 10 applications. It implements the serial protocol described in the **R200 RFID UHF Module User Guide V2.3.3**, validates frames, and converts module responses into typed models.

The solution also includes [`R200Example`](../R200Example), an interactive console application that demonstrates common scenarios with a physical device.

## Table of contents

- [Library contents](#library-contents)
- [Requirements](#requirements)
- [Build](#build)
- [Quick start](#quick-start)
- [Tag inventory](#tag-inventory)
- [Tag selection and memory access](#tag-selection-and-memory-access)
- [Example application](#example-application)
- [Supported commands](#supported-commands)
- [Errors and low-level access](#errors-and-low-level-access)
- [Protocol notes](#protocol-notes)
- [License](#license)

## Library contents

| Component | Description |
| --- | --- |
| [`R200Reader`](R200Reader.cs) | The main API for opening a serial port, sending commands, and reading responses asynchronously. It can also use a custom bidirectional `Stream`, which is useful for tests or alternative transports. |
| [`R200SerialOptions`](Models/R200SerialOptions.cs) | Serial port, baud rate, parity, stop bits, flow control, and timeout settings. |
| [Typed models](Models/R200Models.cs) | Inventory results, tag identities, RF configuration, Query/Select parameters, IO, NXP, Impinj, and BlockPermalock data. Inventory results and most structured responses also retain the original `R200Frame`. |
| [`R200ProtocolCodec`](Protocol/R200ProtocolCodec.cs) | Builds and parses `AA ... DD` frames, calculates their length and checksum, and validates received data. |
| [Protocol enums](Protocol/R200ProtocolEnums.cs) | Frame types, commands, memory banks, regions, Query parameters, and other protocol values. |
| [`R200CommandException`](R200CommandException.cs) | A typed exception for the module's `0xFF` error response, including the error code and the affected tag EPC when returned by the module. |

The asynchronous operations provided by `R200Reader` use `Task` and support `CancellationToken`. The reader implements both synchronous and asynchronous resource disposal (`IDisposable` and `IAsyncDisposable`).

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An R200 UHF RFID module using protocol V2.3.3
- A serial connection exposed as an operating-system port, such as `COM4`

The protocol documentation defines the supported baud rates but does not specify the complete serial-line configuration. The library therefore allows parity, stop bits, flow control, DTR, and RTS to be configured. The defaults are 8 data bits, no parity, one stop bit, and no flow control.

## Build

Run the following commands from the repository root:

```powershell
dotnet restore src/R200RFID/R200RFID.slnx
dotnet build src/R200RFID/R200RFID.slnx
```

An application project can reference the library directly within the repository:

```powershell
dotnet add <path-to-project> reference src/R200RFID/R200RFID.csproj
```

## Quick start

```csharp
using R200RFID;

await using var reader = new R200Reader(new R200SerialOptions
{
    PortName = "COM4",
    BaudRate = 115200,
    CommandTimeout = TimeSpan.FromSeconds(2),
    InventoryQuietPeriod = TimeSpan.FromMilliseconds(300)
});

reader.Open();

var hardware = await reader.GetModuleInformationAsync(
    R200ModuleInformationType.HardwareVersion);

Console.WriteLine($"Hardware: {hardware.Value}");
```

`R200Reader` owns the `SerialPort` it creates and closes it when disposed. The alternative constructor accepts a readable and writable `Stream`; its `leaveOpen` parameter controls whether the stream remains open after the reader is disposed.

## Tag inventory

A single inventory operation returns all detected tags, including their EPC, PC, and RSSI values:

```csharp
var batch = await reader.InventoryOnceAsync();

foreach (var item in batch.Tags)
{
    Console.WriteLine(
        $"EPC={item.Tag.EpcHex}, PC=0x{item.Tag.ProtocolControl:X4}, " +
        $"RSSI={item.RssiDbm} dBm");
}
```

The protocol does not define a separate response indicating that an inventory operation has completed. `InventoryOnceAsync` and `InventoryMultipleAsync` therefore collect notifications until `InventoryQuietPeriod` expires. For continuous reading, use `StartMultipleInventoryAsync`, call `ReadFrameAsync` repeatedly, and finish with `StopMultipleInventoryAsync`.

## Tag selection and memory access

The following example selects a tag by EPC and reads two 16-bit words from its User memory:

```csharp
await reader.SetSelectAsync(new R200SelectParameters(
    Target: 0,
    Action: 0,
    MemoryBank: R200MemoryBank.Epc,
    BitPointer: 0x20,
    MaskBitLength: 96,
    Truncate: false,
    Mask: Convert.FromHexString("30751FEB705C5904E3D50D70")));

await reader.SetSelectModeAsync(
    R200SelectMode.BeforeNonInventoryTagOperations);

var result = await reader.ReadTagAsync(
    accessPassword: 0x00000000,
    memoryBank: R200MemoryBank.User,
    wordAddress: 0,
    wordCount: 2);

Console.WriteLine(Convert.ToHexString(result.Data));
```

Write operations work with complete 16-bit words and accept up to 64 bytes in a single command. `Lock`, `Kill`, and `BlockPermalock` operations may be irreversible; verify the selected tag and access passwords before using them.

## Example application

[`R200Example`](../R200Example) is a documented interactive console application that references the library directly. It includes the following scenarios:

1. **Continuous inventory** — reads notification frames and displays each tag's EPC, PC, RSSI, and read count. Pressing `Ctrl+C` stops the inventory cleanly and returns to the menu.
2. **Write to User memory** — selects a specific tag by EPC, configures Select mode, and writes the entered UTF-8 text. An odd number of bytes is padded with a zero byte to form a complete word.
3. **Configure region and frequency** — selects a regulatory region, converts a frequency to its channel index, disables frequency hopping, and reads the settings back for verification.
4. **Display RF configuration** — shows the current region, channel, calculated frequency, and transmit power.

Run the example from the repository root:

```powershell
dotnet run --project src/R200Example/R200Example.csproj -- COM4 115200
```

The first argument is the port name, and the optional second argument is the baud rate. If the port is omitted, the application prompts for it. The default baud rate is `115200` bit/s. See the [example application README](../R200Example/README.md) for more information.

> [!CAUTION]
> Use only frequencies and transmit power levels permitted by local regulations and supported by the connected hardware.

## Supported commands

- Module hardware, firmware, and manufacturer information
- Single and multiple inventory operations, including stop inventory
- Get and set Select parameters and Select mode
- Read, write, lock, and kill tag operations
- Baud rate configuration
- Get and set Gen2 Query parameters
- Region, working channel, automatic frequency hopping, and custom hopping channel list
- Transmit power and continuous RF carrier
- Demodulator parameters, jammer scan, and RSSI scan
- Direction, output, and input operations for IO1 through IO4
- Sleep, wake, automatic idle sleep, and RF IDLE mode
- NXP ReadProtect, Reset ReadProtect, Change EAS, EAS Alarm, and ChangeConfig
- Impinj Monza QT
- BlockPermalock read and lock
- Direct command and protocol-frame access

## Errors and low-level access

The library converts an error response from the module into an `R200CommandException`:

```csharp
try
{
    await reader.ReadTagAsync(
        accessPassword: 0,
        memoryBank: R200MemoryBank.User,
        wordAddress: 0,
        wordCount: 2);
}
catch (R200CommandException exception)
{
    Console.Error.WriteLine(
        $"R200 error 0x{exception.ErrorCode:X2}: " +
        exception.Failure.Description);
}
```

For less common scenarios, the library exposes `SendCommandAsync`, `WriteCommandAsync`, `ReadFrameAsync`, `ParseInventoryNotification`, and `ParseErrorResponse`. `R200ProtocolCodec` can independently build, serialize, and validate protocol frames.

## Protocol notes

The V2.3.3 source documentation contains several contradictory example values. The implementation calculates the payload length and checksum from the actual data and follows byte tables whose checksums are internally consistent. Original frames remain available in structured results so that firmware-specific behavior can be inspected.

## License

This project is available under the [MIT License](../../LICENSE).
