using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // the one place a display name is decided; change the generator here and nothing else moves.
    // seeded on the commander id rather than picked from a shuffled pool, so the server stores no
    // name table, two machines never disagree, and a rejoining player gets the same name back.
    public static class CommanderNames
    {
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
    }
}
