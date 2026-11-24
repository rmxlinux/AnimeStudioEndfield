using System;

namespace AnimeStudio
{
    /// <summary>
    /// Endfield's Animator muscle stream extends Unity's Humanoid body-muscle
    /// list with foot roll/toe axes.  The six extra axes are inserted before
    /// the finger stream, so treating the stream as stock Unity shifts the
    /// right leg and both arms (the visible 90-degree pose error).
    ///
    /// The game-side HumanTrait table is the authority for this order:
    /// Left/Right Foot Twist Roll, Toes Left-Right and Toes Twist Roll are
    /// present in addition to Unity's standard 55 body muscles.
    /// </summary>
    public static class EndfieldHumanoidLayout
    {
        // Global Animator binding indices.  Motion (0..6), Root (7..13)
        // and limb TRS (14..41) are unchanged.  Extended body muscle values
        // begin at 42; the six inserted axes are 70,72,73,81,83,84.
        public static bool TryMapToUnity(int sourceAttribute, out int unityAttribute)
        {
            unityAttribute = sourceAttribute;
            if (sourceAttribute < 0) return false;
            if (sourceAttribute <= 69) return true;
            if (sourceAttribute == 70 || (sourceAttribute >= 72 && sourceAttribute <= 73) ||
                sourceAttribute == 81 || (sourceAttribute >= 83 && sourceAttribute <= 84))
                return false; // Endfield-only foot roll/toe axes.
            if (sourceAttribute == 71) { unityAttribute = 70; return true; }
            if (sourceAttribute == 82) { unityAttribute = 78; return true; }
            if (sourceAttribute >= 74 && sourceAttribute <= 80)
            {
                unityAttribute = sourceAttribute - 3;
                return true;
            }
            // The remaining body values and the finger stream follow the six
            // inserted axes: 42+61+40=143 is the first Endfield TDoF channel.
            // In particular 137..142 are the last six finger muscles.
            unityAttribute = sourceAttribute - 6;
            return true;
        }

        public static string ToUnityProperty(int sourceAttribute, int[] version)
        {
            if (!TryMapToUnity(sourceAttribute, out var target))
                return string.Empty;
            return ((HumanoidMuscleType)target).Update(version).ToAttributeString();
        }

        public static string ToProperty(int sourceAttribute, int[] version) => sourceAttribute switch
        {
            70 => "Left Foot Twist Roll",
            72 => "Left Toes Left-Right",
            73 => "Left Toes Twist Roll",
            81 => "Right Foot Twist Roll",
            83 => "Right Toes Left-Right",
            84 => "Right Toes Twist Roll",
            _ => ToUnityProperty(sourceAttribute, version)
        };
    }
}
