using System.Runtime.InteropServices;

namespace PromptNavigator;

internal sealed class SuccessFeedback : IDisposable
{
  private readonly Form owner;
  private readonly Action resetCopyFeedback;
  private readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 1600 };
  private readonly System.Windows.Forms.Timer nudgeTimer = new() { Interval = 25 };
  private static readonly int[] NudgeOffsets = [-3, 3, -2, 2, -1, 0];
  private Point nudgeOrigin;
  private Point nudgePosition;
  private int nudgeFrame;

  internal PictureBox Comic { get; } = new()
  {
    Visible = false,
    SizeMode = PictureBoxSizeMode.Zoom,
    BackColor = Color.Transparent,
    AccessibleRole = AccessibleRole.Alert
  };

  internal bool IsNudging => nudgeTimer.Enabled;

  internal SuccessFeedback(Form owner, Control editorFrame, Action resetCopyFeedback)
  {
    this.owner = owner;
    this.resetCopyFeedback = resetCopyFeedback;
    displayTimer.Tick += (_, _) => Hide();
    nudgeTimer.Tick += (_, _) => AdvanceNudge();
    editorFrame.Controls.Add(Comic);
  }

  internal void ConfirmCopy()
  {
    displayTimer.Start();
    StartNudge();
  }

  /// <summary>Shows a comic success cloud over the editor, clamped inside its frame.</summary>
  internal void Show(string artwork)
  {
    var frame = Comic.Parent;
    if (frame is null) return;
    var logical = (int)Math.Round(128 * owner.DeviceDpi / 96.0);
    var side = Math.Min(logical, Math.Min(frame.ClientSize.Width, frame.ClientSize.Height) - 8);
    if (side < 16) return;
    Comic.Image?.Dispose();
    Comic.Image = ToolbarArtwork.LoadQuickIcon(artwork, side, 96);
    if (Comic.Image is null) return;
    Comic.AccessibleName = artwork == "sanitized_message" ? "Sanitized" : "De-sanitized";
    Comic.Bounds = new Rectangle(frame.ClientSize.Width - side - 4, 4, side, side);
    Comic.Visible = true;
    Comic.BringToFront();
    displayTimer.Stop();
    displayTimer.Start();
    StartNudge();
  }

  internal void Hide()
  {
    displayTimer.Stop();
    Comic.Visible = false;
    resetCopyFeedback();
    StopNudge();
  }

  private void StartNudge()
  {
    StopNudge();
    if (owner.WindowState != FormWindowState.Normal ||
        !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var animations, 0) ||
        !animations) return;
    nudgeOrigin = nudgePosition = owner.Location;
    nudgeFrame = 0;
    nudgeTimer.Start();
    AdvanceNudge();
  }

  [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SystemParametersInfo(uint action, uint parameter,
    [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

  private void AdvanceNudge()
  {
    // A user move or a window-state change takes precedence over the animation.
    if (owner.Location != nudgePosition || owner.WindowState != FormWindowState.Normal)
    {
      nudgeTimer.Stop();
      return;
    }
    var offset = (int)Math.Round(NudgeOffsets[nudgeFrame++] * owner.DeviceDpi / 96.0);
    nudgePosition = new Point(nudgeOrigin.X + offset, nudgeOrigin.Y);
    owner.Location = nudgePosition;
    if (nudgeFrame == NudgeOffsets.Length) nudgeTimer.Stop();
  }

  private void StopNudge()
  {
    if (!nudgeTimer.Enabled) return;
    nudgeTimer.Stop();
    if (owner.Location == nudgePosition && owner.WindowState == FormWindowState.Normal)
      owner.Location = nudgeOrigin;
  }

  public void Dispose()
  {
    displayTimer.Dispose();
    StopNudge();
    nudgeTimer.Dispose();
    Comic.Image?.Dispose();
    Comic.Image = null;
    Comic.Dispose();
  }
}
