using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>P7-UX: copy / share the room invite link (WebGL: clipboard + Web Share; elsewhere the system clipboard).</summary>
    public static class WebShare
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int FallRushCopy(string text);
        [DllImport("__Internal")] private static extern int FallRushShare(string title, string text, string url);
        [DllImport("__Internal")] private static extern string FallRushOrigin();
#endif

        public static bool Copy(string text)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { return FallRushCopy(text) == 1; } catch (Exception) { return false; }
#else
            GUIUtility.systemCopyBuffer = text;
            return true;
#endif
        }

        /// <summary>Opens the phone's share sheet; false when the browser has none (the caller copies instead).</summary>
        public static bool Share(string title, string text, string url)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { return FallRushShare(title, text, url) == 1; } catch (Exception) { return false; }
#else
            return false;
#endif
        }

        /// <summary>The page's origin (https://fallrush.fun), for invite links; empty outside the browser.</summary>
        public static string Origin()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { return FallRushOrigin() ?? string.Empty; } catch (Exception) { return string.Empty; }
#else
            return string.Empty;
#endif
        }

        /// <summary>The invite link for a room code.</summary>
        public static string InviteUrl(string code)
        {
            var o = Origin();
            if (string.IsNullOrEmpty(o))
                o = "https://fallrush.fun";
            return $"{o}/play?room={code}";
        }
    }
}
