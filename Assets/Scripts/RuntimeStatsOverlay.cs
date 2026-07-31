using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace TankIO
{
    // on-screen render/memory counters for build recordings, read via ProfilerRecorder so they come
    // from the player. draw call and instance rows sum the per-render-path counters unity 6 splits
    // them into; recorders invalid in release builds skip their row.
    public class RuntimeStatsOverlay : MonoBehaviour
    {
        private enum Format
        {
            Count,
            Bytes,
            Nanoseconds,
        }

        private struct Entry
        {
            public string Label;
            public ProfilerRecorder[] Recorders; // the row shows the sum of the valid ones
            public Format Format;
            public bool SpacerBefore; // section break above
        }

        private struct Row
        {
            public string Label;
            public string Value;
            public float GapBefore;
        }

        private const float Margin = 12f;
        private const float Width = 380f;
        private const int FontSize = 24;
        private const float PadX = 12f;
        private const float PadY = 8f;
        private const float RowGap = 6f; // between items; continuation lines (the culled %) get none
        private const float SectionGap = 16f;

        private static readonly string[] DrawCallCounters =
        {
            "Standard Draw Calls Count",
            "Standard Indirect Draw Calls Count",
            "Standard Instanced Draw Calls Count",
            "SRP Batcher Draw Calls Count",
            "BRG Draw Calls Count",
            "BRG Indirect Draw Calls Count",
        };

        private static readonly string[] InstanceCounters =
        {
            "Standard Instances Count",
            "Standard Indirect Instances Count",
            "Standard Instanced Instances Count",
            "SRP Batcher Instances Count",
            "BRG Instances Count",
            "BRG Indirect Instances Count",
        };

        [SerializeField]
        private bool anchorLeft; // LOD-on take anchors left so both readouts show at mid-slider

        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<Row> rows = new List<Row>();
        private readonly StringBuilder valueBuilder = new StringBuilder(64);
        private float smoothedDeltaTime;
        private bool visible = true;
        private GUIStyle labelStyle;
        private GUIStyle valueStyle;
        private float lineHeight;

        void OnEnable()
        {
            // vsync would clamp frame time to the display and hide what the LOD flip saves
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            Add("GPU Time", ProfilerCategory.Render, "GPU Frame Time", Format.Nanoseconds);
            Add("SetPass Calls", ProfilerCategory.Render, "SetPass Calls Count", spacerBefore: true);
            Add("Draw Calls", ProfilerCategory.Render, DrawCallCounters);
            Add("Instances", ProfilerCategory.Render, InstanceCounters);
            Add("Triangles", ProfilerCategory.Render, "Triangles Count");
            Add("Vertices", ProfilerCategory.Render, "Vertices Count");
            // stays put while the rows above collapse: instances share one texture set
            Add("Texture Memory", ProfilerCategory.Memory, "Texture Memory", Format.Bytes, spacerBefore: true);
            Add("Total Memory", ProfilerCategory.Memory, "Total Used Memory", Format.Bytes);
            // trimmed for the LOD clip; re-enable as needed
            // Add("Shadow Casters", ProfilerCategory.Render, "Shadow Casters Count");
            // Add("Render Textures", ProfilerCategory.Render, "Render Textures Count");
            // Add("RT Memory", ProfilerCategory.Render, "Render Textures Bytes", Format.Bytes);
            // Add("Used Buffers", ProfilerCategory.Render, "Used Buffers Count");
            // Add("Buffer Memory", ProfilerCategory.Render, "Used Buffers Bytes", Format.Bytes);
            // Add("GC Memory", ProfilerCategory.Memory, "GC Used Memory", Format.Bytes);
            // Add("GC Alloc / Frame", ProfilerCategory.Memory, "GC Allocated In Frame", Format.Bytes);
        }

        void Add(string label, ProfilerCategory category, string statName, Format format = Format.Count, bool spacerBefore = false)
        {
            Add(label, category, new[] { statName }, format, spacerBefore);
        }

        void Add(string label, ProfilerCategory category, string[] statNames, Format format = Format.Count, bool spacerBefore = false)
        {
            var recorders = new ProfilerRecorder[statNames.Length];
            for (int index = 0; index < statNames.Length; index++)
                recorders[index] = ProfilerRecorder.StartNew(category, statNames[index]);
            entries.Add(new Entry
            {
                Label = label,
                Recorders = recorders,
                Format = format,
                SpacerBefore = spacerBefore,
            });
        }

        void OnDisable()
        {
            // recorders hold native allocations; without Dispose they leak
            for (int index = 0; index < entries.Count; index++)
                for (int recorderIndex = 0; recorderIndex < entries[index].Recorders.Length; recorderIndex++)
                    entries[index].Recorders[recorderIndex].Dispose();
            entries.Clear();
        }

        void Update()
        {
            smoothedDeltaTime = Mathf.Lerp(smoothedDeltaTime, Time.unscaledDeltaTime, 0.05f);
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.digit1Key.wasPressedThisFrame)
                visible = !visible;
            if (keyboard != null && keyboard.digit5Key.wasPressedThisFrame)
                anchorLeft = !anchorLeft;
#else
            if (Input.GetKeyDown(KeyCode.Alpha1))
                visible = !visible;
            if (Input.GetKeyDown(KeyCode.Alpha5))
                anchorLeft = !anchorLeft;
#endif
        }

        void OnGUI()
        {
            if (!visible)
                return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = FontSize,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft,
                    padding = new RectOffset(0, 0, 0, 0),
                    richText = false,
                };
                labelStyle.normal.textColor = new Color(0.75f, 0.78f, 0.82f);
                valueStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.UpperRight };
                valueStyle.normal.textColor = Color.white;
                lineHeight = labelStyle.CalcHeight(new GUIContent("Ag"), 1000f);
            }

            BuildRows();

            float height = PadY * 2f;
            for (int index = 0; index < rows.Count; index++)
                height += rows[index].GapBefore + lineHeight;

            float x = anchorLeft ? Margin : Screen.width - Width - Margin;
            Rect area = new Rect(x, Margin, Width, height);
            GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.82f);
            GUI.DrawTexture(area, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float rowY = area.y + PadY;
            for (int index = 0; index < rows.Count; index++)
            {
                Row row = rows[index];
                rowY += row.GapBefore;
                var rowRect = new Rect(area.x + PadX, rowY, Width - PadX * 2f, lineHeight);
                DrawRow(rowRect, row.Label, labelStyle);
                DrawRow(rowRect, row.Value, valueStyle);
                rowY += lineHeight;
            }
        }

        // 1px dark offset pass keeps the text readable over bright terrain
        void DrawRow(Rect rect, string text, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text))
                return;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, style);
            GUI.color = Color.white;
            GUI.Label(rect, text, style);
        }

        void BuildRows()
        {
            rows.Clear();
            float ms = smoothedDeltaTime * 1000f;
            float fps = smoothedDeltaTime > 0f ? 1f / smoothedDeltaTime : 0f;
            rows.Add(new Row
            {
                Label = "Frame",
                Value = ms.ToString("0.0") + " ms  " + fps.ToString("0") + " fps",
            });

            for (int index = 0; index < entries.Count; index++)
            {
                Entry entry = entries[index];
                long sum = 0;
                bool anyValid = false;
                for (int recorderIndex = 0; recorderIndex < entry.Recorders.Length; recorderIndex++)
                {
                    ProfilerRecorder recorder = entry.Recorders[recorderIndex];
                    if (!recorder.Valid)
                        continue;
                    anyValid = true;
                    sum += recorder.LastValue;
                }
                if (!anyValid)
                    continue;
                string value;
                switch (entry.Format)
                {
                    case Format.Bytes:
                        value = FormatBytes(sum);
                        break;
                    case Format.Nanoseconds:
                        value = (sum / 1_000_000f).ToString("0.0") + " ms";
                        break;
                    default:
                        value = sum.ToString("N0");
                        break;
                }
                rows.Add(new Row
                {
                    Label = entry.Label,
                    Value = value,
                    GapBefore = entry.SpacerBefore ? SectionGap : RowGap,
                });
                if (entry.Label == "Vertices")
                    AddVegetationRows(); // grouped with the geometry rows
            }
        }

        // drawn/planted per group; no built-in counter attributes instances to a system, so the drawer reports its own
        void AddVegetationRows()
        {
            if (Time.frameCount - InstancedMeshDrawer.LastFrameStamp > 2)
                return; // far tier: drop the rows instead of freezing them
            var groups = InstancedMeshDrawer.StatGroups;
            for (int index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                if (group.Total <= 0)
                    continue;
                int culledPercent = Mathf.RoundToInt(100f * (group.Total - group.Drawn) / group.Total);
                rows.Add(new Row
                {
                    Label = group.Name,
                    Value = group.Drawn.ToString("N0") + " / " + group.Total.ToString("N0"),
                    GapBefore = RowGap,
                });
                rows.Add(new Row
                {
                    Value = culledPercent + "% culled", // continuation line: no label, no gap
                });
            }
        }

        string FormatBytes(long value)
        {
            valueBuilder.Clear();
            if (value < 1024 * 1024)
                valueBuilder.Append((value / 1024f).ToString("0.0")).Append(" KB");
            else
                valueBuilder.Append((value / (1024f * 1024f)).ToString("0.0")).Append(" MB");
            return valueBuilder.ToString();
        }

        // dumps every Render counter name to render_counters.txt, for when a documented name reports invalid
        [ContextMenu("Dump Render Counters")]
        void DumpRenderCounters()
        {
            var handles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
            Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
            var dump = new StringBuilder(8192);
            foreach (var handle in handles)
            {
                var description = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle);
                if (description.Category == ProfilerCategory.Render)
                    dump.AppendLine(description.Name);
            }
            string path = System.IO.Path.Combine(Application.dataPath, "..", "render_counters.txt");
            System.IO.File.WriteAllText(path, dump.ToString());
            Debug.Log("render counters written to " + path);
        }
    }
}
