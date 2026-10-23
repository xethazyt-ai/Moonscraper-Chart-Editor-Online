// See LICENSE in project root for license information.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MoonscraperEngine;
using MoonscraperChartEditor.Song;
using MoonscraperChartEditor.Song.IO;

/// <summary>
/// Connects a Duet Mode session to the song open in the editor.
/// </summary>
public class DuetEditorSong : IDuetSongHost, ISongEditChangeTarget
{
    // Fired after someone else's edit has been applied to the editor's song
    public static event System.Action<IList<SongEditChange>> onRemoteEditApplied;

    // Fired after someone else changed the song's details, so windows showing them can refresh
    public static event System.Action onRemoteSongInfoApplied;

    readonly ChartEditor m_editor;
    readonly SongEditGestureTracker m_tracker;

    // Joiners only: audio files the host's song uses that haven't arrived from the host (yet)
    public List<string> missingAudioFiles { get; private set; }

    // Joiners only: which file each audio stem uses on the host, and the host's files once they're here
    Dictionary<Song.AudioInstrument, string> m_audioFileNames = new Dictionary<Song.AudioInstrument, string>();
    Dictionary<string, string> m_songFiles = new Dictionary<string, string>();

    public DuetEditorSong(ChartEditor editor, SongEditGestureTracker tracker)
    {
        m_editor = editor;
        m_tracker = tracker;
        missingAudioFiles = new List<string>();
    }

    static string WorkingDirectory
    {
        get
        {
            string directory = Path.Combine(Application.persistentDataPath, "DuetSession");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public bool isBusy
    {
        get { return m_tracker.hasPendingEdit || m_editor.isLoadingCollaborationSong || m_editor.services.loadingTasksManager.isRunningTask; }
    }

    public bool TryApplyRemoteEdit(IList<SongEditChange> changes, out string failure)
    {
        // Keep the local selection on the same objects, as far as they still exist
        List<SongObject> selection = new List<SongObject>();
        foreach (SongObject songObject in m_editor.selectedObjectsManager.currentSelectedObjects)
            selection.Add(songObject.Clone());

        bool success;
        using (SongEditJournal.Suppress())
        {
            success = SongEditChangeApplier.TryApplyStrict(this, changes, out failure);
        }

        m_editor.currentSong.UpdateCache();
        m_editor.currentSong.UpdateAllChartCaches();
        m_editor.songObjectPoolManager.SetAllPoolsDirty();
        m_editor.selectedObjectsManager.TryFindAndSelectSongObjects(selection);

        if (success)
        {
            ChartEditor.isDirty = true;
            if (onRemoteEditApplied != null)
                onRemoteEditApplied(changes);
        }

        return success;
    }

    public SongObject Find(ChartLocation location, SongObject songObject)
    {
        Song song = m_editor.currentSong;
        return SongEditChangeApplier.Find(song, location.GetChart(song), songObject);
    }

    // Applies through the same actions local edits use, so notes, sustains and the highway refresh as usual
    public void Apply(SongEditChange change)
    {
        using (SongEditTarget.Use(change.location.GetChart(m_editor.currentSong)))
        {
            switch (change.type)
            {
                case SongEditChange.Type.Add:
                    AddAction.ApplyAction(change.after);
                    break;

                case SongEditChange.Type.Delete:
                    DeleteAction.ApplyAction(change.before);
                    break;

                case SongEditChange.Type.Modify:
                    DeleteAction.ApplyAction(change.before);
                    AddAction.ApplyAction(change.after);
                    break;
            }
        }
    }

    public Dictionary<string, string> ReadSongInfo()
    {
        return DuetSongInfo.Read(m_editor.currentSong);
    }

    public void ApplySongInfo(Dictionary<string, string> info)
    {
        DuetSongInfo.Apply(m_editor.currentSong, info);
        ChartEditor.isDirty = true;
        if (onRemoteSongInfoApplied != null)
            onRemoteSongInfoApplied();
    }

    public List<string> GetSongFilePaths()
    {
        Song song = m_editor.currentSong;
        List<string> audio = new List<string>();
        foreach (Song.AudioInstrument instrument in EnumX<Song.AudioInstrument>.Values)
            audio.Add(song.GetAudioLocation(instrument));

        string chartFile = m_editor.currentChartFile;
        return DuetFiles.FindSongFiles(string.IsNullOrEmpty(chartFile) ? null : Path.GetDirectoryName(chartFile), audio);
    }

    public void UseSongFiles(Dictionary<string, string> localPaths)
    {
        m_songFiles = new Dictionary<string, string>(localPaths, System.StringComparer.OrdinalIgnoreCase);

        Song song = m_editor.currentSong;
        missingAudioFiles.Clear();
        foreach (KeyValuePair<Song.AudioInstrument, string> pair in m_audioFileNames)
        {
            string path;
            if (m_songFiles.TryGetValue(pair.Value, out path))
                song.SetAudioLocation(pair.Key, path);
            else if (!missingAudioFiles.Contains(pair.Value))
                missingAudioFiles.Add(pair.Value);
        }

        m_editor.ReloadAudio();
    }

    public DuetSongSnapshot TakeSnapshot()
    {
        Song song = m_editor.currentSong;
        DuetSongSnapshot snapshot = new DuetSongSnapshot();

        // Audio file locations only make sense on this computer, so send file names separately
        Song copy = new Song(song);
        foreach (Song.AudioInstrument audio in EnumX<Song.AudioInstrument>.Values)
        {
            string location = song.GetAudioLocation(audio);
            if (!string.IsNullOrEmpty(location))
                snapshot.audioFileNames[audio] = Path.GetFileName(location);

            copy.SetAudioLocation(audio, string.Empty);
        }

        string path = Path.Combine(WorkingDirectory, "host-snapshot" + MsceIOHelper.FileExtention);
        ChartWriter.ErrorReport errorReport;
        new ChartWriter(path).Write(copy, song.defaultExportOptions, out errorReport);
        snapshot.chartText = File.ReadAllText(path);

        return snapshot;
    }

    public void LoadSnapshot(DuetSongSnapshot snapshot, bool isResync)
    {
        string path = Path.Combine(WorkingDirectory, "session" + MsceIOHelper.FileExtention);
        File.WriteAllText(path, snapshot.chartText);
        Song song = ChartReader.ReadChart(path);

        if (isResync)
        {
            // Same song, so keep the audio already loaded
            foreach (Song.AudioInstrument audio in EnumX<Song.AudioInstrument>.Values)
                song.SetAudioLocation(audio, m_editor.currentSong.GetAudioLocation(audio));
        }
        else
        {
            // The audio comes separately from the host, straight after the chart. See UseSongFiles.
            m_audioFileNames = new Dictionary<Song.AudioInstrument, string>(snapshot.audioFileNames);
            m_songFiles = new Dictionary<string, string>();
            missingAudioFiles.Clear();
            foreach (string name in m_audioFileNames.Values)
            {
                if (!missingAudioFiles.Contains(name))
                    missingAudioFiles.Add(name);
            }
        }

        m_editor.LoadCollaborationSong(song, !isResync, isResync);
    }
}
