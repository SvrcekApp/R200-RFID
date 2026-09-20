namespace R200RFID;

/// <summary>Představuje selhání příkazu vrácené modulem prostřednictvím odpovědi s příkazem 0xFF.</summary>
public sealed class R200CommandException : IOException
{
    /// <summary>Vytvoří výjimku z naparsované chyby modulu.</summary>
    public R200CommandException(R200Failure failure)
        : base($"R200 command failed with error 0x{failure.ErrorCode:X2}: {failure.Description}")
    {
        Failure = failure;
    }

    /// <summary>Získá úplné strukturované informace o selhání.</summary>
    public R200Failure Failure { get; }

    /// <summary>Získá původní kód chyby modulu.</summary>
    public byte ErrorCode => Failure.ErrorCode;

    /// <summary>Získá dotčený tag, pokud chybová odpověď obsahovala PC a EPC.</summary>
    public R200TagIdentity? Tag => Failure.Tag;
}
