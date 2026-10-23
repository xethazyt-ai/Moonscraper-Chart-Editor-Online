// See LICENSE in project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

/// <summary>
/// One connection to another person in a session, over any transport (direct TCP or an online room).
/// All members are called from the main thread.
/// </summary>
public interface IDuetLink
{
    int id { get; set; }
    string remoteAddress { get; }
    bool isClosed { get; }
    string closeReason { get; }

    void Send(Dictionary<string, object> message);

    // Bytes sent but not yet on their way, so big transfers can wait instead of piling up ahead of edits
    long queuedBytes { get; }
    bool TryReceive(out Dictionary<string, object> message);
    void Close(string reason = "Disconnected");

    // Lets queued messages go out before closing, such as a final "rejected" message
    void CloseAfterSending(string reason);
}

/// <summary>
/// Where a host's incoming connections come from.
/// </summary>
public interface IDuetListener
{
    // A room code to share, once there is one. Null for direct connections.
    string roomCode { get; }

    // Set if the listener can no longer take connections
    string error { get; }

    bool TryAccept(out IDuetLink link);
    void Stop();
}

/// <summary>
/// A joiner's connection attempt, checked every frame until it finishes.
/// </summary>
public interface IDuetConnector
{
    // True once finished: either link is set, or error says why it failed
    bool TryFinish(out IDuetLink link, out string error);
    void Cancel();
}

/// <summary>
/// Online rooms joined by code. Set by the transport that provides them, when it's built in.
/// </summary>
public interface IDuetRoomProvider
{
    string status { get; }
    IDuetListener CreateRoom();
    IDuetConnector JoinRoom(string roomCode);
}

/// <summary>
/// A Windows pop-up (the save prompt, a file window) freezes Moonscraper until it's closed. Online rooms need ticking
/// to stay connected, so while one is open this ticks them from a background thread instead, and nobody is dropped.
/// </summary>
public static class DuetKeepAlive
{
    const int TickIntervalMs = 30;

    public static Action tick;      // Set by the online rooms provider. Always called under tickLock.
    public static readonly object tickLock = new object();

    // Wrap a call that blocks the main thread: using (DuetKeepAlive.WhileBlocked()) { ... }
    public static IDisposable WhileBlocked()
    {
        return new Scope();
    }

    class Scope : IDisposable
    {
        volatile bool m_done = false;

        public Scope()
        {
            if (tick != null)
                new Thread(Loop) { IsBackground = true, Name = "Duet keep alive" }.Start();
        }

        void Loop()
        {
            while (!m_done)
            {
                Thread.Sleep(TickIntervalMs);
                lock (tickLock)
                {
                    if (m_done)
                        return;

                    Action current = tick;
                    try
                    {
                        if (current != null)
                            current();
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogWarning("Duet: keeping the connection alive failed: " + e.Message);
                    }
                }
            }
        }

        public void Dispose()
        {
            m_done = true;
            lock (tickLock) { }     // Wait for a tick in progress, so the main thread has it to itself again
        }
    }
}

public static class DuetRooms
{
    public static IDuetRoomProvider provider;

    public static bool isAvailable { get { return provider != null; } }

    public const int MaxMembers = 64;   // The most Epic allows in a lobby, counting the host

    // Room codes are typed by hand, so accept lowercase, spaces and dashes
    public static string NormalizeCode(string code)
    {
        StringBuilder normalized = new StringBuilder();
        foreach (char c in code ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
                normalized.Append(char.ToUpperInvariant(c));
        }
        return normalized.ToString();
    }
}

/// <summary>
/// Turns messages into bytes and back: a 4-byte big-endian length followed by UTF-8 JSON.
/// </summary>
public static class DuetFraming
{
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    public static byte[] Encode(Dictionary<string, object> message)
    {
        byte[] body = Encoding.UTF8.GetBytes(DuetJson.Write(message));
        byte[] frame = new byte[4 + body.Length];
        frame[0] = (byte)(body.Length >> 24);
        frame[1] = (byte)(body.Length >> 16);
        frame[2] = (byte)(body.Length >> 8);
        frame[3] = (byte)body.Length;
        Buffer.BlockCopy(body, 0, frame, 4, body.Length);
        return frame;
    }

    public static Dictionary<string, object> Decode(byte[] body, int offset, int count)
    {
        return DuetJson.Read(Encoding.UTF8.GetString(body, offset, count)) as Dictionary<string, object>;
    }

    /// <summary>
    /// Rebuilds messages from a stream of bytes that arrives in pieces of any size, in order.
    /// </summary>
    public class Assembler
    {
        byte[] m_buffer = new byte[4096];
        int m_count = 0;

        public void Append(byte[] data, int offset, int count, List<Dictionary<string, object>> messagesOut)
        {
            if (m_count + count > m_buffer.Length)
            {
                byte[] bigger = new byte[Math.Max(m_buffer.Length * 2, m_count + count)];
                Buffer.BlockCopy(m_buffer, 0, bigger, 0, m_count);
                m_buffer = bigger;
            }
            Buffer.BlockCopy(data, offset, m_buffer, m_count, count);
            m_count += count;

            int start = 0;
            while (m_count - start >= 4)
            {
                int length = (m_buffer[start] << 24) | (m_buffer[start + 1] << 16) | (m_buffer[start + 2] << 8) | m_buffer[start + 3];
                if (length < 0 || length > MaxMessageBytes)
                    throw new InvalidDataException("Message too large: " + length + " bytes");

                if (m_count - start - 4 < length)
                    break;

                Dictionary<string, object> message = Decode(m_buffer, start + 4, length);
                if (message != null)
                    messagesOut.Add(message);
                start += 4 + length;
            }

            if (start > 0)
            {
                Buffer.BlockCopy(m_buffer, start, m_buffer, 0, m_count - start);
                m_count -= start;
            }
        }
    }
}

/// <summary>
/// Connects to a host by address on a background thread.
/// </summary>
public class DuetTcpConnector : IDuetConnector
{
    const int TimeoutMs = 8000;

    volatile DuetConnection m_result;
    volatile string m_error;
    volatile bool m_cancelled = false;

    public DuetTcpConnector(string address, int port)
    {
        new Thread(() =>
        {
            try
            {
                DuetConnection connection = DuetConnection.Connect(address, port, TimeoutMs);
                if (m_cancelled)
                    connection.Close("Cancelled");
                else
                    m_result = connection;
            }
            catch (Exception e)
            {
                m_error = e.Message;
            }
        }) { IsBackground = true, Name = "Duet connect" }.Start();
    }

    public bool TryFinish(out IDuetLink link, out string error)
    {
        link = m_result;
        error = m_error;
        return link != null || error != null;
    }

    public void Cancel()
    {
        m_cancelled = true;
        if (m_result != null)
            m_result.Close("Cancelled");
    }
}
