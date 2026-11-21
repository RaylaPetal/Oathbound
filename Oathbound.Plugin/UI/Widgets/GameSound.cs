using System;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Oathbound.Plugin.UI;

/// The game's own chat sounds (<se.1> to <se.16>), at the player's sound effect volume.
public static class GameSound
{
    public const int Count = 16;
    private static bool failed;

    /// 0 (or out of range) plays nothing. After one failure, sound stays off for the session rather than throwing again.
    public static void Play(int chatSound)
    {
        if (chatSound is < 1 or > Count || failed)
            return;
        try
        {
            UIGlobals.PlayChatSoundEffect((uint)chatSound);
        }
        catch (Exception ex)
        {
            failed = true;
            Plugin.Log.Warning(ex, "Game sound failed - Oathbound sounds are off for this session.");
        }
    }
}
