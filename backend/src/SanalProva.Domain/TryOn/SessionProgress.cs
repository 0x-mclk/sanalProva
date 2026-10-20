namespace SanalProva.Domain.TryOn;

/// <summary>Oturumun bir sonraki doğal adımı. Sayfa yenilendiğinde veya "devam eden prova" açıldığında kullanıcı buraya yönlenir.</summary>
public enum SessionStep
{
    Consent,
    Photo,
    Mode,
    Outfit,
    Generating,
    Result,
    Closed,
}

public static class SessionProgress
{
    public static SessionStep NextStep(SessionStatus status, bool hasConsent, bool hasPhoto, bool hasDraft, bool hasActiveJob, bool hasResult)
    {
        if (status != SessionStatus.Open)
        {
            return SessionStep.Closed;
        }

        if (!hasConsent)
        {
            return SessionStep.Consent;
        }

        if (!hasPhoto)
        {
            return SessionStep.Photo;
        }

        if (hasActiveJob)
        {
            return SessionStep.Generating;
        }

        if (hasResult)
        {
            return SessionStep.Result;
        }

        return hasDraft ? SessionStep.Outfit : SessionStep.Mode;
    }

    /// <summary>Ekranda gösterilen kısa oturum kodu ("PR-024"). Benzersizlik garantisi vermez; yalnızca görsel etikettir.</summary>
    public static string DisplayCode(Guid sessionId)
    {
        Span<byte> bytes = stackalloc byte[16];
        sessionId.TryWriteBytes(bytes);
        return $"PR-{(bytes[14] << 8 | bytes[15]) % 1000:D3}";
    }
}
