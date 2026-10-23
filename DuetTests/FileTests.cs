// See LICENSE in project root for license information.

// Checks that the host's song files (audio, art, song.ini) reach a joiner byte for byte, and that joining again
// only sends the files that changed.

using System;
using System.Collections.Generic;
using System.IO;
using MoonscraperChartEditor.Song;

public static class FileTests
{
    static int s_failures = 0;

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "duet-files-" + Guid.NewGuid().ToString("N"));
        string hostFolder = Path.Combine(root, "host", "My Song");
        string elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(hostFolder);
        Directory.CreateDirectory(elsewhere);
        UnityEngine.Application.persistentDataPath = Path.Combine(root, "joiner-data");

        Random random = new Random(1);
        string song = Write(hostFolder, "song.ogg", 300 * 1024 + 17, random);
        Write(hostFolder, "guitar.ogg", 100 * 1024, random);
        Write(hostFolder, "album.png", 10 * 1024, random);
        File.WriteAllText(Path.Combine(hostFolder, "song.ini"), "[song]\nname = My Song\n");
        Write(hostFolder, "silence.wav", 0, random);
        File.WriteAllText(Path.Combine(hostFolder, "notes.chart"), "not shared");
        File.WriteAllText(Path.Combine(hostFolder, "readme.txt"), "not shared");
        string drums = Write(elsewhere, "drums.ogg", 50 * 1024, random);

        List<string> found = DuetFiles.FindSongFiles(hostFolder, new[] { song, drums });
        List<string> names = found.ConvertAll(Path.GetFileName);
        Expect("the song's files are found, wherever its audio is", names.Count == 6 && names.Contains("drums.ogg") && names.Contains("song.ini")
            && names.Contains("album.png") && names.Contains("silence.wav") && !names.Contains("notes.chart") && !names.Contains("readme.txt"));

        SessionTests.TestSong hostSong = new SessionTests.TestSong("files-host");
        hostSong.sharedFiles = found;

        DuetSession host = DuetSession.Host(hostSong, "Robert", 0);
        SessionTests.TestSong joinerSong = Join(host, "files-joiner-1");
        Expect("every file arrives byte for byte", joinerSong.receivedFiles != null && SameFiles(found, joinerSong.receivedFiles));

        // Joining again the next day: nothing to download
        DuetSession second;
        SessionTests.TestSong again = Join(host, "files-joiner-2", out second);
        Expect("joining again reuses the files already here", again.receivedFiles != null && second.songFiles.totalBytes == 0 && SameFiles(found, again.receivedFiles));
        second.Leave();

        // The host swapped the guitar track since then
        Write(hostFolder, "guitar.ogg", 120 * 1024, random);
        DuetSession third;
        SessionTests.TestSong changed = Join(host, "files-joiner-3", out third);
        Expect("only a file that changed is sent again", changed.receivedFiles != null && third.songFiles.totalBytes == 120 * 1024 && SameFiles(found, changed.receivedFiles));

        host.Leave();

        DuetFileEntry sneaky = DuetFiles.FromJson(new Dictionary<string, object> { { "name", "..\\..\\evil.ogg" }, { "size", 1.0 }, { "sha1", new string('a', 40) } });
        Expect("a file name can't point outside the download folder", sneaky != null && sneaky.name == "evil.ogg");
        Expect("a bad hash is refused", DuetFiles.FromJson(new Dictionary<string, object> { { "name", "a.ogg" }, { "size", 1.0 }, { "sha1", "../x" } }) == null);

        try { Directory.Delete(root, true); } catch (Exception) { }
        return s_failures;
    }

    static SessionTests.TestSong Join(DuetSession host, string name)
    {
        DuetSession joiner;
        SessionTests.TestSong song = Join(host, name, out joiner);
        joiner.Leave();
        return song;
    }

    static SessionTests.TestSong Join(DuetSession host, string name, out DuetSession joiner)
    {
        SessionTests.TestSong song = new SessionTests.TestSong(name);
        DuetSession session = DuetSession.Join(song, name, "127.0.0.1", host.port);
        joiner = session;
        SessionTests.Pump(new List<DuetSession> { host, session }, () => song.receivedFiles != null);
        return song;
    }

    static string Write(string folder, string name, int size, Random random)
    {
        byte[] data = new byte[size];
        random.NextBytes(data);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    static bool SameFiles(List<string> sent, Dictionary<string, string> received)
    {
        if (received.Count != sent.Count)
            return false;

        foreach (string path in sent)
        {
            string local;
            if (!received.TryGetValue(Path.GetFileName(path), out local) || !File.Exists(local))
                return false;

            byte[] a = File.ReadAllBytes(path), b = File.ReadAllBytes(local);
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != b[i])
                    return false;
            }
        }
        return true;
    }

    static void Expect(string label, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + label);
        if (!condition)
            ++s_failures;
    }
}
