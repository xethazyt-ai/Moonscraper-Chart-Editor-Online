// See LICENSE in project root for license information.

// Runs real Duet Mode sessions over TCP on this computer: one host and two joiners, each with its own copy of a song.
// Checks that joining copies the chart, edits flow both ways, clashing edits end with everyone on the host's version,
// and mismatched versions are turned away.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MoonscraperChartEditor.Song;
using MoonscraperChartEditor.Song.IO;

public static class SessionTests
{
    // A song in memory standing in for the editor
    public class TestSong : IDuetSongHost
    {
        public Song song = new Song();
        public string name;
        public int resyncs = 0;

        public TestSong(string name) { this.name = name; }

        public bool isBusy { get { return false; } }

        public bool TryApplyRemoteEdit(IList<SongEditChange> changes, out string failure)
        {
            return SongEditChangeApplier.TryApplyStrict(new SongEditChangeApplier.SongDataTarget(song), changes, out failure);
        }

        public DuetSongSnapshot TakeSnapshot()
        {
            DuetSongSnapshot snapshot = new DuetSongSnapshot();
            string path = Path.Combine(Path.GetTempPath(), "duet-" + name + "-snapshot" + MsceIOHelper.FileExtention);
            ChartWriter.ErrorReport errorReport;
            new ChartWriter(path).Write(song, song.defaultExportOptions, out errorReport);
            snapshot.chartText = File.ReadAllText(path);
            snapshot.audioFileNames[Song.AudioInstrument.Song] = "song.ogg";
            return snapshot;
        }

        public void LoadSnapshot(DuetSongSnapshot snapshot, bool isResync)
        {
            string path = Path.Combine(Path.GetTempPath(), "duet-" + name + "-received" + MsceIOHelper.FileExtention);
            File.WriteAllText(path, snapshot.chartText);
            song = ChartReader.ReadChart(path);
            if (isResync)
                ++resyncs;
        }

        public List<string> sharedFiles = new List<string>();
        public Dictionary<string, string> receivedFiles;
        public List<string> GetSongFilePaths() { return sharedFiles; }
        public void UseSongFiles(Dictionary<string, string> localPaths) { receivedFiles = new Dictionary<string, string>(localPaths); }

        public Dictionary<string, string> ReadSongInfo() { return DuetSongInfo.Read(song); }
        public void ApplySongInfo(Dictionary<string, string> info) { DuetSongInfo.Apply(song, info); }

        // Makes an edit locally, the way the editor does before reporting it
        public List<SongEditChange> Edit(params SongEditChange[] changes)
        {
            string failure;
            if (!SongEditChangeApplier.TryApplyStrict(new SongEditChangeApplier.SongDataTarget(song), changes, out failure))
                throw new Exception(name + " couldn't make its own edit: " + failure);
            return new List<SongEditChange>(changes);
        }
    }

    static int s_failures = 0;
    static readonly ChartLocation GuitarExpert = new ChartLocation { isSongLevel = false, instrument = Song.Instrument.Guitar, difficulty = Song.Difficulty.Expert };

