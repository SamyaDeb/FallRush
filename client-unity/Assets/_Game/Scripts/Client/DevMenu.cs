namespace FallRush.Client
{
    /// <summary>
    /// P7 A1.7: dev-only UI (practice / tuning, the G4 hazards sandbox, the Feel Lab, the on-chain-Final-only entry,
    /// server URLs) shows in the Editor and development builds, and in a release build only with <c>?dev=1</c>
    /// (<c>-fallrushDev</c> on desktop), so we can still reach it on the public build.
    /// </summary>
    public static class DevMenu
    {
        private static int s_On = -1;

        public static bool Enabled
        {
            get
            {
                if (s_On < 0)
                    s_On = UnityEngine.Debug.isDebugBuild || FallRush.Shared.NetTargets.GetQueryParam(UnityEngine.Application.absoluteURL, "dev") == "1" ||
                           System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-fallrushDev") >= 0 ? 1 : 0;
                return s_On == 1;
            }
        }
    }
}
