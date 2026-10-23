// See LICENSE in project root for license information.

using UnityEngine;

/// <summary>
/// An IMGUI window that can be dragged by its title and resized by dragging any of its corners, like a Windows
/// window. Its size is remembered between runs.
/// </summary>
public class DuetWindow
{
    const float GripSize = 14;

    readonly int m_id;
    readonly string m_prefKey;
    readonly Vector2 m_minSize;
    readonly bool m_scrollWhenSized;

    public Rect rect;               // In GUI units, before scaling
    bool m_fixedHeight;             // Until it's resized, a window can grow to fit what's in it
    int m_corner = -1;              // While resizing: 0 top left, 1 top right, 2 bottom right, 3 bottom left
    Rect m_startRect;
    Vector2 m_startMouse;
    Vector2 m_scroll;
    GUI.WindowFunction m_content;
    float m_scale = 1;

    // scrollWhenSized: once someone picks a height, scroll the contents if they don't fit
    public DuetWindow(int id, string prefKey, Rect initial, Vector2 minSize, bool fixedHeight, bool scrollWhenSized)
    {
        m_id = id;
        m_prefKey = prefKey;
        m_minSize = minSize;
        m_scrollWhenSized = scrollWhenSized;
        rect = initial;
        m_fixedHeight = fixedHeight;

        float width = PlayerPrefs.GetFloat(prefKey + ".Width", 0);
        float height = PlayerPrefs.GetFloat(prefKey + ".Height", 0);
        if (width > 0)
            rect.width = Mathf.Max(width, minSize.x);
        if (height > 0)
        {
            rect.height = Mathf.Max(height, minSize.y);
            m_fixedHeight = true;
        }
    }

    public bool isResizing { get { return m_corner >= 0; } }

    public void Draw(string title, GUI.WindowFunction content, float scale)
    {
        m_scale = scale;
        if (m_corner >= 0)
            UpdateResize();

        m_content = content;
        if (m_fixedHeight)
            rect = GUILayout.Window(m_id, rect, DrawContent, title, GUILayout.Width(rect.width), GUILayout.Height(rect.height));
        else
            rect = GUILayout.Window(m_id, rect, DrawContent, title, GUILayout.Width(rect.width));
    }

    Vector2 MouseInGui()
    {
        return new Vector2(Input.mousePosition.x / m_scale, (Screen.height - Input.mousePosition.y) / m_scale);
    }

    void UpdateResize()
    {
        if (!Input.GetMouseButton(0))
        {
            m_corner = -1;
            PlayerPrefs.SetFloat(m_prefKey + ".Width", rect.width);
            PlayerPrefs.SetFloat(m_prefKey + ".Height", rect.height);
            return;
        }

        Vector2 delta = MouseInGui() - m_startMouse;
        Rect resized = m_startRect;
        bool left = m_corner == 0 || m_corner == 3;
        bool top = m_corner == 0 || m_corner == 1;

        if (left)
        {
            resized.width = Mathf.Max(m_minSize.x, m_startRect.width - delta.x);
            resized.x = m_startRect.xMax - resized.width;
        }
        else
        {
            resized.width = Mathf.Max(m_minSize.x, m_startRect.width + delta.x);
        }

        if (top)
        {
            resized.height = Mathf.Max(m_minSize.y, m_startRect.height - delta.y);
            resized.y = m_startRect.yMax - resized.height;
        }
        else
        {
            resized.height = Mathf.Max(m_minSize.y, m_startRect.height + delta.y);
        }

        rect = resized;
        m_fixedHeight = true;
    }

    void DrawContent(int id)
    {
        UnityEngine.Event e = UnityEngine.Event.current;
        float width = rect.width, height = rect.height;
        Rect[] grips =
        {
            new Rect(0, 0, GripSize, GripSize),
            new Rect(width - GripSize, 0, GripSize, GripSize),
            new Rect(width - GripSize, height - GripSize, GripSize, GripSize),
            new Rect(0, height - GripSize, GripSize, GripSize),
        };

        // Corners first, before the controls inside can take the click
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            for (int i = 0; i < grips.Length; ++i)
            {
                if (grips[i].Contains(e.mousePosition))
                {
                    m_corner = i;
                    m_startRect = rect;
                    m_startMouse = MouseInGui();
                    e.Use();
                    break;
                }
            }
        }

        if (m_fixedHeight && m_scrollWhenSized)
        {
            m_scroll = GUILayout.BeginScrollView(m_scroll);
            m_content(id);
            GUILayout.EndScrollView();
        }
        else
        {
            m_content(id);
        }

        // A few dots in the bottom right corner, so it's clear the window can be resized
        if (e.type == EventType.Repaint)
        {
            Color old = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.45f);
            for (int i = 0; i < 3; ++i)
            {
                for (int j = 0; j <= i; ++j)
                    GUI.DrawTexture(new Rect(width - 5 - j * 4, height - 5 - (i - j) * 4, 2, 2), Texture2D.whiteTexture);
            }
            GUI.color = old;
        }

        GUI.DragWindow();
    }
}
