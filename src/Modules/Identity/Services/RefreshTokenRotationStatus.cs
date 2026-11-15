namespace Ecommerce.Modules.Identity.Services;

public enum RefreshTokenRotationStatus
{
    Rotated = 1,
    Invalid = 2,
    SessionExpired = 3,
    TokenExpired = 4,
    ReplayDetected = 5
}
