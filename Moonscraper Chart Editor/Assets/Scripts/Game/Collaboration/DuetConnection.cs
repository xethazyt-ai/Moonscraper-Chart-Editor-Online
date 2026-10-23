// See LICENSE in project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// One TCP connection carrying Duet Mode messages. Each message is a 4-byte big-endian length followed by UTF-8 JSON.
/// Reading and writing happen on background threads; call Receive from the main thread to collect incoming messages.
/// </summary>
public class DuetConnection : IDuetLink
{
    readonly TcpClient m_client;
    readonly NetworkStream m_stream;
    readonly ConcurrentQueue<Dictionary<string, object>> m_incoming = new ConcurrentQueue<Dictionary<string, object>>();
    readonly BlockingCollection<byte[]> m_outgoing = new BlockingCollection<byte[]>();
    volatile bool m_closed = false;
    long m_queuedBytes = 0;

    public int id { get; set; }
    public string remoteAddress { get; private set; }
    public bool isClosed { get { return m_closed; } }
    public string closeReason { get; private set; }
    public long queuedBytes { get { return Interlocked.Read(ref m_queuedBytes); } }

    public DuetConnection(TcpClient client)
    {
        m_client = client;
        m_client.NoDelay = true;
        m_stream = client.GetStream();
        remoteAddress = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "unknown";

        new Thread(ReadLoop) { IsBackground = true, Name = "Duet read" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "Duet write" }.Start();
    }

    public static DuetConnection Connect(string host, int port, int timeoutMs)
    {
        TcpClient client = new TcpClient();
        IAsyncResult result = client.BeginConnect(host, port, null, null);
        if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
        {
            client.Close();
            throw new TimeoutException("No answer from " + host + ":" + port);
        }
        client.EndConnect(result);
        return new DuetConnection(client);
    }

    public void Send(Dictionary<string, object> message)
    {
        if (m_closed)
            return;

        byte[] frame = DuetFraming.Encode(message);

        try
        {
            Interlocked.Add(ref m_queuedBytes, frame.Length);
            m_outgoing.Add(frame);
        }
        catch (InvalidOperationException)
        {
            // Closed while sending
        }
    }

    public bool TryReceive(out Dictionary<string, object> message)
    {
        return m_incoming.TryDequeue(out message);
    }

    public void Close(string reason = "Disconnected")
    {
        if (m_closed)
            return;

        closeReason = reason;
        m_closed = true;
        m_outgoing.CompleteAdding();

        try { m_client.Close(); } catch (Exception) { }
    }

    // Lets queued messages go out before closing, such as a final "rejected" message
    public void CloseAfterSending(string reason)
    {
        closeReason = reason;
        m_outgoing.CompleteAdding();
    }

    void ReadLoop()
    {
        try
        {
            byte[] header = new byte[4];
            while (!m_closed)
            {
                if (!ReadExactly(header, 4))
                    break;

                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length < 0 || length > DuetFraming.MaxMessageBytes)
                    throw new InvalidDataException("Message too large: " + length + " bytes");

                byte[] body = new byte[length];
                if (!ReadExactly(body, length))
                    break;

                Dictionary<string, object> message = DuetFraming.Decode(body, 0, length);
                if (message != null)
                    m_incoming.Enqueue(message);
            }

            Close(closeReason ?? "The other side closed the connection");
        }
        catch (Exception e)
        {
            Close(m_closed ? closeReason : "Connection lost: " + e.Message);
        }
    }

    bool ReadExactly(byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = m_stream.Read(buffer, read, count - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    void WriteLoop()
    {
        try
        {
            foreach (byte[] frame in m_outgoing.GetConsumingEnumerable())
            {
                m_stream.Write(frame, 0, frame.Length);
                Interlocked.Add(ref m_queuedBytes, -frame.Length);
            }

            m_stream.Flush();
        }
        catch (Exception e)
        {
            if (!m_closed)
                closeReason = "Connection lost: " + e.Message;
        }

        Close(closeReason ?? "Disconnected");
    }
}

/// <summary>
/// Accepts incoming Duet Mode connections on a port.
/// </summary>
public class DuetListener : IDuetListener
{
    readonly TcpListener m_listener;
    readonly ConcurrentQueue<DuetConnection> m_accepted = new ConcurrentQueue<DuetConnection>();
    volatile bool m_stopped = false;

    public int port { get; private set; }
    public string roomCode { get { return null; } }
    public string error { get { return null; } }

    public DuetListener(int port)
    {
        m_listener = new TcpListener(IPAddress.Any, port);
        m_listener.Start();
        this.port = ((IPEndPoint)m_listener.LocalEndpoint).Port;

        new Thread(AcceptLoop) { IsBackground = true, Name = "Duet accept" }.Start();
    }

    public bool TryAccept(out IDuetLink link)
    {
        DuetConnection connection;
        bool accepted = m_accepted.TryDequeue(out connection);
        link = connection;
        return accepted;
    }

    public void Stop()
    {
        m_stopped = true;
        try { m_listener.Stop(); } catch (Exception) { }
    }

    void AcceptLoop()
    {
        while (!m_stopped)
        {
            try
            {
                TcpClient client = m_listener.AcceptTcpClient();
                m_accepted.Enqueue(new DuetConnection(client));
            }
            catch (Exception)
            {
                if (m_stopped)
                    return;

                Thread.Sleep(100);
            }
        }
    }
}
