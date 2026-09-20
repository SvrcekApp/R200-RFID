# R200RFID

`R200RFID` is a .NET 10 library implementing the command set from **R200 RFID UHF module user guide V2.3.3**.

The main entry point is `R200Reader`. Commands return typed models while retaining the original `R200Frame` whenever the response contains structured data.

## Connect to a serial module

```csharp
using R200RFID;

using var reader = new R200Reader(new R200SerialOptions
{
    PortName = "COM4",
    BaudRate = 115200
});

reader.Open();

var hardware = await reader.GetModuleInformationAsync(
    R200ModuleInformationType.HardwareVersion);

Console.WriteLine(hardware.Value);
```

Serial parity, stop bits, handshake, DTR and RTS are configurable because the PDF specifies the protocol and supported baud rates, but does not define the complete serial-line setup.

## Inventory

```csharp
var batch = await reader.InventoryOnceAsync();

foreach (var item in batch.Tags)
{
    Console.WriteLine($"{item.Tag.EpcHex}  RSSI={item.RssiDbm} dBm");
}
```

The protocol does not define an inventory-complete response. Inventory methods therefore collect notifications until `InventoryQuietPeriod` elapses. For long-running operation, use `StartMultipleInventoryAsync`, `ReadFrameAsync` and `StopMultipleInventoryAsync`.

## Select and tag memory

```csharp
await reader.SetSelectAsync(new R200SelectParameters(
    Target: 0,
    Action: 0,
    MemoryBank: R200MemoryBank.Epc,
    BitPointer: 0x20,
    MaskBitLength: 96,
    Truncate: false,
    Mask: Convert.FromHexString("30751FEB705C5904E3D50D70")));

var data = await reader.ReadTagAsync(
    accessPassword: 0x0000FFFF,
    memoryBank: R200MemoryBank.User,
    wordAddress: 0,
    wordCount: 2);
```

## Implemented commands

- Module hardware, software and manufacturer information
- Single and multiple inventory, stop inventory
- Set/get Select and Select mode
- Read, write, lock and kill tag
- Set baud rate
- Set/get Query parameters
- Set/get region and working channel
- Automatic hopping and custom hopping channel list
- Get/set transmit power and continuous carrier
- Get/set demodulator, jammer scan and RSSI scan
- IO1-IO4 direction, output and input
- Sleep, wake, automatic idle sleep and IDLE mode
- NXP ReadProtect, Reset ReadProtect, Change EAS, EAS Alarm and ChangeConfig
- Impinj Monza QT
- BlockPermalock read and lock
- Raw command/frame access

## Protocol inconsistencies

The source PDF contains several contradictory example values. The implementation calculates PL and checksum from the actual payload and follows byte tables whose checksum is internally consistent. Raw frames are retained so firmware-specific behavior remains inspectable.
