using System.Collections.Generic;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// P7-UX (OC-44): one owner for the pointer lock in a match. Before, any click outside the settings panel re-locked the
    /// pointer, so the bet card / drop cards could not be clicked and an eliminated player was stuck with a locked mouse.
    /// Rules:
    ///   - an interactive panel (bet drawer, drops dock, receipt, settings, overlays) that is open frees the cursor;
    ///   - a spectator (out of the round or not in the match) plays with a free cursor (hold the right button to orbit);
    ///   - an alive player's click on the game (not on UI) locks it (WebGL: the lock needs that user gesture).
    /// The decisions are pure functions so EditMode tests cover them.
    /// </summary>
    public static class CursorPolicy
    {
        private static readonly HashSet<string> s_Open = new HashSet<string>();

        /// <summary>Set by MatchHud each frame: out in the Classic rounds (or a watcher without a slot).</summary>
        public static bool ClassicOut;

        /// <summary>Set by the on-chain economy HUD each frame: a spectator of the on-chain Final.</summary>
        public static bool ChainOut;

        public static bool Spectating => ClassicOut || ChainOut;

        /// <summary>Registers (or clears) an open interactive panel by key.</summary>
        public static void SetOpen(string panel, bool open)
        {
            if (open)
                s_Open.Add(panel);
            else if (s_Open.Remove(panel))
                ClosedFrame = Time.frameCount;
        }

        /// <summary>The frame a panel last closed (Esc closes the top panel, not also the settings).</summary>
        public static int ClosedFrame = -1;

        /// <summary>The frame the policy itself freed the cursor (that is not "Esc while playing").</summary>
        public static int PolicyFreeFrame = -1;

        /// <summary>A panel closed this frame or the last (scripts update in any order).</summary>
        public static bool JustClosed => Time.frameCount - ClosedFrame <= 1;

        public static bool IsOpen(string panel) => s_Open.Contains(panel);

        public static bool AnyPanelOpen => s_Open.Count > 0;

        /// <summary>Any open panel other than <paramref name="panel"/>.</summary>
        public static bool AnyOpenExcept(string panel) => s_Open.Count > (s_Open.Contains(panel) ? 1 : 0);

        /// <summary>Clears every flag (scene change).</summary>
        public static void Reset()
        {
            s_Open.Clear();
            ClassicOut = ChainOut = false;
        }

        /// <summary>The cursor must be free (unlock now).</summary>
        public static bool MustFree(bool panelOpen, bool spectating, bool overlay) => panelOpen || spectating || overlay;

        /// <summary>A click may take the pointer lock.</summary>
        public static bool MayLock(bool clicked, bool overUi, bool panelOpen, bool spectating, bool overlay, bool probe) =>
            clicked && !overUi && !MustFree(panelOpen, spectating, overlay) && !probe;

        /// <summary>The mouse orbits the camera: locked, or a spectator holding the right button over the game.</summary>
        public static bool Orbit(bool locked, bool spectating, bool rightHeld, bool overUi) => locked || spectating && rightHeld && !overUi;

        /// <summary>Applies the free rule now (called by the match view every frame).</summary>
        public static void Apply(bool overlay)
        {
            if (Cursor.lockState == CursorLockMode.Locked && MustFree(AnyPanelOpen, Spectating, overlay))
            {
                Cursor.lockState = CursorLockMode.None;
                PolicyFreeFrame = Time.frameCount;
            }
        }
    }
}
