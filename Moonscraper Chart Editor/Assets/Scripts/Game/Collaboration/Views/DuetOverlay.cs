// See LICENSE in project root for license information.

using System.Collections.Generic;
using UnityEngine;
using MoonscraperChartEditor.Song;

/// <summary>
/// Shows everyone else in a Duet session: their mouse cursor with their name, their ghost note on the highway, and a
/// marker beside the song bar on the right for where each person is. Also sends where you are.
/// Cursors and markers are drawn on top of the editor; ghost notes are drawn on the highway like real notes.
/// </summary>
public class DuetOverlay : MonoBehaviour
{
    const float MarkerSize = 12;

    ChartEditor m_editor;
    TimelineHandler m_timeline;
    readonly List<PlaceNote> m_placeNotes = new List<PlaceNote>();
    DuetPresence m_localPresence;
    DuetGhostNotes m_ghostNotes;

    Texture2D m_arrow;
    Texture2D m_marker;
    GUIStyle m_tagStyle;

    void Start()
    {
        m_editor = ChartEditor.Instance;
        DuetEditorSong.onRemoteSongInfoApplied += OnRemoteSongInfoApplied;

        // The note tool's ghost notes, found even while the tool is switched off
        foreach (PlaceNote placeNote in Resources.FindObjectsOfTypeAll<PlaceNote>())
        {
            if (placeNote.gameObject.scene.IsValid())
                m_placeNotes.Add(placeNote);
        }

        m_arrow = MakeArrowTexture(24, 34);
        m_marker = MakeMarkerTexture(32);

        GameObject template = null;
        foreach (PlaceNote placeNote in m_placeNotes)
        {
            NoteController controller = placeNote.GetComponent<NoteController>();
            if (controller != null && controller.noteObjectSelector != null)
            {
                template = placeNote.gameObject;
                break;
            }
        }
        if (template != null)
            m_ghostNotes = new DuetGhostNotes(template);
        else
            Debug.LogWarning("Duet: couldn't find the note tool, so other people's ghost notes won't show");
    }

    void OnDestroy()
    {
        DuetEditorSong.onRemoteSongInfoApplied -= OnRemoteSongInfoApplied;
        if (m_ghostNotes != null)
            m_ghostNotes.Destroy();
    }

    void OnRemoteSongInfoApplied()
    {
        m_editor.uiServices.editorPanels.displayProperties.UpdateSongNameText();
        m_editor.RepaintWindowText();
    }

    void Update()
    {
        DuetSession session = DuetPanel.activeSession;
        if (session == null || session.state != DuetSession.State.Connected)
        {
            m_localPresence = null;
            if (m_ghostNotes != null)
                m_ghostNotes.HideAll();
            return;
        }

        m_localPresence = ReadLocalPresence();
        session.SetLocalPresence(m_localPresence);
        ShowGhostNotes(session);
    }

    void ShowGhostNotes(DuetSession session)
    {
        if (m_ghostNotes == null)
            return;

        m_ghostNotes.Begin();
        if (DuetPanel.showGhostNotes && m_editor.currentState == ChartEditor.State.Editor && m_editor.currentSong != null && m_editor.currentChart != null)
        {
            foreach (KeyValuePair<int, DuetPresence> pair in session.presences)
            {
                if (!m_localPresence.IsOnSameChart(pair.Value))
                    continue;

                foreach (DuetPresence.Ghost ghost in pair.Value.ghosts)
                    m_ghostNotes.Show(ghost);
            }
        }
        m_ghostNotes.End();
    }

    DuetPresence ReadLocalPresence()
    {
        DuetPresence presence = new DuetPresence();
        presence.color = DuetPanel.localColor;
        presence.viewTick = m_editor.currentTickPos;
        presence.instrument = MenuBar.currentInstrument.ToString();
        presence.difficulty = MenuBar.currentDifficulty.ToString();
        presence.isTyping = DuetChat.isTyping;

        Vector2? mouse = m_editor.services.mouseMonitorSystem.world2DPosition;
        if (mouse != null && m_editor.currentState == ChartEditor.State.Editor)
        {
            Vector2 position = (Vector2)mouse;
            presence.hasPointer = true;
            presence.pointerX = position.x;
            presence.pointerTick = m_editor.currentSong.WorldYPositionToTick(position.y);

            if (!Globals.gameSettings.keysModeEnabled)
            {
                foreach (PlaceNote placeNote in m_placeNotes)
                {
                    if (placeNote != null && placeNote.gameObject.activeInHierarchy && placeNote.note != null)
                        presence.ghosts.Add(new DuetPresence.Ghost { tick = placeNote.note.tick, rawNote = placeNote.note.rawNote, flags = (int)placeNote.note.flags });
                }
            }
        }

        return presence;
    }

