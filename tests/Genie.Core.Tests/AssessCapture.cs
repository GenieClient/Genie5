namespace Genie.Core.Tests;

/// <summary>
/// simtel12's live <c>assess</c> capture (public #313), verbatim, in DR's CRLF
/// wire framing — the single copy both assess test classes feed through the
/// parser.
///
/// <para>
/// Four creatures, numbered in assess order, each line linking
/// <c>look #ID</c> on the name and <c>face #ID</c> on the trailing "F".
/// Note the leading <c>pushStream</c>+<c>clearStream</c> pair, the blank line
/// after the header, and that every line re-pushes the stream after a
/// <c>popStream</c> — the block is not one continuous push.
/// </para>
///
/// <para>
/// It lives in its own file because two classes pin different things against
/// it: <see cref="AssessStreamCaptureTests"/> pins what the PARSER does with
/// the raw stream (no unknown tags, stream routing, link spans, framing),
/// while <see cref="AssessRowsTests"/> pins the structured rows the engine
/// builds from it. A second, drifting copy of the bytes would quietly
/// decouple those two halves.
/// </para>
/// </summary>
internal static class AssessCapture
{
    internal const string Block =
        "<pushStream id=\"assess\"/><clearStream id=\"assess\"/>You assess your combat situation...\r\n" +
        "\r\n" +
        "<popStream/><pushStream id=\"assess\"/>You (solidly balanced) are facing <d cmd='look #45029699'>a sleazy lout</d> (1) at pole weapon range.\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029699'>A sleazy lout</d> (1: nimbly balanced) is facing you at pole weapon range. | <d cmd='face #45029699'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029702'>A sleazy lout</d> (2: solidly balanced) is behind you at pole weapon range. | <d cmd='face #45029702'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029705'>A sleazy lout</d> (3: badly balanced) is behind you at pole weapon range. | <d cmd='face #45029705'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029711'>A sleazy lout</d> (4: badly balanced) is moving to flank you at pole weapon range. | <d cmd='face #45029711'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/>\r\n";
}
