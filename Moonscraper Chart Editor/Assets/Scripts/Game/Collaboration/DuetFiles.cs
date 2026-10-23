// See LICENSE in project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>
/// One of the song's files, as the host describes it to someone joining
/// </summary>
public class DuetFileEntry
{
    public string name;     // File name only, no folder
    public long size;
    public string sha1;     // Lowercase hex, so a joiner can reuse a copy it already has
}

/// <summary>
/// The song's files (audio, album art, backgrounds, video, song.ini) that the host sends to each joiner, so the
/// joiner's copy of the song matches the host's. Received files are kept by content, so joining again later only
/// sends files that changed.
/// </summary>
public static class DuetFiles
{
    public const int ChunkBytes = 48 * 1024;
    public const long MaxQueuedBytes = 256 * 1024;  // Keeps edits from waiting behind a long file transfer

    static readonly HashSet<string> s_songFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".mp3", ".wav", ".opus", ".png", ".jpg", ".jpeg", ".mp4", ".webm", ".avi", ".mkv", ".mov",
    };

    class HashedFile { public long size; public DateTime modified; public string sha1; }
    static readonly Dictionary<string, HashedFile> s_hashes = new Dictionary<string, HashedFile>();

    public static string CacheFolder
    {
        get { return Path.Combine(Application.persistentDataPath, "DuetFiles"); }
    }

    public static string CachedPath(DuetFileEntry entry)
    {
        return Path.Combine(Path.Combine(CacheFolder, entry.sha1), entry.name);
    }

    public static bool IsCached(DuetFileEntry entry)
    {
        FileInfo file = new FileInfo(CachedPath(entry));
        return file.Exists && file.Length == entry.size;
    }

    /// <summary>
    /// The files that make up a song: the audio it uses wherever it is, and from the chart's folder every audio file,
    /// image, video and song.ini. Charts themselves aren't included, since the chart is sent separately.
    /// When two files share a name, the first one wins.
    /// </summary>
    public static List<string> FindSongFiles(string chartFolder, IEnumerable<string> audioPaths)
    {
        List<string> found = new List<string>();
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in audioPaths)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && names.Add(Path.GetFileName(path)))
                found.Add(Path.GetFullPath(path));
        }

        if (!string.IsNullOrEmpty(chartFolder) && Directory.Exists(chartFolder))
        {
            string[] files = Directory.GetFiles(chartFolder);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                bool wanted = s_songFileExtensions.Contains(Path.GetExtension(path)) || string.Equals(name, "song.ini", StringComparison.OrdinalIgnoreCase);
                if (wanted && names.Add(name))
                    found.Add(Path.GetFullPath(path));
            }
        }

        return found;
    }

    public static List<DuetFileEntry> Describe(List<string> paths, Dictionary<string, string> nameToPath)
    {
        List<DuetFileEntry> entries = new List<DuetFileEntry>();
        foreach (string path in paths)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                entries.Add(new DuetFileEntry { name = info.Name, size = info.Length, sha1 = Sha1(path) });
                nameToPath[info.Name] = path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Duet: couldn't read " + path + " to share it: " + e.Message);
            }
        }
        return entries;
    }

    // Remembers each file's hash until it changes, so hosting a big song doesn't re-read it for every joiner
    public static string Sha1(string path)
    {
        FileInfo info = new FileInfo(path);
        HashedFile hashed;
        if (s_hashes.TryGetValue(info.FullName, out hashed) && hashed.size == info.Length && hashed.modified == info.LastWriteTimeUtc)
            return hashed.sha1;

        string sha1;
        using (FileStream stream = File.OpenRead(path))
        using (SHA1 hasher = SHA1.Create())
            sha1 = BitConverter.ToString(hasher.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();

        s_hashes[info.FullName] = new HashedFile { size = info.Length, modified = info.LastWriteTimeUtc, sha1 = sha1 };
        return sha1;
    }

    public static Dictionary<string, object> ToJson(DuetFileEntry entry)
    {
        return new Dictionary<string, object> { { "name", entry.name }, { "size", (double)entry.size }, { "sha1", entry.sha1 } };
    }

    public static DuetFileEntry FromJson(Dictionary<string, object> json)
    {
        string name = Path.GetFileName(DuetJson.GetString(json, "name", string.Empty).Replace('\\', '/'));    // Never a path into another folder
        string sha1 = DuetJson.GetString(json, "sha1", string.Empty).ToLowerInvariant();
        if (name.Length == 0 || name == "." || name == ".." || sha1.Length != 40 || !IsHex(sha1))
            return null;
        return new DuetFileEntry { name = name, size = (long)DuetJson.GetNumber(json, "size"), sha1 = sha1 };
    }

    static bool IsHex(string text)
    {
        foreach (char c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Sends the files one joiner asked for, a chunk at a time, only while the connection isn't backed up.
/// </summary>
public class DuetFileSender
{
    readonly Queue<KeyValuePair<DuetFileEntry, string>> m_waiting = new Queue<KeyValuePair<DuetFileEntry, string>>();
    DuetFileEntry m_entry;
    FileStream m_stream;
    readonly byte[] m_buffer = new byte[DuetFiles.ChunkBytes];

    public bool isDone { get { return m_stream == null && m_waiting.Count == 0; } }

    public void Add(DuetFileEntry entry, string path)
    {
        m_waiting.Enqueue(new KeyValuePair<DuetFileEntry, string>(entry, path));
    }

    public void Pump(IDuetLink link)
    {
        while (!link.isClosed && link.queuedBytes < DuetFiles.MaxQueuedBytes)
        {
            if (m_stream == null)
            {
                if (m_waiting.Count == 0)
                    return;

                KeyValuePair<DuetFileEntry, string> next = m_waiting.Dequeue();
                m_entry = next.Key;
                try
                {
                    m_stream = File.OpenRead(next.Value);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("Duet: couldn't open " + next.Value + " to send it: " + e.Message);
                    link.Send(Message("filefailed", m_entry.name));
                    continue;
                }
            }

            long offset = m_stream.Position;
            int read = m_stream.Read(m_buffer, 0, m_buffer.Length);
            if (read > 0)
            {
                Dictionary<string, object> chunk = Message("file", m_entry.name);
                chunk["offset"] = (double)offset;
                chunk["data"] = Convert.ToBase64String(m_buffer, 0, read);
                link.Send(chunk);
            }

            if (read <= 0 || m_stream.Position >= m_entry.size)
            {
                m_stream.Dispose();
                m_stream = null;
            }
        }
    }

    public void Close()
    {
        if (m_stream != null)
            m_stream.Dispose();
        m_stream = null;
        m_waiting.Clear();
    }

    static Dictionary<string, object> Message(string type, string name)
    {
        return new Dictionary<string, object> { { "type", type }, { "name", name } };
    }
}

/// <summary>
/// Collects the host's song files on a joiner, reusing copies already received in an earlier session.
/// </summary>
public class DuetFileReceiver
{
    readonly Dictionary<string, DuetFileEntry> m_entries = new Dictionary<string, DuetFileEntry>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FileStream> m_partial = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> m_pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Every file the host shared, by name, and where it is on this computer once it's here
    public readonly Dictionary<string, string> localPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> failed = new List<string>();

    public long totalBytes { get; private set; }     // Of the files being sent, not the reused ones
    public long receivedBytes { get; private set; }
    public bool isComplete { get { return m_pending.Count == 0; } }

    // Returns the names of the files that need sending
    public List<string> Start(List<DuetFileEntry> entries)
    {
        List<string> wanted = new List<string>();
        foreach (DuetFileEntry entry in entries)
        {
            if (m_entries.ContainsKey(entry.name))
                continue;
            m_entries[entry.name] = entry;

            if (DuetFiles.IsCached(entry))
            {
                localPaths[entry.name] = DuetFiles.CachedPath(entry);
            }
            else if (entry.size <= 0)
            {
                string path = DuetFiles.CachedPath(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, new byte[0]);
                localPaths[entry.name] = path;
            }
            else
            {
                wanted.Add(entry.name);
                m_pending.Add(entry.name);
                totalBytes += entry.size;
            }
        }
        return wanted;
    }

    public void Append(string name, long offset, byte[] data)
    {
        DuetFileEntry entry;
        if (!m_pending.Contains(name) || !m_entries.TryGetValue(name, out entry))
            return;

        FileStream stream;
        if (!m_partial.TryGetValue(name, out stream))
        {
            string path = DuetFiles.CachedPath(entry) + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            stream = File.Create(path);
            m_partial[name] = stream;
        }

        if (offset != stream.Position || offset + data.Length > entry.size)
        {
            Fail(name, "arrived out of order");
            return;
        }

        stream.Write(data, 0, data.Length);
        receivedBytes += data.Length;

        if (stream.Position >= entry.size)
            Finish(entry, stream);
    }

    public void Fail(string name, string reason)
    {
        if (!m_pending.Remove(name))
            return;

        FileStream stream;
        if (m_partial.TryGetValue(name, out stream))
        {
            stream.Dispose();
            m_partial.Remove(name);
            TryDelete(stream.Name);
        }

        failed.Add(name);
        Debug.LogWarning("Duet: " + name + " " + reason);
    }

    void Finish(DuetFileEntry entry, FileStream stream)
    {
        string partPath = stream.Name;
        stream.Dispose();
        m_partial.Remove(entry.name);

        if (DuetFiles.Sha1(partPath) != entry.sha1)
        {
            TryDelete(partPath);
            Fail(entry.name, "didn't arrive intact");
            return;
        }

        string path = DuetFiles.CachedPath(entry);
        TryDelete(path);
        File.Move(partPath, path);
        localPaths[entry.name] = path;
        m_pending.Remove(entry.name);
    }

    public void Close()
    {
        foreach (FileStream stream in m_partial.Values)
        {
            stream.Dispose();
            TryDelete(stream.Name);
        }
        m_partial.Clear();
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception) { }
    }
}