    public static int Run()
    {
        TestSong hostSong = new TestSong("host");
        TestSong aliceSong = new TestSong("alice");
        TestSong bobSong = new TestSong("bob");

        hostSong.Edit(Add(new Note(0, 0)), Add(new Note(192, 1, 96)), Add(new BPM(768, 140000)));

        DuetSession host = DuetSession.Host(hostSong, "Robert", 0);  // Port 0 picks any free port
        DuetSession alice = DuetSession.Join(aliceSong, "Alex", "127.0.0.1", host.port);
        List<DuetSession> all = new List<DuetSession> { host, alice };

        Pump(all, () => alice.state == DuetSession.State.Connected && host.users.Count == 2);
        Expect("joiner receives the host's chart", Same(hostSong, aliceSong));
        Expect("host lists the joiner by name", host.users.Exists(u => u.name == "Alex"));
        Expect("joiner lists both people", alice.users.Count == 2);

        host.OnLocalEdit(hostSong.Edit(Add(new Note(384, 2))));
        Pump(all, () => Same(hostSong, aliceSong));
        Expect("host's edit reaches the joiner", Same(hostSong, aliceSong));

        alice.OnLocalEdit(aliceSong.Edit(Add(new Section("Verse 1", 0)), Modify(new Note(192, 1, 96), new Note(192, 1, 192))));
        Pump(all, () => Same(hostSong, aliceSong) && alice.unacknowledgedEditCount == 0);
        Expect("joiner's edit reaches the host", Same(hostSong, aliceSong));
        Expect("host acknowledges the joiner's edit", alice.unacknowledgedEditCount == 0 && alice.lastSeq == host.lastSeq);

        DuetSession bob = DuetSession.Join(bobSong, "Alex", "127.0.0.1", host.port);
        all.Add(bob);
        Pump(all, () => bob.state == DuetSession.State.Connected && host.users.Count == 3);
        Expect("a second joiner gets the current chart", Same(hostSong, bobSong));
        Expect("a repeated name gets a number", host.users.Exists(u => u.name == "Alex 2"));

        bob.OnLocalEdit(bobSong.Edit(Add(new Starpower(0, 384))));
        Pump(all, () => Same(hostSong, aliceSong) && Same(hostSong, bobSong));
        Expect("a joiner's edit reaches the other joiner", Same(aliceSong, bobSong) && Same(hostSong, bobSong));

        // Host and a joiner change the same note at the same moment. The host's version should win everywhere.
        host.OnLocalEdit(hostSong.Edit(Modify(new Note(384, 2), new Note(384, 2, 48))));
        alice.OnLocalEdit(aliceSong.Edit(Modify(new Note(384, 2), new Note(384, 2, 300))));
        Pump(all, () => Same(hostSong, aliceSong) && Same(hostSong, bobSong) && aliceSong.resyncs > 0);
        Expect("clashing edits: everyone ends on the same chart", Same(hostSong, aliceSong) && Same(hostSong, bobSong));
        Expect("clashing edits: the host's version wins", NoteLength(hostSong, 384, 2) == 48);
        Expect("clashing edits: the joiner was refreshed", aliceSong.resyncs > 0);

        // Edits keep flowing after a refresh
        alice.OnLocalEdit(aliceSong.Edit(Delete(new Note(0, 0))));
        Pump(all, () => Same(hostSong, aliceSong) && Same(hostSong, bobSong));
        Expect("edits keep working after a refresh", Same(hostSong, aliceSong) && Same(hostSong, bobSong) && NoteLength(hostSong, 0, 0) < 0);

        // Someone running a different version is turned away with a reason
        DuetConnection stranger = DuetConnection.Connect("127.0.0.1", host.port, 2000);
        stranger.Send(new Dictionary<string, object> { { "type", "hello" }, { "protocol", 99.0 }, { "name", "Old build" } });
        Dictionary<string, object> reply = null;
        Pump(all, () => stranger.TryReceive(out reply));
        Expect("a different version is rejected with a reason", reply != null && DuetJson.GetString(reply, "type") == "rejected" && DuetJson.GetString(reply, "reason", "").Contains("version"));
        stranger.Close();

        bob.Leave();
        Pump(all, () => host.users.Count == 2 && alice.users.Count == 2);
        Expect("leaving updates everyone's list", host.users.Count == 2 && alice.users.Count == 2);

        host.Leave();
        Pump(all, () => !alice.isActive);
        Expect("joiners are told when the host ends the session", !alice.isActive);

        RunWithEditor();

        return s_failures;
    }

