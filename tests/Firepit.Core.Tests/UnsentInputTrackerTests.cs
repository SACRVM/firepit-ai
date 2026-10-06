using System.Text;
using Firepit.Core.Sessions;

namespace Firepit.Core.Tests;

public class UnsentInputTrackerTests
{
    private static UnsentInputTracker After(params string[] chunks)
    {
        var tracker = new UnsentInputTracker();
        foreach (var chunk in chunks)
        {
            tracker.Observe(Encoding.UTF8.GetBytes(chunk));
        }
        return tracker;
    }

    [Fact]
    public void StartsEmpty() => Assert.False(new UnsentInputTracker().HasUnsentInput);

    [Fact]
    public void TypingLeavesUnsentInput() =>
        Assert.True(After("k", "l", "e", "i", "n", "e", "r", " ", "b", "u", "g", ":", " ").HasUnsentInput);

    [Fact]
    public void NonAsciiTextCounts() => Assert.True(After("ä").HasUnsentInput);

    [Fact]
    public void EnterSendsTheLine() => Assert.False(After("h", "i", "\r").HasUnsentInput);

    [Fact]
    public void CtrlCDropsTheLine() => Assert.False(After("h", "i", "\x03").HasUnsentInput);

    [Fact]
    public void AChunkEndingInEnterSendsTheLine() => Assert.False(After("hi\r").HasUnsentInput);

    [Fact]
    public void PastedTextIsUnsent() => Assert.True(After("\x1b[200~some text\x1b[201~").HasUnsentInput);

    [Theory]
    [InlineData("\x1b[I")]      // focus in
    [InlineData("\x1b[O")]      // focus out
    [InlineData("\x1b[A")]      // arrow up
    [InlineData("\x1b[<0;10;5M")] // mouse report
    [InlineData("\x1b]11;rgb:1515/1111/0d0d\x1b\\")] // reply to a colour query
    [InlineData("\x1b")]        // Esc
    public void EscapeSequencesDoNotStartALine(string sequence) =>
        Assert.False(After(sequence).HasUnsentInput);

    [Fact]
    public void EscapeSequencesDoNotSendALine() =>
        // Terminals can send Alt+Enter as ESC CR; it is a newline in the
        // draft, not a submit.
        Assert.True(After("hi", "\x1b[I", "\x1b\r").HasUnsentInput);

    [Fact]
    public void BackspaceAloneDoesNotStartALine() => Assert.False(After("\x7f").HasUnsentInput);

    [Fact]
    public void BackspacingToEmptyStaysUnsent() =>
        // Deliberate: the line itself is not visible from the keystrokes, and
        // guessing "empty" wrongly is the failure that overwrites a draft.
        Assert.True(After("h", "\x7f").HasUnsentInput);

    [Fact]
    public void ResetClearsIt()
    {
        var tracker = After("hi");
        tracker.Reset();
        Assert.False(tracker.HasUnsentInput);
    }
}
