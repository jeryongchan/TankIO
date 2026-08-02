using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TankIO
{
    // longest continuous capital holds, best per name, saved to disk on change.
    // the one piece of state that survives a server restart; the world itself regenerates.
    // server-only: clients would be shown the board through something replicated, never this file.
    public static class Leaderboard
    {
        [Serializable]
        public struct Entry
        {
            public string Name;
            public double Seconds;
        }

        [Serializable]
        private class SaveData
        {
            public List<Entry> Entries = new List<Entry>();
        }

        private const int MaxEntries = 10;

        private static List<Entry> entries;

        public static IReadOnlyList<Entry> TopEntries
        {
            get
            {
                if (entries == null)
                    Load();
                return entries;
            }
        }

        static string FilePath
        {
            get { return Path.Combine(Application.persistentDataPath, "leaderboard.json"); }
        }

        static void Load()
        {
            entries = new List<Entry>();
            try
            {
                if (File.Exists(FilePath))
                    entries = JsonUtility.FromJson<SaveData>(File.ReadAllText(FilePath)).Entries;
            }
            catch (Exception exception)
            {
                // a corrupt or unreadable file starts the board empty rather than killing the server
                Debug.LogWarning($"leaderboard load failed, starting empty: {exception.Message}");
            }
        }

        public static void ReportHold(string name, double seconds)
        {
            if (entries == null)
                Load();

            // best per name: generated names recur across bot generations and restarts, and one
            // regular would otherwise fill the board with their runs
            int existingIndex = entries.FindIndex(entry => entry.Name == name);
            if (existingIndex >= 0)
            {
                if (entries[existingIndex].Seconds >= seconds)
                    return;
                entries.RemoveAt(existingIndex);
            }
            else if (entries.Count >= MaxEntries && seconds <= entries[entries.Count - 1].Seconds)
                return;

            entries.Add(new Entry { Name = name, Seconds = seconds });
            entries.Sort((a, b) => b.Seconds.CompareTo(a.Seconds));
            if (entries.Count > MaxEntries)
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            Save();
        }

        static void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(new SaveData { Entries = entries }));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"leaderboard save failed: {exception.Message}");
            }
        }
    }
}