    // The host is the editor itself: its edits go through the real command stack, and joiners' edits are applied
    // through the same actions the editor uses
    static void RunWithEditor()
    {
        Song editorSong = new Song();
        ChartEditor editor = new ChartEditor { currentSong = editorSong, currentChart = editorSong.GetChart(Song.Instrument.Guitar, Song.Difficulty.Expert) };
        ChartEditor.Instance = editor;
        SongEditCommandStack stack = new SongEditCommandStack();
        editor.songEditGestureTracker = new SongEditGestureTracker(stack);

        DuetEditorSong hostSong = new DuetEditorSong(editor, editor.songEditGestureTracker);
        DuetSession host = DuetSession.Host(hostSong, "Robert", 0);
        editor.songEditGestureTracker.onEditFinished.Register((in SongEditGestureTracker.FinishedEdit edit) => host.OnLocalEdit(edit.changes));

        TestSong joinerSong = new TestSong("editor-joiner");
        DuetSession joiner = DuetSession.Join(joinerSong, "Alex", "127.0.0.1", host.port);
        List<DuetSession> all = new List<DuetSession> { host, joiner };

        UnityEngine.Input.anyKey = false;
        stack.Push(new SongEditAdd(new Note(0, 0)));
        stack.Push(new SongEditAdd(new Note(0, 1)));
        editor.songEditGestureTracker.SystemUpdate();

        Pump(all, () => joiner.state == DuetSession.State.Connected);
        Expect("editor host: joiner gets the chart", SameSong(editor.currentSong, joinerSong));

        joiner.OnLocalEdit(joinerSong.Edit(Add(new Note(192, 2, 96)), Add(new BPM(384, 150000))));
        Pump(all, () => SameSong(editor.currentSong, joinerSong) && joiner.unacknowledgedEditCount == 0);
        Expect("editor host: joiner's edit lands in the editor", SameSong(editor.currentSong, joinerSong));

        // The joiner's edit must not be reported as the host's own next edit
        stack.Push(new SongEditDelete(new Note(0, 1)));
        editor.songEditGestureTracker.SystemUpdate();
        Pump(all, () => SameSong(editor.currentSong, joinerSong));
        Expect("editor host: its next edit reaches the joiner without a clash", SameSong(editor.currentSong, joinerSong) && joinerSong.resyncs == 0);

        // Undo on the host reaches the joiner too
        stack.Pop();
        Pump(all, () => SameSong(editor.currentSong, joinerSong));
        Expect("editor host: undo reaches the joiner", SameSong(editor.currentSong, joinerSong) && NoteLength(joinerSong, 0, 1) == 0);

        host.Leave();
        Pump(all, () => !joiner.isActive);
    }

    static bool SameSong(Song song, TestSong testSong)
    {
        return ChartText(song, "editor") == ChartText(testSong.song, testSong.name);
    }

    public static SongEditChange Add(SongObject songObject)
    {
        return new SongEditChange { type = SongEditChange.Type.Add, location = LocationOf(songObject), after = songObject };
    }

    static SongEditChange Delete(SongObject songObject)
    {
        return new SongEditChange { type = SongEditChange.Type.Delete, location = LocationOf(songObject), before = songObject };
    }

    static SongEditChange Modify(SongObject before, SongObject after)
    {
        return new SongEditChange { type = SongEditChange.Type.Modify, location = LocationOf(before), before = before, after = after };
    }

    static ChartLocation LocationOf(SongObject songObject)
    {
        return songObject is ChartObject ? GuitarExpert : ChartLocation.SongLevel;
    }

    static long NoteLength(TestSong testSong, uint tick, int fret)
    {
        foreach (Note note in testSong.song.GetChart(Song.Instrument.Guitar, Song.Difficulty.Expert).notes)
        {
            if (note.tick == tick && note.rawNote == fret)
                return note.length;
        }
        return -1;
    }

    // Updates every session until the condition holds, or gives up after a few seconds
    public static void Pump(List<DuetSession> sessions, Func<bool> condition)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 5000)
        {
            foreach (DuetSession session in sessions)
                session.Update();

            if (condition())
                return;

            System.Threading.Thread.Sleep(5);
        }
    }

    public static bool Same(TestSong a, TestSong b)
    {
        return ChartText(a.song, a.name) == ChartText(b.song, b.name);
    }

    // The song's chart data as saved, without the [Song] properties section
    static string ChartText(Song song, string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "duet-session-" + name + MsceIOHelper.FileExtention);
        ChartWriter.ErrorReport errorReport;
        new ChartWriter(path).Write(song, song.defaultExportOptions, out errorReport);

        List<string> lines = new List<string>(File.ReadAllLines(path));
        int start = lines.IndexOf("[Song]");
        int end = lines.IndexOf("}", start);
        lines.RemoveRange(start, end - start + 1);

        return string.Join("\n", lines);
    }

    static void Expect(string label, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + label);
        if (!condition)
            ++s_failures;
    }
}
