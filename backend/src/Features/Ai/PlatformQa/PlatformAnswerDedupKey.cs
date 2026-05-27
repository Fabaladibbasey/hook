using System.IO.Hashing;
using System.Text;

namespace Hook.Features.Ai.PlatformQa;

internal static class PlatformAnswerDedupKey
{
    internal static long Of(string text) =>
        unchecked((long)XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text)));
}
