namespace Ecommerce.Modules.Identity.Domain;

public sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    public RefreshToken(
        Guid sessionId,
        byte[] secretHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        createdAt = createdAt.ToUniversalTime();
        expiresAt = expiresAt.ToUniversalTime();

        if (sessionId == Guid.Empty)
            throw new ArgumentException("A refresh token must belong to a session.", nameof(sessionId));
        if (secretHash is not { Length: 32 })
            throw new ArgumentException("Refresh-token secrets must be stored as SHA-256 hashes.", nameof(secretHash));
        if (expiresAt <= createdAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        Id = Guid.CreateVersion7();
        SessionId = sessionId;
        SecretHash = secretHash.ToArray();
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public byte[] SecretHash { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; internal set; }
    public DateTimeOffset? RevokedAt { get; internal set; }
    public Guid? ReplacedByTokenId { get; internal set; }
    public AuthSession Session { get; private set; } = null!;
}