    void OnGUI()
    {
        DuetSession session = DuetPanel.activeSession;
        if (session == null || session.state != DuetSession.State.Connected || UnityEngine.Event.current.type != EventType.Repaint || DuetPanel.isHudHidden)
            return;

        GUI.depth = 10;     // Behind the Duet window
        GUI.matrix = Matrix4x4.identity;
        Color oldColor = GUI.color;

        if (m_tagStyle == null)
            m_tagStyle = MakeTagStyle();
        m_tagStyle.fontSize = Mathf.RoundToInt(13 * Mathf.Max(1f, Screen.height / 1080f));

        Camera camera = Camera.main;
        foreach (KeyValuePair<int, DuetPresence> pair in session.presences)
        {
            DuetPresence presence = pair.Value;
            DuetUser user = session.users.Find(u => u.id == pair.Key);
            string name = user != null ? user.name : "Someone";
            bool sameChart = m_localPresence == null || m_localPresence.IsOnSameChart(presence);

            if (presence.hasPointer)
                DrawPointer(camera, presence, sameChart ? name : name + " (" + presence.difficulty + " " + presence.instrument + ")");
        }

        DrawSongBarMarkers(session);

        GUI.color = oldColor;
    }

    // Screen position for a spot on the highway, in GUI coordinates (y down). False if it's off screen.
    bool HighwayToGui(Camera camera, float x, uint tick, out Vector2 gui)
    {
        Vector3 screen = camera.WorldToScreenPoint(new Vector3(x, m_editor.currentSong.TickToWorldYPosition(tick), 0));
        gui = new Vector2(screen.x, Screen.height - screen.y);
        return screen.z > 0 && screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
    }

    void DrawPointer(Camera camera, DuetPresence presence, string label)
    {
        Vector2 tip;
        if (!HighwayToGui(camera, presence.pointerX, presence.pointerTick, out tip))
            return;

        float scale = Mathf.Max(1f, Screen.height / 1080f);
        Color color = DuetPresence.GetColor(presence.color);

        GUIContent content = new GUIContent(label);
        Vector2 size = m_tagStyle.CalcSize(content);
        Rect arrowRect = new Rect(tip.x, tip.y, m_arrow.width * scale * 0.75f, m_arrow.height * scale * 0.75f);
        Rect tagRect = new Rect(tip.x + 16 * scale, tip.y + 20 * scale, size.x, size.y);
        if (DuetMenuCover.Covers(arrowRect) || DuetMenuCover.Covers(tagRect))
            return;     // Behind an open Moonscraper menu

        GUI.color = color;
        GUI.DrawTexture(arrowRect, m_arrow);

        m_tagStyle.normal.textColor = color;
        GUI.color = Color.white;
        GUI.Label(tagRect, content, m_tagStyle);
    }

