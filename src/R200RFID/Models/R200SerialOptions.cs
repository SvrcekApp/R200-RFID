using System.IO.Ports;

namespace R200RFID;

/// <summary>Nastavuje sériový přenos a časové limity protokolu používané třídou <see cref="R200Reader"/>.</summary>
public sealed class R200SerialOptions
{
    /// <summary>Získá název sériového portu operačního systému, například COM4.</summary>
    public required string PortName { get; init; }

    /// <summary>Získá přenosovou rychlost sériové linky použitou při otevření portu.</summary>
    public required int BaudRate { get; init; }

    /// <summary>Získá počet datových bitů. PDF jej neurčuje; výchozí hodnota je 8.</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>Získá nastavení parity. PDF jej neurčuje; výchozí hodnota je bez parity.</summary>
    public Parity Parity { get; init; } = Parity.None;

    /// <summary>Získá nastavení stop bitů. PDF jej neurčuje; výchozí hodnota je jeden stop bit.</summary>
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>Získá nastavení řízení toku sériové linky. PDF jej neurčuje; výchozí hodnota je bez řízení toku.</summary>
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>Získá informaci, zda je signál DTR aktivní.</summary>
    public bool DtrEnable { get; init; }

    /// <summary>Získá informaci, zda je signál RTS aktivní.</summary>
    public bool RtsEnable { get; init; }

    /// <summary>Získá maximální dobu čekání na běžnou odpověď příkazu.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Získá dobu bez komunikace, podle které se určí ukončení dávky inventory.</summary>
    public TimeSpan InventoryQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(250);
}
