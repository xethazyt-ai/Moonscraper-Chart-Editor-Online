// See LICENSE in project root for license information.

using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// A chat window during a Duet session, which can be dragged anywhere and scrolled back through. Press the chat key
/// (Enter unless rebound in Controls) to write, Enter to send, Escape to stop writing. Also shows who's typing.
/// </summary>
public class DuetChat : MonoBehaviour
{
    const int WindowId = 0x4476;    // Any number unique among IMGUI windows, next to the Duet window's
    const string InputName = "DuetChatInput";
    const string PrefX = "Duet.ChatX";
    const string PrefY = "Duet.ChatY";
    const float Width = 340;
    const float Height = 300;

    static DuetChat s_instance;

    ChartEditor m_editor;
    readonly DuetWindow m_window = new DuetWindow(WindowId, "Duet.Chat", new Rect(0, 0, Width, Height), new Vector2(220, 150), true, false);
    bool m_windowPlaced = false;
    Vector2 m_savedPosition;
    Vector2 m_scroll;
    int m_seenChatCount = -1;
    string m_text = string.Empty;
    bool m_writing = false;
    bool m_focusRequested = false;
    int m_startedFrame = -1;
    GUIStyle m_lineStyle;
    GUIStyle m_typingStyle;

    // True while writing a message, so the editor's shortcuts leave the keys alone
    public static bool isTyping { get { return s_instance != null && s_instance.m_writing; } }

    public static bool IsPointerOver()
    {
        if (s_instance == null || !s_instance.isShown)
            return false;

        float scale = DuetPanel.GuiScale;
        Vector2 pointer = new Vector2(Input.mousePosition.x / scale, (Screen.height - Input.mousePosition.y) / scale);
        return s_instance.m_window.rect.Contains(pointer) && !DuetMenuCover.Covers(s_instance.m_window.rect, scale);
    }

    bool isShown
    {
        get
        {
            DuetSession session = DuetPanel.activeSession;
            return session != null && session.state == DuetSession.State.Connected && !DuetPanel.isHudHidden;
        }
    }

    void Start()
    {
        s_instance = this;
        m_editor = ChartEditor.Instance;
    }

    void Update()
    {
        if (!isShown)
        {
            if (m_writing)
                StopWriting();
            return;
        }

        // Enter also sets lyric times, so leave it to the lyric editor while that's open
        if (!m_writing && MSChartEditorInput.GetInputDown(MSChartEditorInputActions.DuetChat) && m_editor.currentState == ChartEditor.State.Editor
            && FindObjectOfType<LyricEditor2Controller>() == null)
            StartWriting(Time.frameCount);
    }

    void StartWriting(int frame)
    {
        m_writing = true;
        m_focusRequested = true;
        m_startedFrame = frame;
    }

    void StopWriting()
    {
        m_writing = false;
        m_focusRequested = false;
        GUIUtility.keyboardControl = 0;
    }

    void OnGUI()
    {
        if (!isShown)
            return;

        if (m_lineStyle == null)
            MakeStyles();

        float scale = DuetPanel.GuiScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        if (!m_windowPlaced)
        {
            m_window.rect.x = PlayerPrefs.GetFloat(PrefX, 20);
            m_window.rect.y = PlayerPrefs.GetFloat(PrefY, Screen.height / scale - m_window.rect.height - 60);
            m_savedPosition = m_window.rect.position;
            m_windowPlaced = true;
        }

        // Clicking outside the window while writing stops writing
        if (m_writing && UnityEngine.Event.current.type == EventType.MouseDown && !m_window.rect.Contains(UnityEngine.Event.current.mousePosition))
            StopWriting();

        // An open Moonscraper menu goes in front of the window, so the window steps out of its way
        if (DuetMenuCover.Covers(m_window.rect, scale))
            return;

        m_window.Draw("Chat", DrawWindow, scale);

        // Keep it on screen, and remember where it was put
        m_window.rect.x = Mathf.Clamp(m_window.rect.x, 0, Mathf.Max(0, Screen.width / scale - m_window.rect.width));
        m_window.rect.y = Mathf.Clamp(m_window.rect.y, 0, Mathf.Max(0, Screen.height / scale - m_window.rect.height));
        if (m_window.rect.position != m_savedPosition && !Input.GetMouseButton(0))
        {
            m_savedPosition = m_window.rect.position;
            PlayerPrefs.SetFloat(PrefX, m_savedPosition.x);
            PlayerPrefs.SetFloat(PrefY, m_savedPosition.y);
        }
    }

