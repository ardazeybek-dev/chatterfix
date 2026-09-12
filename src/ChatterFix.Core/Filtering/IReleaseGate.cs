namespace ChatterFix.Core.Filtering;

/// <summary>
/// Holds a release back for a short window so a dropped connection can be repaired.
///
/// A failing switch sometimes loses contact while the button is still held down.
/// Windows sees that as a release immediately followed by a press, which in a game
/// reads as letting go mid-swing. Holding the release for a few milliseconds makes
/// the difference visible: if a press arrives inside the window, the contact merely
/// bounced and both events are cancelled, so the hold never breaks.
/// </summary>
public interface IReleaseGate
{
    /// <summary>Holds the release for the given button until <paramref name="deadlineUs"/>.</summary>
    void Hold(MouseButton button, long deadlineUs);

    /// <summary>
    /// Cancels a held release. Returns true when the release was still pending and is
    /// now cancelled; false when it had already been sent, in which case the caller
    /// must treat the button as released.
    /// </summary>
    bool Cancel(MouseButton button);

    /// <summary>Sends every pending release immediately. Called on shutdown so nothing stays stuck.</summary>
    void FlushAll();
}
