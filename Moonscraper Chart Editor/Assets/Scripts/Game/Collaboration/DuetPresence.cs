// See LICENSE in project root for license information.

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where someone is in the song and what they're pointing at, sent many times a second so everyone can see each
/// other's cursor, ghost note and place in the song. Positions are in ticks so they line up on every screen,
/// whatever each person's hyperspeed or window size.
/// </summary>
public class DuetPresence
{
    // A note the person would place if they clicked now. Sent as the note itself, so everyone draws it with their own
    // skin and lane layout, and works out strum or HOPO from their copy of the chart.
    public struct Ghost
    {
        public uint tick;
        public int rawNote;
        public int flags;   // Note.Flags, e.g. tap or forced
    }

    public int color;
    public uint viewTick;           // The tick at the strikeline
    public string instrument = string.Empty;
    public string difficulty = string.Empty;

    public bool hasPointer;
    public float pointerX;
    public uint pointerTick;

    public List<Ghost> ghosts = new List<Ghost>();

    public bool isTyping;   // Writing a chat message

    public static readonly string[] ColorNames = { "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Pink" };

    static readonly Color[] s_colors =
    {
        new Color(0.94f, 0.27f, 0.27f),
        new Color(0.98f, 0.57f, 0.18f),
        new Color(0.98f, 0.85f, 0.20f),
        new Color(0.33f, 0.85f, 0.35f),
        new Color(0.25f, 0.85f, 0.90f),
        new Color(0.30f, 0.50f, 0.98f),
        new Color(0.66f, 0.40f, 0.95f),
        new Color(0.97f, 0.45f, 0.75f),
    };

    public static int ColorCount { get { return s_colors.Length; } }

    // A color is either one of the presets above, by its number, or any color at all: this flag plus 0xRRGGBB
    public const int CustomColorFlag = 0x1000000;

    public static bool IsCustomColor(int color) { return (color & CustomColorFlag) != 0; }

    public static int MakeCustomColor(Color color)
    {
        int r = Mathf.Clamp(Mathf.RoundToInt(color.r * 255), 0, 255);
        int g = Mathf.Clamp(Mathf.RoundToInt(color.g * 255), 0, 255);
        int b = Mathf.Clamp(Mathf.RoundToInt(color.b * 255), 0, 255);
        return CustomColorFlag | (r << 16) | (g << 8) | b;
    }

    public static Color GetColor(int color)
    {
        if (IsCustomColor(color))
            return new Color(((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f);

        if (color < 0 || color >= s_colors.Length)
            color = 0;
        return s_colors[color];
    }

    public static string GetColorName(int color)
    {
        if (!IsCustomColor(color))
            return ColorNames[color >= 0 && color < ColorNames.Length ? color : 0];
        return "#" + (color & 0xFFFFFF).ToString("X6");
    }

    public static bool IsValidColor(int color)
    {
        return IsCustomColor(color) ? (color & ~(CustomColorFlag | 0xFFFFFF)) == 0 : color >= 0 && color < s_colors.Length;
    }

    public bool IsOnSameChart(DuetPresence other)
    {
        return other != null && instrument == other.instrument && difficulty == other.difficulty;
    }

    public Dictionary<string, object> ToJson()
    {
        Dictionary<string, object> json = new Dictionary<string, object>
        {
            { "type", "presence" },
            { "color", (double)color },
            { "view", (double)viewTick },
            { "instrument", instrument },
            { "difficulty", difficulty },
        };

        if (isTyping)
            json["typing"] = true;

        if (hasPointer)
            json["pointer"] = new List<object> { (double)pointerX, (double)pointerTick };

        if (ghosts.Count > 0)
        {
            List<object> list = new List<object>();
            foreach (Ghost ghost in ghosts)
                list.Add(new List<object> { (double)ghost.tick, (double)ghost.rawNote, (double)ghost.flags });
            json["ghosts"] = list;
        }

        return json;
    }

    public static DuetPresence FromJson(Dictionary<string, object> json)
    {
        DuetPresence presence = new DuetPresence();
        presence.color = (int)DuetJson.GetNumber(json, "color");
        presence.viewTick = ToTick(DuetJson.GetNumber(json, "view"));
        presence.instrument = DuetJson.GetString(json, "instrument", string.Empty);
        presence.difficulty = DuetJson.GetString(json, "difficulty", string.Empty);

        object typing;
        presence.isTyping = json.TryGetValue("typing", out typing) && typing is bool && (bool)typing;

        List<object> pointer = DuetJson.GetArray(json, "pointer");
        if (pointer != null && pointer.Count >= 2 && pointer[0] is double && pointer[1] is double)
        {
            presence.hasPointer = true;
            presence.pointerX = (float)(double)pointer[0];
            presence.pointerTick = ToTick((double)pointer[1]);
        }

        List<object> ghosts = DuetJson.GetArray(json, "ghosts");
        if (ghosts != null)
        {
            foreach (object item in ghosts)
            {
                List<object> ghost = item as List<object>;
                if (ghost != null && ghost.Count >= 3 && ghost[0] is double && ghost[1] is double && ghost[2] is double)
                    presence.ghosts.Add(new Ghost { tick = ToTick((double)ghost[0]), rawNote = ToSmallInt((double)ghost[1]), flags = ToSmallInt((double)ghost[2]) });
            }
        }

        return presence;
    }

    static int ToSmallInt(double value)
    {
        if (double.IsNaN(value) || value < 0 || value > ushort.MaxValue)
            return 0;
        return (int)value;
    }

    static uint ToTick(double value)
    {
        if (value <= 0 || double.IsNaN(value))
            return 0;
        return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
    }
}
