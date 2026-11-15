namespace Ecommerce.Modules.Identity.Domain;

public sealed class AuthSession
{
    private AuthSession()
    {
    }

    public AuthSession(
        Guid userId,
        AccountType actorKind,
        AuthenticationMethod authenticationMethod,
        DateTimeOffset authenticatedAt,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset idleExpiresAt,
        string? deviceLabel = null,
        byte[]? userAgentHash = null)
    {
        authenticatedAt = authenticatedAt.ToUniversalTime();
        absoluteExpiresAt = absoluteExpiresAt.ToUniversalTime();
        idleExpiresAt = idleExpiresAt.ToUniversalTime();

        if (userId == Guid.Empty)
            throw new ArgumentException("A session must belong to a persisted user.", nameof(userId));
        if (actorKind is not (AccountType.Customer or AccountType.Staff))
            throw new ArgumentOutOfRangeException(nameof(actorKind));
        if (!Enum.IsDefined(authenticationMethod))
            throw new ArgumentOutOfRangeException(nameof(authenticationMethod));
        if (absoluteExpiresAt <= authenticatedAt)
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiresAt));
        if (idleExpiresAt <= authenticatedAt || idleExpiresAt > absoluteExpiresAt)
            throw new ArgumentOutOfRangeException(nameof(idleExpiresAt));
        if (deviceLabel?.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(deviceLabel));
        if (userAgentHash is { Length: not 32 })
            throw new ArgumentException("User-agent fingerprints must be SHA-256 hashes.", nameof(userAgentHash));

        Id = Guid.CreateVersion7();
        UserId = userId;
        ActorKind = actorKind;
        AuthenticationMethod = authenticationMethod;
        AuthenticatedAt = authenticatedAt;
        CreatedAt = authenticatedAt;
        LastSeenAt = authenticatedAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        IdleExpiresAt = idleExpiresAt;
        DeviceLabel = deviceLabel;
        UserAgentHash = userAgentHash?.ToArray();
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public AccountType ActorKind { get; private set; }
    public AuthenticationMethod AuthenticationMethod { get; private set; }
    public DateTimeOffset AuthenticatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; internal set; }
    public DateTimeOffset AbsoluteExpiresAt { get; private set; }
    public DateTimeOffset IdleExpiresAt { get; internal set; }
    public DateTimeOffset? RevokedAt { get; internal set; }
    public string? RevocationReason { get; internal set; }
    public byte[]? UserAgentHash { get; private set; }
    public string? DeviceLabel { get; private set; }
}
