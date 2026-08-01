using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Astra.TcpGateway;

internal sealed class TcpSessionTokenService(
    IOptions<TcpSessionTokenOptions> options,
    TimeProvider timeProvider)
{
    private readonly TcpSessionTokenOptions _options = options.Value;
    private readonly byte[] _signingKey = Encoding.UTF8.GetBytes(options.Value.SigningKey);

    public string Issue(Guid playerId, DateTimeOffset expiresAt)
    {
        var now = timeProvider.GetUtcNow();
        if (expiresAt <= now || expiresAt - now > _options.MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Token expiry is outside the allowed lifetime.");
        }

        var payload = $"{playerId:N}.{expiresAt.ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    public bool TryValidate(Guid playerId, string token, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2_048)
        {
            return false;
        }

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 ||
            !Guid.TryParseExact(parts[0], "N", out var tokenPlayerId) ||
            tokenPlayerId != playerId ||
            !long.TryParse(parts[1], out var expiresAtUnixSeconds))
        {
            return false;
        }

        var payload = $"{parts[0]}.{parts[1]}";
        byte[] suppliedSignature;
        try
        {
            suppliedSignature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(_signingKey);
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
        {
            return false;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds);
        var now = timeProvider.GetUtcNow();
        return expiresAt > now && expiresAt - now <= _options.MaxLifetime;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return Base64Url.EncodeToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
