using System.Security.Cryptography;

namespace Astra.Domain;

public interface IGachaRandomSource
{
    int Next(int exclusiveUpperBound);
}

public sealed class CryptographicGachaRandomSource : IGachaRandomSource
{
    public int Next(int exclusiveUpperBound) =>
        RandomNumberGenerator.GetInt32(exclusiveUpperBound);
}
