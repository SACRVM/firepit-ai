namespace Firepit.Core.Sessions;

/// <summary>
/// Whether the user has probably typed something into the agent's input line
/// and not sent it yet. Fed with the keystrokes the user sends to the PTY —
/// never with the agent's output, so the host stays transparent.
/// </summary>
/// <remarks>
/// <para>
/// Exists for automated prompt delivery. A session at
/// <see cref="SessionState.Embers"/> is not working, but it can still be
/// mid-sentence on the user's side: a pause to think is silent on the PTY.
/// A prompt written then lands on the end of the half-typed line and the
/// carriage return after it submits both.
/// </para>
/// <para>
/// A guess from keystrokes alone, and biased towards "unsent": typing sets
/// it, Enter and Ctrl+C clear it, and nothing else does. Backspacing a line
/// back to empty leaves it set, because the line is not visible from here.
/// That failure only delays an automated delivery until the next Enter; the
/// other one overwrites what the user was writing.
/// </para>
/// </remarks>
public sealed class UnsentInputTracker
{
    private const byte Escape = 0x1B;
    private const byte CarriageReturn = (byte)'\r';
    private const byte Interrupt = 0x03;
    private const byte Delete = 0x7F;

    /// <summary>Bracketed-paste start marker: pasted text, not a key or a terminal reply.</summary>
    private static ReadOnlySpan<byte> PasteStart => "\x1b[200~"u8;

    public bool HasUnsentInput { get; private set; }

    /// <summary>Observe one chunk the terminal sent towards the PTY.</summary>
    public void Observe(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty) return;

        if (input.StartsWith(PasteStart))
        {
            HasUnsentInput = true;
            return;
        }

        // Escape sequences carry arrow and function keys, but also everything
        // the terminal sends on its own: focus in/out, mouse reports, replies
        // to the agent's queries. None of it says anything about the line.
        if (input[0] == Escape) return;

        var last = input[^1];
        if (last is CarriageReturn or Interrupt)
        {
            HasUnsentInput = false;
            return;
        }

        foreach (var b in input)
        {
            if (b >= 0x20 && b != Delete)
            {
                HasUnsentInput = true;
                return;
            }
        }
    }

    /// <summary>A fresh agent process starts with an empty input line.</summary>
    public void Reset() => HasUnsentInput = false;
}
