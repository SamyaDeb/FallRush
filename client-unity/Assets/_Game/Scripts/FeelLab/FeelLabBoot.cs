using System;
using FallRush.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FallRush.FeelLab
{
    /// <summary>
    /// P1-N entry: the Feel Lab is the dev practice mode with an engine switch. The menu's "Feel Lab" button (dev builds
    /// or <c>-feellab</c>) sets an in-process flag and loads the map through <see cref="NetBootstrap.StartPractice"/>; on
    /// that map load this adds <see cref="FeelLabMode"/>, which replaces the practice component. With
    /// <c>-feellab -map &lt;Key|all&gt;</c> it starts straight away (smoke test). Classic play never sets the flag.
    /// </summary>
    public static class FeelLabBoot
    {
        public const string Flag = "fallrush.feellab";
        public static readonly string[] Maps = { "Sandbox", "BlockPartyDash", "GooTower", "StarScramble", "CrumbleShelf" };

        public static bool Requested => AppDomain.CurrentDomain.GetData(Flag) is bool b && b;

        public static bool ArgEnabled => HasArg("-feellab");

        public static void Enter(string key)
        {
            AppDomain.CurrentDomain.SetData(Flag, true);
            NetBootstrap.StartPractice(key);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
            if (ArgEnabled)
                AppDomain.CurrentDomain.SetData("fallrush.feellab.arg", true); // read by the menu button (shown with -feellab)
        }

        private static bool _autoStarted;

        private static void OnLoaded(Scene scene, LoadSceneMode mode)
        {
            if (ArgEnabled && !_autoStarted && GetArg("-map") != null && !NetBootstrap.IsMapScene(scene.name) && scene.name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _autoStarted = true;
                var m = GetArg("-map");
                Enter(m == "all" ? Maps[0] : m);
                return;
            }

            if (!Requested || !NetBootstrap.IsMapScene(scene.name) || !NetBootstrap.IsPractice)
                return;
            new GameObject("FeelLab").AddComponent<FeelLabMode>();
        }

        public static bool HasArg(string a)
        {
            foreach (var x in Environment.GetCommandLineArgs())
                if (x == a)
                    return true;
            return false;
        }

        public static string GetArg(string a)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == a)
                    return args[i + 1];
            return null;
        }
    }
}
