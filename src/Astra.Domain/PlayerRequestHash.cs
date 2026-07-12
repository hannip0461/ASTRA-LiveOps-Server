using System.Security.Cryptography;
using System.Text.Json;
using Astra.Contracts;

namespace Astra.Domain;

public static class PlayerRequestHash
{
    public static string GrantCurrency(
        Guid playerId,
        CurrencyCode currency,
        long amount,
        string reason) =>
        Create(new
        {
            operation = "wallet.grant.v1",
            playerId = playerId.ToString("N"),
            currency,
            amount,
            reason
        });

    public static string SpendCurrency(
        Guid playerId,
        CurrencyCode currency,
        long amount,
        string reason) =>
        Create(new
        {
            operation = "wallet.spend.v1",
            playerId = playerId.ToString("N"),
            currency,
            amount,
            reason
        });

    public static string DrawGacha(
        Guid playerId,
        string bannerId,
        int drawCount) =>
        Create(new
        {
            operation = "gacha.draw.v1",
            playerId = playerId.ToString("N"),
            bannerId,
            drawCount
        });

    public static string ClaimMail(Guid playerId, string mailId) =>
        Create(new
        {
            operation = "mail.claim.v1",
            playerId = playerId.ToString("N"),
            mailId
        });

    private static string Create<T>(T request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
