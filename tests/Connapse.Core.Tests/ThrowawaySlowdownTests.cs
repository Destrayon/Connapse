using System.Security.Cryptography;
using FluentAssertions;

namespace Connapse.Core.Tests;

/// <summary>THROWAWAY (CON-145): one class whose tests each do seconds of real CPU work, one after another, to show the Small wall budget still fails a real slowdown. Reverted in the next commit.</summary>
[Trait("Category", "Unit")]
public class ThrowawaySlowdownTests
{
    private const int Rounds = 140_000;

    private static byte[] Work(int seed)
    {
        byte[] data = new byte[64 * 1024];
        new Random(seed).NextBytes(data);
        byte[] hash = SHA256.HashData(data);
        for (int i = 0; i < Rounds; i++)
        {
            hash.CopyTo(data, 0);
            hash = SHA256.HashData(data);
        }
        return hash;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Work_AnySeed_HashesToThirtyTwoBytes(int seed) =>
        Work(seed).Should().HaveCount(32);
}
