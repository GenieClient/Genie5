using System;
using System.Linq;
using System.Text;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #280 — <c>GameConnection</c>'s read loop decoded each socket read
/// independently, so a multi-byte UTF-8 sequence straddling the 8 KB boundary
/// became replacement characters. DR output is overwhelmingly ASCII, which is
/// why this could only ever surface as an unreproducible "weird character in
/// my log" report.
/// </summary>
public class Utf8SplitReadTests
{
    /// <summary>
    /// The read loop used to call <c>Encoding.UTF8.GetString</c> on each socket
    /// read independently, so a character whose bytes straddled the 8 KB
    /// boundary decoded to replacement characters. A stateful decoder carries
    /// the partial sequence across reads instead. This asserts the decoder
    /// contract the read loop now depends on.
    /// </summary>
    [Fact]
    public void A_stateful_decoder_rejoins_a_character_split_across_reads()
    {
        // "Se'Karan — “quoted”" exercises an em dash and curly quotes: the
        // three-byte sequences DR actually emits in item and room text.
        var full  = "Se'Karan \u2014 \u201cquoted\u201d";
        var bytes = Encoding.UTF8.GetBytes(full);

        // Split mid-character: walk to a byte that is a UTF-8 continuation
        // (10xxxxxx), so the first chunk ends half way through a sequence.
        var split = Enumerable.Range(1, bytes.Length - 1)
                              .First(i => (bytes[i] & 0xC0) == 0x80);

        var decoder = Encoding.UTF8.GetDecoder();
        var sb      = new StringBuilder();
        var chars   = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];

        foreach (var chunk in new[] { bytes[..split], bytes[split..] })
        {
            var n = decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
            sb.Append(chars, 0, n);
        }

        Assert.Equal(full, sb.ToString());
        Assert.DoesNotContain('\uFFFD', sb.ToString());

        // And the old behaviour is what the fix replaced: decoding the same two
        // chunks independently corrupts the split character.
        var naive = Encoding.UTF8.GetString(bytes[..split]) + Encoding.UTF8.GetString(bytes[split..]);
        Assert.Contains('\uFFFD', naive);
    }
}
