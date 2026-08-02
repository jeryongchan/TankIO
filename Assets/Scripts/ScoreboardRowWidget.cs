using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TankIO
{
    // one scoreboard row's texts, filled by ScoreboardPanel. finds its own parts instead of being
    // wired: the background is this object's Graphic, and the four texts are the children in
    // hierarchy order rank, name, time played, best hold
    public class ScoreboardRowWidget : MonoBehaviour
    {
        [SerializeField]
        private Color normalColor = new Color(0f, 0f, 0f, 0f);

        [SerializeField]
        private Color ownColor = new Color(0.39f, 0.84f, 0.67f, 0.25f); // the self mint, faint

        private TMP_Text rankText;
        private TMP_Text nameText;
        private TMP_Text timePlayedText;
        private TMP_Text bestHoldText;
        private Graphic background;

        void Awake()
        {
            background = GetComponent<Graphic>();
            TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
            rankText = texts[0];
            nameText = texts[1];
            timePlayedText = texts[2];
            bestHoldText = texts[3];
        }

        public void Show(int rank, string name, string timePlayed, string bestHold, bool own, bool holdsCapital)
        {
            gameObject.SetActive(true);
            rankText.text = rank.ToString();
            nameText.text = holdsCapital ? name + " ●" : name;
            timePlayedText.text = timePlayed;
            bestHoldText.text = bestHold;
            if (background != null)
                background.color = own ? ownColor : normalColor;
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }

        // still laid out, just empty: the pinned footer row keeps its slot so the holder line
        // underneath does not jump every time the list scrolls past your own row
        public void Blank()
        {
            gameObject.SetActive(true);
            rankText.text = "";
            nameText.text = "";
            timePlayedText.text = "";
            bestHoldText.text = "";
            if (background != null)
                background.color = new Color(0f, 0f, 0f, 0f);
        }
    }
}
