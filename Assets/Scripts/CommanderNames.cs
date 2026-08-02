using System.Collections.Generic;
using System.Text;
using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // the one place a display name is decided; change the generator here and nothing else moves.
    // seeded on the commander id rather than picked from a shuffled pool, so bots need no name
    // table, two machines never disagree, and a rejoining player gets the same name back.
    // a human can override theirs on the title screen, which is the only case the server stores.
    public static class CommanderNames
    {
        private const int MaxCharacters = 14;

        // server side only, and only for humans who typed a name: it is copied onto the HQ at spawn
        // and replicated from there, so no client ever reads this
        private static readonly Dictionary<ulong, string> chosen = new Dictionary<ulong, string>();
        private static readonly string[] Roots =
        {
            "Volkov", "Falken", "Rurik", "Kestrel", "Brandt", "Novak",
            "Halberd", "Ironside", "Marek", "Dorn", "Ashgrove", "Vasquez"
        };

        // must fit FixedString32Bytes, which holds 29 bytes; the longest root plus 2 digits is 10
        public static string Generate(ulong commanderId)
        {
            var rng = Random.CreateFromIndex((uint)commanderId);
            return Roots[rng.NextInt(Roots.Length)] + rng.NextInt(10, 100);
        }

        // what the HQ is named at spawn: the typed name if there is one, otherwise the generated one
        public static string ForCommander(ulong commanderId)
        {
            return chosen.TryGetValue(commanderId, out string name) ? name : Generate(commanderId);
        }

        // the connection payload, which is empty for a bot and for any build that sends no name
        public static void ServerRemember(ulong commanderId, byte[] utf8)
        {
            if (utf8 == null || utf8.Length == 0)
                return;
            string clean = Clean(Encoding.UTF8.GetString(utf8));
            if (clean.Length > 0)
                chosen[commanderId] = clean;
        }

        public static void ServerForget(ulong commanderId)
        {
            chosen.Remove(commanderId);
        }

        static string Clean(string typed)
        {
            string trimmed = typed == null ? "" : typed.Trim();
            if (trimmed.Length > MaxCharacters)
                trimmed = trimmed.Substring(0, MaxCharacters);
            // one typed character costs up to four of the 29 bytes, so the character cap alone
            // cannot keep a name inside FixedString32Bytes
            while (Encoding.UTF8.GetByteCount(trimmed) > 29)
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            return trimmed;
        }
    }
}
