using System.Security.Cryptography;
using Ecommerce.Modules.Identity.Domain;
using Ecommerce.Modules.Identity.Persistence;
using Ecommerce.Modules.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Ecommerce.IntegrationTests.Identity;

[Collection("Identity database")]
public sealed class RefreshTokenRotationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RefreshTokenRotationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RotateAsync_ConsumesCurrentTokenAndPersistsOnlySuccessorHash()
    {
        var seed = await CreateSessionAsync();

        RefreshTokenRotationResult result;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());

            result = await service.RotateAsync(
                seed.TokenId,
                seed.Secret,
                TimeSpan.FromDays(7));
        }

        Assert.Equal(RefreshTokenRotationStatus.Rotated, result.Status);
        Assert.NotNull(result.SuccessorTokenId);
        Assert.NotNull(result.SuccessorSecret);
        Assert.Equal(32, result.SuccessorSecret!.Length);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(token => token.SessionId == seed.SessionId)
            .OrderBy(token => token.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, tokens.Count);
        var consumed = Assert.Single(tokens, token => token.Id == seed.TokenId);
        var successor = Assert.Single(tokens, token => token.Id == result.SuccessorTokenId);

        Assert.NotNull(consumed.ConsumedAt);
        Assert.Equal(successor.Id, consumed.ReplacedByTokenId);
        Assert.Null(successor.ConsumedAt);
        Assert.Null(successor.RevokedAt);
        Assert.Equal(SHA256.HashData(result.SuccessorSecret), successor.SecretHash);
        Assert.True(result.ExpiresAt <= (await db.AuthSessions.AsNoTracking()
            .SingleAsync(session => session.Id == seed.SessionId)).AbsoluteExpiresAt);
    }

    [Fact]
    public async Task RotateAsync_WrongSecretDoesNotRevokeSession()
    {
        var seed = await CreateSessionAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var service = new RefreshTokenRotationService(db);

        var result = await service.RotateAsync(
            seed.TokenId,
            new byte[32],
            TimeSpan.FromDays(7));

        Assert.Equal(RefreshTokenRotationStatus.Invalid, result.Status);
        var session = await db.AuthSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        var token = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.TokenId);

        Assert.Null(session.RevokedAt);
        Assert.Null(token.ConsumedAt);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task RotateAsync_ConsumedTokenReplayRevokesOnlyItsSessionAndActiveSuccessor()
    {
        var seed = await CreateSessionAsync();
        var unrelatedSession = await CreateSessionAsync(seed.UserId);
        Guid successorId;

        using (var scope = _factory.Services.CreateScope())
        {
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
            var rotation = await service.RotateAsync(seed.TokenId, seed.Secret, TimeSpan.FromDays(7));
            Assert.Equal(RefreshTokenRotationStatus.Rotated, rotation.Status);
            successorId = rotation.SuccessorTokenId!.Value;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
            var replay = await service.RotateAsync(seed.TokenId, seed.Secret, TimeSpan.FromDays(7));
            Assert.Equal(RefreshTokenRotationStatus.ReplayDetected, replay.Status);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await db.AuthSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        var successor = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == successorId);

        var otherSession = await db.AuthSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == unrelatedSession.SessionId);

        Assert.NotNull(session.RevokedAt);
        Assert.Equal("RefreshTokenReplay", session.RevocationReason);
        Assert.NotNull(successor.RevokedAt);
        Assert.Null(otherSession.RevokedAt);
    }



    [Fact]
    public async Task DatabaseIndex_RejectsSecondActiveRefreshTokenForSameSession()
    {
        var seed = await CreateSessionAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var now = DateTimeOffset.UtcNow;
        db.RefreshTokens.Add(new RefreshToken(
            seed.SessionId,
            RandomNumberGenerator.GetBytes(32),
            now,
            now.AddHours(1)));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task RotateAsync_SuccessorExpiryNeverExceedsAbsoluteSessionExpiry()
    {
        var seed = await CreateSessionAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
            var result = await service.RotateAsync(seed.TokenId, seed.Secret, TimeSpan.FromDays(60));
            Assert.Equal(RefreshTokenRotationStatus.Rotated, result.Status);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await db.AuthSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        var successor = await db.RefreshTokens.AsNoTracking()
            .SingleAsync(token => token.SessionId == seed.SessionId && token.ConsumedAt == null && token.RevokedAt == null);

        Assert.Equal(session.AbsoluteExpiresAt, session.IdleExpiresAt);
        Assert.Equal(session.AbsoluteExpiresAt, successor.ExpiresAt);
    }

    [Fact]
    public async Task RotateAsync_ConsumedTokenReplayIsDetectedAfterOriginalTokenExpiry()
    {
        var seed = await CreateSessionAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
            var rotation = await service.RotateAsync(seed.TokenId, seed.Secret, TimeSpan.FromDays(7));
            Assert.Equal(RefreshTokenRotationStatus.Rotated, rotation.Status);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE identity."RefreshTokens"
                SET "CreatedAt" = clock_timestamp() - interval '2 minutes',
                    "ExpiresAt" = clock_timestamp() - interval '1 minute'
                WHERE "Id" = {seed.TokenId}
                """);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
            var replay = await service.RotateAsync(seed.TokenId, seed.Secret, TimeSpan.FromDays(7));
            Assert.Equal(RefreshTokenRotationStatus.ReplayDetected, replay.Status);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await verifyDb.AuthSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        Assert.NotNull(session.RevokedAt);
    }

    [Fact]
    public async Task RotateAsync_ConcurrentSameTokenRequestsCannotCreateTwoSuccessors()
    {
        var seed = await CreateSessionAsync();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<RefreshTokenRotationResult> RotateInNewScopeAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var service = new RefreshTokenRotationService(
                scope.ServiceProvider.GetRequiredService<IdentityDbContext>());
            await start.Task;
            return await service.RotateAsync(seed.TokenId, seed.Secret, TimeSpan.FromDays(7));
        }

        var first = RotateInNewScopeAsync();
        var second = RotateInNewScopeAsync();
        start.SetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == RefreshTokenRotationStatus.Rotated);
        Assert.Single(results, result => result.Status == RefreshTokenRotationStatus.ReplayDetected);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var session = await db.AuthSessions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(token => token.SessionId == seed.SessionId)
            .ToListAsync();

        Assert.NotNull(session.RevokedAt);
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.NotNull(token.RevokedAt ?? token.ConsumedAt));
        Assert.Single(tokens, token => token.Id != seed.TokenId);
    }

    private async Task<SeededSession> CreateSessionAsync(Guid? existingUserId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.Database.MigrateAsync();

        ApplicationUser user;
        if (existingUserId is Guid userId)
        {
            user = await db.Users.SingleAsync(candidate => candidate.Id == userId);
        }
        else
        {
            user = new ApplicationUser
            {
                UserName = $"refresh-{Guid.NewGuid():N}",
                AccountType = AccountType.Customer
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var now = DateTimeOffset.UtcNow;
        var session = new AuthSession(
            user.Id,
            AccountType.Customer,
            AuthenticationMethod.Password,
            now,
            now.AddDays(30),
            now.AddDays(7));

        var secret = RandomNumberGenerator.GetBytes(32);
        var token = new RefreshToken(
            session.Id,
            SHA256.HashData(secret),
            now,
            now.AddDays(7));

        db.AuthSessions.Add(session);
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();

        Assert.Equal('7', session.Id.ToString("N")[12]);
        Assert.Equal('7', token.Id.ToString("N")[12]);

        return new SeededSession(user.Id, session.Id, token.Id, secret);
    }

    private sealed record SeededSession(Guid UserId, Guid SessionId, Guid TokenId, byte[] Secret);
}
