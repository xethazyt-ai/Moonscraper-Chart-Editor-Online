// See LICENSE in project root for license information.

using System.Collections.Generic;
using UnityEngine;
using MoonscraperChartEditor.Song;

/// <summary>
/// Other people's ghost notes, drawn by copies of the editor's own note tool so they look exactly like your own:
/// the current skin's strum, HOPO, tap, star power and open notes.
/// </summary>
public class DuetGhostNotes
{
    class Slot
    {
        public NoteController ghost;
        public Note note = new Note(0, 0);
    }

    readonly GameObject m_template;
    readonly Transform m_root;
    readonly List<Slot> m_slots = new List<Slot>();
    int m_used = 0;

    public DuetGhostNotes(GameObject template)
    {
        m_template = template;
        m_root = new GameObject("Duet Ghost Notes").transform;
    }

    public void Begin()
    {
        m_used = 0;
    }

    // Shows one ghost note this frame. Call between Begin and End.
    public void Show(DuetPresence.Ghost ghost)
    {
        ChartEditor editor = ChartEditor.Instance;
        if (ghost.tick < editor.minPos || ghost.tick > editor.maxPos)
            return;

        Slot slot = m_used < m_slots.Count ? m_slots[m_used] : AddSlot();
        ++m_used;
        Place(slot.ghost, slot.note, ghost);
    }

    public void End()
    {
        for (int i = m_used; i < m_slots.Count; ++i)
            Hide(m_slots[i].ghost);
    }

    public void HideAll()
    {
        Begin();
        End();
    }

    public void Destroy()
    {
        if (m_root != null)
            Object.Destroy(m_root.gameObject);
        m_slots.Clear();
    }

    static void Place(NoteController controller, Note note, DuetPresence.Ghost ghost)
    {
        ChartEditor editor = ChartEditor.Instance;
        note.song = editor.currentSong;
        note.chart = editor.currentChart;
        note.tick = ghost.tick;
        note.rawNote = ghost.rawNote;
        note.flags = (Note.Flags)ghost.flags;
        note.length = 0;
        PlaceNote.UpdatePrevAndNext(note, editor.currentChart);

        if (!controller.gameObject.activeSelf)
            controller.gameObject.SetActive(true);
        controller.note = note;     // Moves it into place and picks the visuals for this note
    }

    static void Hide(NoteController controller)
    {
        if (controller != null && controller.gameObject.activeSelf)
            controller.gameObject.SetActive(false);
    }

    Slot AddSlot()
    {
        Slot slot = new Slot();
        slot.ghost = MakeCopy("Duet Ghost Note");
        m_slots.Add(slot);
        return slot;
    }
    // A copy of the note tool that only draws: no placing notes, no clicks
    NoteController MakeCopy(string name)
    {
        // Made under a switched off parent so none of its scripts start before they're taken off
        GameObject holder = new GameObject("Duet Ghost Note Holder");
        holder.SetActive(false);
        GameObject copy = Object.Instantiate(m_template, holder.transform);
        copy.name = name;

        foreach (ToolObject tool in copy.GetComponentsInChildren<ToolObject>(true))
            Object.DestroyImmediate(tool);
        foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(collider);
        foreach (Collider2D collider in copy.GetComponentsInChildren<Collider2D>(true))
            Object.DestroyImmediate(collider);

        copy.SetActive(false);
        copy.transform.SetParent(m_root, false);
        Object.Destroy(holder);
        return copy.GetComponent<NoteController>();
    }
}
