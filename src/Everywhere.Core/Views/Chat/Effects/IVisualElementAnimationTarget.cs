using Everywhere.Chat;

namespace Everywhere.Views;

public interface IVisualElementAnimationTarget
{
    /// <summary>
    /// Tries to get the center point of the specified attachment on the screen coordinates.
    /// This is used for the animation effect to determine where the visual element should fly to.
    /// </summary>
    /// <param name="attachment"></param>
    /// <param name="center"></param>
    /// <returns></returns>
    bool TryGetAttachmentCenterOnScreen(ChatAttachment attachment, out PixelPoint center);

    /// <summary>
    /// Tries to get the bounding rectangle of the Eva control on the screen coordinates.
    /// Used to create the inverted mask for absorption effect.
    /// </summary>
    /// <param name="bounds"></param>
    /// <returns></returns>
    bool TryGetEvaBoundsOnScreen(out PixelRect bounds);
}