    void DrawSongBarMarkers(DuetSession session)
    {
        if (m_timeline == null)
            m_timeline = FindObjectOfType<TimelineHandler>();
        if (m_timeline == null || !m_timeline.gameObject.activeInHierarchy || m_editor.currentSongLength <= 0)
            return;

        // The bar's corners on screen: 0 bottom left, 1 top left, 2 top right, 3 bottom right
        Vector3[] corners = new Vector3[4];
        ((RectTransform)m_timeline.transform).GetWorldCorners(corners);
        Camera uiCamera = m_editor.uiServices.uiCamera;
        Vector3 bottomRight = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[3]);
        Vector3 topRight = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[2]);

        float scale = Mathf.Max(1f, Screen.height / 1080f);
        float size = MarkerSize * scale;
        Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        string hovered = null;
        Rect hoveredRect = new Rect();

        List<KeyValuePair<string, DuetPresence>> everyone = new List<KeyValuePair<string, DuetPresence>>();
        if (m_localPresence != null)
            everyone.Add(new KeyValuePair<string, DuetPresence>("You", m_localPresence));
        foreach (KeyValuePair<int, DuetPresence> pair in session.presences)
        {
            DuetUser user = session.users.Find(u => u.id == pair.Key);
            everyone.Add(new KeyValuePair<string, DuetPresence>(user != null ? user.name : "Someone", pair.Value));
        }

        Song song = m_editor.currentSong;
        foreach (KeyValuePair<string, DuetPresence> person in everyone)
        {
            float fraction = Mathf.Clamp01(song.TickToTime(person.Value.viewTick, song.resolution) / m_editor.currentSongLength);
            float screenY = Mathf.Lerp(bottomRight.y, topRight.y, fraction);
            Rect rect = new Rect(Mathf.Min(bottomRight.x + 2 * scale, Screen.width - size), Screen.height - screenY - size / 2, size, size);

            if (DuetMenuCover.Covers(rect))
                continue;

            GUI.color = DuetPresence.GetColor(person.Value.color);
            GUI.DrawTexture(rect, m_marker);

            if (rect.Contains(mouse))
            {
                hovered = person.Key;
                hoveredRect = rect;
            }
        }

        // Name of the marker under the mouse, to the left of the bar so it stays on screen
        if (hovered != null)
        {
            GUIContent content = new GUIContent(hovered);
            Vector2 labelSize = m_tagStyle.CalcSize(content);
            m_tagStyle.normal.textColor = Color.white;
            GUI.color = Color.white;
            GUI.Label(new Rect(hoveredRect.x - labelSize.x - 30 * scale, hoveredRect.y - (labelSize.y - size) / 2, labelSize.x, labelSize.y), content, m_tagStyle);
        }
    }

    GUIStyle MakeTagStyle()
    {
        Texture2D background = new Texture2D(1, 1);
        background.SetPixel(0, 0, new Color(0, 0, 0, 0.7f));
        background.Apply();

        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.normal.background = background;
        style.fontStyle = FontStyle.Bold;
        style.padding = new RectOffset(5, 5, 2, 2);
        style.wordWrap = false;
        return style;
    }

    #region Textures, white so GUI.color tints them

    // A classic mouse pointer: white with a dark outline
    static Texture2D MakeArrowTexture(int width, int height)
    {
        Vector2[] shape =
        {
            new Vector2(0.02f, 0.02f), new Vector2(0.02f, 0.78f), new Vector2(0.24f, 0.60f), new Vector2(0.42f, 0.98f),
            new Vector2(0.58f, 0.92f), new Vector2(0.40f, 0.56f), new Vector2(0.70f, 0.56f),
        };

        bool[,] inside = new bool[width, height];
        for (int x = 0; x < width; ++x)
        {
            for (int y = 0; y < height; ++y)
                inside[x, y] = InPolygon(new Vector2((x + 0.5f) / width, (y + 0.5f) / height), shape);
        }

        Texture2D texture = NewTexture(width, height);
        for (int x = 0; x < width; ++x)
        {
            for (int y = 0; y < height; ++y)
            {
                Color pixel = Color.clear;
                if (inside[x, y])
                {
                    bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1
                        || !inside[x - 1, y] || !inside[x + 1, y] || !inside[x, y - 1] || !inside[x, y + 1];
                    pixel = edge ? new Color(0.05f, 0.05f, 0.05f, 1) : Color.white;
                }
                texture.SetPixel(x, height - 1 - y, pixel);     // Texture rows go bottom to top
            }
        }
        texture.Apply();
        return texture;
    }

    // A small triangle pointing left at the song bar
    static Texture2D MakeMarkerTexture(int size)
    {
        Vector2[] shape = { new Vector2(0.05f, 0.5f), new Vector2(0.95f, 0.05f), new Vector2(0.95f, 0.95f) };
        Texture2D texture = NewTexture(size, size);
        for (int x = 0; x < size; ++x)
        {
            for (int y = 0; y < size; ++y)
                texture.SetPixel(x, y, InPolygon(new Vector2((x + 0.5f) / size, (y + 0.5f) / size), shape) ? Color.white : Color.clear);
        }
        texture.Apply();
        return texture;
    }

    static Texture2D NewTexture(int width, int height)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        return texture;
    }

    static bool InPolygon(Vector2 point, Vector2[] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].y > point.y) != (polygon[j].y > point.y)
                && point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x)
                inside = !inside;
        }
        return inside;
    }

    #endregion
}
