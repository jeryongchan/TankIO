using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TankIO
{
    // the Tab overlay, toggled: the full online roster in a scroll list, rebuilt every frame while
    // open. rebuilding beats change-tracking here: the panel is open a few seconds at a time and the
    // time-played column ticks anyway.
    // one row is authored in the editor inside the scroll content; the script clones it to match the
    // online count. the authored footer row always shows the local player, so scrolling never loses
    // your own rank
    public class ScoreboardPanel : MonoBehaviour
    {
        [SerializeField]
        private GameObject panelRoot; // the Panel child, never the object holding this script: it gets SetActive(false)

        [SerializeField]
        private TMP_Text headerText;

        [SerializeField]
        private ScoreboardRowWidget rowTemplate; // the one authored row inside the scroll content

        [SerializeField]
        private ScoreboardRowWidget pinnedOwnRow; // the authored footer row outside the scroll view

        [SerializeField]
        private TMP_Text holderLineText;

        // CameraController skips wheel zoom while the roster is open, so the wheel scrolls the list
        public static bool IsOpen { get; private set; }

        private readonly List<ScoreboardRowWidget> rowPool = new List<ScoreboardRowWidget>();

        private static readonly List<Scoreboard.Row> sorted = new List<Scoreboard.Row>();

        void Awake()
        {
            rowPool.Add(rowTemplate);
        }

        void OnDestroy()
        {
            IsOpen = false;
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
                IsOpen = !IsOpen;
            bool show =
                IsOpen
                && Scoreboard.Instance != null
                && NetworkManager.Singleton != null
                && NetworkManager.Singleton.IsClient;
            if (panelRoot.activeSelf != show)
                panelRoot.SetActive(show);
            if (show)
                Rebuild();
        }

        void Rebuild()
        {
            double now = NetworkManager.Singleton.ServerTime.Time;
            ulong ownId = NetworkManager.Singleton.LocalClientId;

            ulong holderId = 0;
            double runningHold = 0.0;
            bool held = CapitalController.Instance != null && CapitalController.Instance.TryGetHolder(out holderId);
            if (held)
                runningHold = CapitalController.Instance.HoldSeconds(now);

            sorted.Clear();
            foreach (Scoreboard.Row row in Scoreboard.Instance.Rows)
                sorted.Add(row);
            // rank by best hold; the current holder counts its running hold, so it climbs while holding
            sorted.Sort(
                (a, b) =>
                {
                    double bestA = DisplayedHold(a, held, holderId, runningHold);
                    double bestB = DisplayedHold(b, held, holderId, runningHold);
                    if (bestA != bestB)
                        return bestB.CompareTo(bestA);
                    return a.JoinTime.CompareTo(b.JoinTime); // tie: longest online first
                }
            );

            headerText.text = "Commanders Online: " + sorted.Count;

            while (rowPool.Count < sorted.Count)
            {
                ScoreboardRowWidget clone = Instantiate(rowTemplate, rowTemplate.transform.parent);
                clone.transform.SetAsLastSibling(); // pool grows in rank order, so sibling order is rank order
                rowPool.Add(clone);
            }
            int ownRank = -1;
            for (int index = 0; index < rowPool.Count; index++)
            {
                if (index < sorted.Count)
                {
                    if (sorted[index].CommanderId == ownId)
                        ownRank = index;
                    Fill(rowPool[index], index, now, ownId, held, holderId, runningHold);
                }
                else
                    rowPool[index].Hide();
            }

            // ownRank < 0: a machine with no HQ of its own, like the dedicated server's view
            if (ownRank >= 0)
                Fill(pinnedOwnRow, ownRank, now, ownId, held, holderId, runningHold);
            else
                pinnedOwnRow.Blank();

            holderLineText.text = held
                ? "● " + NameOf(holderId) + " holding the capital - " + FormatDuration(runningHold)
                : "Capital currently unheld";
        }

        void Fill(
            ScoreboardRowWidget widget,
            int rank,
            double now,
            ulong ownId,
            bool held,
            ulong holderId,
            double runningHold
        )
        {
            Scoreboard.Row row = sorted[rank];
            double bestHold = DisplayedHold(row, held, holderId, runningHold);
            widget.Show(
                rank + 1,
                NameOf(row.CommanderId),
                FormatDuration(now - row.JoinTime),
                bestHold >= 1.0 ? FormatDuration(bestHold) : "-",
                row.CommanderId == ownId,
                held && row.CommanderId == holderId
            );
        }

        // the name rides the commander's HQ, which every client sees. the generated one covers the
        // frame where the row has arrived and the HQ has not, so a name never blinks empty
        static string NameOf(ulong commanderId)
        {
            string name = HqController.DisplayNameFor(commanderId);
            return name.Length > 0 ? name : CommanderNames.Generate(commanderId);
        }

        static double DisplayedHold(Scoreboard.Row row, bool held, ulong holderId, double runningHold)
        {
            if (held && row.CommanderId == holderId && runningHold > row.BestHoldSeconds)
                return runningHold;
            return row.BestHoldSeconds;
        }

        static string FormatDuration(double seconds)
        {
            int whole = (int)seconds;
            if (whole >= 3600)
                return whole / 3600 + "h " + whole % 3600 / 60 + "m";
            if (whole >= 60)
                return whole / 60 + "m " + whole % 60 + "s";
            return whole + "s";
        }
    }
}
