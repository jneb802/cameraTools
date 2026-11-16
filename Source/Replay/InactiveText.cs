using TMPro;
using UnityEngine;

namespace cameraTools.Replay
{
    /// <summary>
    /// Adds a TextMeshPro label with its font already set. TextMeshPro.Awake looks for a font, and Valheim ships no
    /// TMP default font (LiberationSans SDF), so a label added to an active object logs "The LiberationSans SDF Font
    /// Asset was not found" before the caller can set one: once per label, which flooded Player.log while the overlay
    /// grid was laid again on every seek. Here the object is made inactive first, so Awake waits until the caller
    /// activates it and finds the font set.
    /// </summary>
    internal static class InactiveText
    {
        /// <summary>Deactivates <paramref name="holder"/> and adds the label; the caller activates the holder.</summary>
        internal static TextMeshPro Add(GameObject holder, TMP_FontAsset? font)
        {
            holder.SetActive(false);
            TextMeshPro label = holder.AddComponent<TextMeshPro>();
            if (font != null) label.font = font;
            return label;
        }
    }
}
