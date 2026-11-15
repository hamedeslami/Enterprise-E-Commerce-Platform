namespace Ecommerce.Modules.Identity.Services;

public sealed record RefreshTokenRotationResult(
    RefreshTokenRotationStatus Status,
    Guid? SuccessorTokenId = null,
    byte[]? SuccessorSecret = null,
    DateTimeOffset? ExpiresAt = null);
