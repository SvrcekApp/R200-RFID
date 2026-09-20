using System.Buffers.Binary;

namespace R200RFID;

/// <summary>Sestavuje a ověřuje bajtové rámce AA...DD používané protokolem R200.</summary>
public static class R200ProtocolCodec
{
    /// <summary>Počáteční bajt rámce.</summary>
    public const byte FrameHeader = 0xAA;
    /// <summary>Koncový bajt rámce.</summary>
    public const byte FrameEnd = 0xDD;

    /// <summary>Sestaví rámec pro známý kód příkazu.</summary>
    public static byte[] BuildFrame(R200FrameType type, R200Command command, ReadOnlySpan<byte> payload = default) =>
        BuildFrame(type, (byte)command, payload);

    /// <summary>Sestaví rámec pro libovolný číselný kód příkazu.</summary>
    public static byte[] BuildFrame(R200FrameType type, byte command, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload cannot exceed 65535 bytes.");
        }

        var frame = new byte[payload.Length + 7];
        // Uspořádání na lince: AA, typ, příkaz, PL(MSB), PL(LSB), data, kontrolní součet, DD.
        frame[0] = FrameHeader;
        frame[1] = (byte)type;
        frame[2] = command;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        frame[^2] = ComputeChecksum(frame.AsSpan(1, payload.Length + 4));
        frame[^1] = FrameEnd;
        return frame;
    }

    /// <summary>Vypočítá nižší bajt součtu od typu rámce po poslední bajt dat.</summary>
    public static byte ComputeChecksum(ReadOnlySpan<byte> typeThroughPayload)
    {
        var sum = 0;
        // Protokol z kontrolního součtu záměrně vynechává AA i DD.
        foreach (var value in typeThroughPayload)
        {
            sum += value;
        }

        return unchecked((byte)sum);
    }

    /// <summary>Ověří hlavičku, PL, kontrolní součet a ukončovací bajt a poté vrátí naparsovaný rámec.</summary>
    public static R200Frame ParseFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7)
        {
            throw new InvalidDataException("An R200 frame must contain at least 7 bytes.");
        }

        if (frame[0] != FrameHeader)
        {
            throw new InvalidDataException($"Invalid frame header 0x{frame[0]:X2}; expected 0xAA.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(3, 2));
        var expectedLength = payloadLength + 7;
        if (frame.Length != expectedLength)
        {
            throw new InvalidDataException($"Frame has {frame.Length} bytes, but PL declares {expectedLength} bytes.");
        }

        if (frame[^1] != FrameEnd)
        {
            throw new InvalidDataException($"Invalid frame terminator 0x{frame[^1]:X2}; expected 0xDD.");
        }

        var expectedChecksum = ComputeChecksum(frame.Slice(1, payloadLength + 4));
        if (frame[^2] != expectedChecksum)
        {
            throw new InvalidDataException(
                $"Invalid checksum 0x{frame[^2]:X2}; expected 0x{expectedChecksum:X2}.");
        }

        return new R200Frame((R200FrameType)frame[1], frame[2], frame.Slice(5, payloadLength).ToArray());
    }
}
