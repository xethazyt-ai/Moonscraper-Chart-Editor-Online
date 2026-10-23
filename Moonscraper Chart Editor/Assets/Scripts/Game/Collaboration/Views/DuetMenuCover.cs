// See LICENSE in project root for license information.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Where Moonscraper's open drop-down lists are on screen: the menu bar's File, Tools, Options and the rest, and
/// the other drop-downs such as Waveform. The Duet windows, name tags and cursors are drawn on top of everything,
/// so they hide wherever one of these lists would be under them, as if the list were in front.
/// </summary>
public static class DuetMenuCover
{
    const string ListName = "Dropdown List";    // What both kinds of drop-down call their list while it's open
    const int FindEveryFrames = 60;             // Looking for drop-downs is slow, and they rarely come and go

    static readonly List<Transform> s_dropdowns = new List<Transform>();
    static readonly List<Rect> s_lists = new List<Rect>();
    static readonly Vector3[] s_corners = new Vector3[4];
    static int s_foundFrame = -FindEveryFrames;
    static int s_updatedFrame = -1;

    // True if an open list overlaps this rect, given in screen pixels with y going down (unscaled GUI coordinates)
    public static bool Covers(Rect screenRect)
    {
        UpdateLists();
        foreach (Rect list in s_lists)
        {
            if (list.Overlaps(screenRect))
                return true;
        }
        return false;
    }

    // The same for a rect in scaled GUI units
    public static bool Covers(Rect guiRect, float scale)
    {
        return Covers(new Rect(guiRect.x * scale, guiRect.y * scale, guiRect.width * scale, guiRect.height * scale));
    }

    static void UpdateLists()
    {
        if (s_updatedFrame == Time.frameCount)
            return;
        s_updatedFrame = Time.frameCount;

        if (Time.frameCount - s_foundFrame >= FindEveryFrames)
        {
            s_foundFrame = Time.frameCount;
            s_dropdowns.Clear();
            foreach (Dropdown dropdown in Object.FindObjectsOfType<Dropdown>())
                s_dropdowns.Add(dropdown.transform);
            foreach (CustomUnityDropdown dropdown in Object.FindObjectsOfType<CustomUnityDropdown>())
                s_dropdowns.Add(dropdown.transform);
        }

        s_lists.Clear();
        foreach (Transform dropdown in s_dropdowns)
        {
            if (dropdown == null)
                continue;

            Transform list = dropdown.Find(ListName);
            RectTransform rectTransform = list != null ? list as RectTransform : null;
            if (rectTransform != null && list.gameObject.activeInHierarchy)
                s_lists.Add(ScreenRect(rectTransform));
        }
    }

    static Rect ScreenRect(RectTransform rectTransform)
    {
        Canvas canvas = rectTransform.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;

        rectTransform.GetWorldCorners(s_corners);
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
        foreach (Vector3 corner in s_corners)
        {
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, corner);
            min = Vector2.Min(min, screen);
            max = Vector2.Max(max, screen);
        }

        return new Rect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
    }
}