    void DrawWindow(int id)
    {
        DuetSession session = DuetPanel.activeSession;
        UnityEngine.Event e = UnityEngine.Event.current;

        // Jump to the newest message when one arrives
        if (session.chatCount != m_seenChatCount)
        {
            m_seenChatCount = session.chatCount;
            m_scroll.y = float.MaxValue;
        }

        m_scroll = GUILayout.BeginScrollView(m_scroll, GUILayout.ExpandHeight(true));
        if (session.chat.Count == 0)
            GUILayout.Label("No messages yet.", m_typingStyle);
        foreach (DuetChatLine line in session.chat)
            GUILayout.Label(FormatLine(session, line), m_lineStyle);
        GUILayout.EndScrollView();

        string typing = TypingText(session);
        GUILayout.Label(typing ?? " ", m_typingStyle);

        if (m_writing && e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == InputName && Time.frameCount != m_startedFrame)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                session.SendChat(m_text);
                m_text = string.Empty;
                StopWriting();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                StopWriting();
                e.Use();
            }
        }

        GUI.SetNextControlName(InputName);
        string text = GUILayout.TextField(m_text, DuetSession.MaxChatLength);
        if (m_writing)
            m_text = text;

        if (m_focusRequested && e.type == EventType.Repaint)
        {
            GUI.FocusControl(InputName);
            m_focusRequested = false;
        }
        else if (!m_writing && e.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == InputName)
        {
            StartWriting(-1);   // Clicked into the box
            m_focusRequested = false;
        }
        else if (m_writing && !m_focusRequested && e.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != InputName)
        {
            m_writing = false;  // Focus went somewhere else, such as the Duet window
        }

        GUILayout.Label(m_writing ? "Enter to send, Escape to stop" : "Press Enter (or your chat key) to chat", m_typingStyle);
    }

    string TypingText(DuetSession session)
    {
        List<string> names = new List<string>();
        foreach (KeyValuePair<int, DuetPresence> pair in session.presences)
        {
            if (pair.Value.isTyping)
            {
                DuetUser user = session.users.Find(u => u.id == pair.Key);
                names.Add(user != null ? user.name : "Someone");
            }
        }

        if (names.Count == 0)
            return null;
        if (names.Count == 1)
            return names[0] + " is typing...";
        return string.Join(", ", names.GetRange(0, names.Count - 1).ToArray()) + " and " + names[names.Count - 1] + " are typing...";
    }

    static string FormatLine(DuetSession session, DuetChatLine line)
    {
        Color color = Color.white;
        DuetPresence presence;
        if (line.userId == session.localUserId)
            color = DuetPresence.GetColor(DuetPanel.localColor);
        else if (session.presences.TryGetValue(line.userId, out presence))
            color = DuetPresence.GetColor(presence.color);

        return "<b><color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + Escape(line.name) + "</color></b>: " + Escape(line.text);
    }

    // Stops anything typed from being read as formatting
    static string Escape(string text)
    {
        StringBuilder escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            escaped.Append(c);
            if (c == '<')
                escaped.Append('​');
        }
        return escaped.ToString();
    }

    void MakeStyles()
    {
        m_lineStyle = new GUIStyle(GUI.skin.label);
        m_lineStyle.richText = true;
        m_lineStyle.wordWrap = true;

        m_typingStyle = new GUIStyle(GUI.skin.label);
        m_typingStyle.fontStyle = FontStyle.Italic;
        m_typingStyle.fontSize = Mathf.Max(10, GUI.skin.label.fontSize - 1);
        m_typingStyle.normal.textColor = new Color(1, 1, 1, 0.6f);
    }
}
