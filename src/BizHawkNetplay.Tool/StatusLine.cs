using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace BizHawkNetplay.Tool;

/// <summary>
/// The one-line status bar under the tabs, drawn by us instead of by a <see cref="Label"/>.
///
/// A Label is the obvious control and was the original one, but assigning its Text costs far more
/// than building the string handed to it. <c>Control.Text</c> writes through to <c>SetWindowText</c>,
/// a synchronous <c>WM_SETTEXT</c> whose default handling raises an accessibility name-change event
/// to every hooked UIA/MSAA client in the process — a game overlay, a screen reader, a capture tool.
/// A LAN session on 2026-08-10 measured that one assignment at 1.8-3.2 ms on the thread that owns
/// the frame clock, worst case <c>ui 5.0 (pace 0.0 status 5.0 log 0.0 list 0.1)</c>. Everything
/// around it — the interpolated strings, <c>PacingStats.Summarize</c> — was microseconds. The floor
/// never dropped below 1.8 ms even on the refreshes that skip Summarize, which is what identified
/// the setter rather than the arithmetic.
///
/// The text also differs on every refresh, because it carries the frame counter, so no caller can
/// ever short-circuit the write. Keeping it in a field and invalidating moves the work to WM_PAINT,
/// which the message loop runs when it is idle instead of inline in the tick. Accessibility still
/// reads the line when it asks — <see cref="Text"/> is overridden, not abandoned — it simply is not
/// pushed a notification four times a second.
/// </summary>
internal sealed class StatusLine : Control
{
    private string _text = "";

    public StatusLine()
    {
        // UserPaint so nothing, border included, routes through the window class; double buffering
        // because the whole line repaints on every change and would otherwise flicker.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>
    /// The displayed text. Deliberately does not call the base setter — that is the SetWindowText
    /// path this control exists to avoid. Readers get the field, which is the same string the line
    /// is painted from, so nothing observes a stale value.
    /// </summary>
    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override string Text
    {
        get => _text;
        set
        {
            string next = value ?? "";
            if (_text == next) return;   // the frame counter means this rarely fires, but idle does
            _text = next;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Sunken, not SunkenOuter: BorderStyle.Fixed3D is WS_EX_CLIENTEDGE, a two-pixel edge, and
        // this is the ControlPaint spelling of the same thing. The background is already filled —
        // AllPaintingInWmPaint routes OnPaintBackground through this path — so no Clear here.
        ControlPaint.DrawBorder3D(e.Graphics, ClientRectangle, Border3DStyle.Sunken);
        // The extra 2px inset clears that border so a long line's ellipsis doesn't touch it.
        Rectangle text = Rectangle.FromLTRB(
            Padding.Left + 2, Padding.Top, Width - Padding.Right - 2, Height - Padding.Bottom);
        TextRenderer.DrawText(e.Graphics, _text, Font, text, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
