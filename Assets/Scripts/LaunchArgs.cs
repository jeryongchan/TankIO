using System;

namespace TankIO
{
    // "-mapSize 500" style pairs from the launch command; absent flag leaves the inspector value in charge
    public static class LaunchArgs
    {
        public static bool TryGetInt(string flag, out int value)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(args[i + 1], out value))
                    return true;
            }
            value = 0;
            return false;
        }
    }
}
