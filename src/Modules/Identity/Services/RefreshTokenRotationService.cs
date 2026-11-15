using System.Security.Cryptography;
using Ecommerce.Modules.Identity.Domain;
using Ecommerce.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Modules.Identity.Services;

/// <summary>
/// Performs the database-critical portion of refresh rotation.
/// The caller is responsible for parsing the external credential and issuing cookies only after this method returns Rotated.
/// </summary>
public sealed class RefreshTokenRotationService(IdentityDbContext dbContext)
{
    private const int SecretLengthBytes = 32;

    public async Task<RefreshTokenRotationResult> RotateAsync(
        Guid tokenId,
        ReadOnlyMemory<byte> presentedSecret,
        TimeSpan idleWindow,
        CancellationToken cancellationToken = default)
    {
        if (tokenId == Guid.Empty || presentedSecret.Length != SecretLengthBytes || idleWindow <= TimeSpan.Zero)
            return new(RefreshTokenRotationStatus.Invalid);

        var presentedHash = SHA256.HashData(presentedSecret.Span);

        // This read is only a lookup optimization. The secret is checked again after both rows are locked.
        var lookup = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(token => token.Id == tokenId)
            .Select(token => new { token.SessionId, token.SecretHash })
            .SingleOrDefaultAsync(cancellationToken);

        if (lookup is null || !CryptographicOperations.FixedTimeEquals(lookup.SecretHash, presentedHash))
            return new(RefreshTokenRotationStatus.Invalid);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            cancellationToken);

        // Every session/token mutation must follow this lock order: session first, then token.
        var session = await dbContext.AuthSessions
            .FromSqlInterpolated($"""
                SELECT * FROM identity."AuthSessions"
                WHERE "Id" = {lookup.SessionId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (session is null)
            return new(RefreshTokenRotationStatus.Invalid);

        var token = await dbContext.RefreshTokens
            .FromSqlInterpolated($"""
                SELECT * FROM identity."RefreshTokens"
                WHERE "Id" = {tokenId} AND "SessionId" = {session.Id}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (token is null || !CryptographicOperations.FixedTimeEquals(token.SecretHash, presentedHash))
            return new(RefreshTokenRotationStatus.Invalid);

        var now = await dbContext.Database
            .SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (session.RevokedAt is not null ||
            session.IdleExpiresAt <= now ||
            session.AbsoluteExpiresAt <= now)
        {
            return new(RefreshTokenRotationStatus.SessionExpired);
        }

        // Replay classification deliberately precedes the presented token's own expiry check.
        if (token.ConsumedAt is not null)
        {
            session.RevokedAt = now;
            session.RevocationReason = "RefreshTokenReplay";

            await dbContext.RefreshTokens
                .Where(candidate =>
                    candidate.SessionId == session.Id &&
                    candidate.ConsumedAt == null &&
                    candidate.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(candidate => candidate.RevokedAt, now),
                    cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(RefreshTokenRotationStatus.ReplayDetected);
        }

        if (token.RevokedAt is not null)
            return new(RefreshTokenRotationStatus.Invalid);

        if (token.ExpiresAt <= now)
            return new(RefreshTokenRotationStatus.TokenExpired);

        var newIdleExpiresAt = now.Add(idleWindow);
        if (newIdleExpiresAt > session.AbsoluteExpiresAt)
            newIdleExpiresAt = session.AbsoluteExpiresAt;

        var successorSecret = RandomNumberGenerator.GetBytes(SecretLengthBytes);
        var successor = new RefreshToken(
            session.Id,
            SHA256.HashData(successorSecret),
            now,
            newIdleExpiresAt);

        // The session lock serializes competing refreshes. The conditional update is a second,
        // database-enforced guard against stale state and must affect exactly one row.
        var consumedRows = await dbContext.RefreshTokens
            .Where(candidate =>
                candidate.Id == token.Id &&
                candidate.SessionId == session.Id &&
                candidate.ConsumedAt == null &&
                candidate.RevokedAt == null &&
                candidate.ExpiresAt > now &&
                candidate.Session.RevokedAt == null &&
                candidate.Session.IdleExpiresAt > now &&
                candidate.Session.AbsoluteExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.ConsumedAt, now)
                    .SetProperty(candidate => candidate.ReplacedByTokenId, successor.Id),
                cancellationToken);

        if (consumedRows != 1)
            return new(RefreshTokenRotationStatus.Invalid);

        session.LastSeenAt = now;
        session.IdleExpiresAt = newIdleExpiresAt;
        dbContext.RefreshTokens.Add(successor);

        // Consume, session activity and successor insertion commit or roll back together.
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            RefreshTokenRotationStatus.Rotated,
            successor.Id,
            successorSecret,
            successor.ExpiresAt);
    }
}
