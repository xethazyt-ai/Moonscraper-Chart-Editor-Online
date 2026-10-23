// See LICENSE in project root for license information.

// Online rooms for Duet Mode over Epic Online Services. The host makes a lobby whose ID is a short room code and
// joiners find it by that code. Messages then go straight between the two PCs over EOS P2P, which falls back to
// Epic's relay servers when a direct connection isn't possible. Everyone signs in anonymously with a device ID.
//
// Only compiled when DUET_EOS is defined (Assets/csc.rsp), because the EOS SDK isn't part of the repository.
// Setup is in DuetTests/EOS-SETUP.md.

#if DUET_EOS

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;

public class DuetEos : MonoBehaviour, IDuetRoomProvider
{
    const string LibraryName = "EOSSDK-Win64-Shipping.dll";
    const string SocketName = "DuetMode";
    const string LobbyIdPrefix = "duet";
    const string BucketId = "duet";
    const int RoomCodeLength = 6;
    const string RoomCodeLetters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";    // No 0/O or 1/I/L, so codes are easy to read out
    const int MaxPacketsPerFrame = 4096;
    const int ShutdownGraceMs = 400;

    enum Phase { Idle, SigningIn, Ready, Failed }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Register()
    {
        GameObject go = new GameObject("Duet EOS");
        DontDestroyOnLoad(go);
        DuetRooms.provider = go.AddComponent<DuetEos>();
    }

    // The EOS folder sits beside Assets in the editor, and beside the .exe in a build
    static string EosFolder { get { return Path.GetFullPath(Path.Combine(Application.dataPath, Path.Combine("..", "EOS"))); } }

    Phase m_phase = Phase.Idle;
    string m_failure;
    IntPtr m_library = IntPtr.Zero;
    bool m_sdkInitialized = false;
    PlatformInterface m_platform;
    ProductUserId m_localUser;
    SocketId m_socket;
    ulong m_requestNotification = 0;
    ulong m_closedNotification = 0;
    ulong m_memberNotification = 0;

    readonly Dictionary<string, Link> m_links = new Dictionary<string, Link>();
    Room m_room;
    readonly byte[] m_receiveBuffer = new byte[P2PInterface.MAX_PACKET_SIZE];
    readonly List<Dictionary<string, object>> m_receivedMessages = new List<Dictionary<string, object>>();

    public string status
    {
        get
        {
            switch (m_phase)
            {
                case Phase.SigningIn: return "Signing in to Epic Online Services...";
                case Phase.Ready: return "Online rooms ready";
                case Phase.Failed: return "Online rooms unavailable: " + m_failure;
                default: return "Online rooms ready";
            }
        }
    }

    public IDuetListener CreateRoom()
    {
        if (m_room != null)
            m_room.Stop();

        StartIfNeeded();
        m_room = new Room(this);
        return m_room;
    }

    public IDuetConnector JoinRoom(string roomCode)
    {
        StartIfNeeded();
        return new Join(this, DuetRooms.NormalizeCode(roomCode));
    }

    #region Start and stop

    void StartIfNeeded()
    {
        if (m_phase == Phase.Failed)
        {
            // Try again from scratch, for example after fixing credentials.txt
            Shutdown();
            m_phase = Phase.Idle;
        }

        if (m_phase != Phase.Idle)
            return;

        m_phase = Phase.SigningIn;
        string error = StartPlatform();
        if (error != null)
        {
            Fail(error);
            return;
        }

        CreateDeviceIdOptions deviceOptions = new CreateDeviceIdOptions { DeviceModel = SystemInfo.deviceModel };
        m_platform.GetConnectInterface().CreateDeviceId(ref deviceOptions, null, OnDeviceIdCreated);
    }

    string StartPlatform()
    {
        Dictionary<string, string> credentials;
        string error;
        if (!TryReadCredentials(out credentials, out error))
            return error;

        // EOS can only start once each time its DLL is loaded, and the editor never unloads native plugins,
        // so load the DLL by hand and free it on shutdown. Assets/csc.rsp defines EOS_DYNAMIC_BINDINGS so builds do the same.
        string libraryPath = Path.Combine(EosFolder, LibraryName);
        m_library = LoadLibrary(libraryPath);
        if (m_library == IntPtr.Zero)
            return "couldn't load " + libraryPath + " (Windows error " + Marshal.GetLastWin32Error() + ")";
        Bindings.Hook(m_library, GetProcAddress);

        InitializeOptions initializeOptions = new InitializeOptions { ProductName = "Moonscraper Duet", ProductVersion = DuetSession.ProtocolVersion.ToString() };
        Result result = PlatformInterface.Initialize(ref initializeOptions);
        if (result != Result.Success && result != Result.AlreadyConfigured)
            return "the SDK didn't start (" + result + ")";
        m_sdkInitialized = true;

        LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.Warning);
        LoggingInterface.SetCallback((ref LogMessage message) => Debug.LogWarning("EOS [" + message.Category + "] " + message.Message));

        string cacheDirectory = Path.Combine(Application.temporaryCachePath, "EOS");
        Directory.CreateDirectory(cacheDirectory);

        Options options = new Options
        {
            ProductId = credentials["ProductId"],
            SandboxId = credentials["SandboxId"],
            DeploymentId = credentials["DeploymentId"],
            ClientCredentials = new ClientCredentials { ClientId = credentials["ClientId"], ClientSecret = credentials["ClientSecret"] },
            Flags = Application.isEditor ? PlatformFlags.LoadingInEditor : PlatformFlags.DisableOverlay,
            CacheDirectory = cacheDirectory,
        };

        m_platform = PlatformInterface.Create(ref options);
        if (m_platform == null)
            return "the platform couldn't be created. Check the values in " + Path.Combine(EosFolder, "credentials.txt");

        DuetKeepAlive.tick = TickPlatform;

        return null;
    }

    void OnDeviceIdCreated(ref CreateDeviceIdCallbackInfo info)
    {
        // DuplicateNotAllowed means this PC already has a device ID, which is fine
        if (info.ResultCode != Result.Success && info.ResultCode != Result.DuplicateNotAllowed)
        {
            Fail("couldn't create a device ID (" + info.ResultCode + ")");
            return;
        }

        LoginOptions loginOptions = new LoginOptions
        {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken, Token = null },
            UserLoginInfo = new UserLoginInfo { DisplayName = "Moonscraper Duet" },
        };
        m_platform.GetConnectInterface().Login(ref loginOptions, null, OnLoggedIn);
    }

    void OnLoggedIn(ref LoginCallbackInfo info)
    {
        if (info.ResultCode != Result.Success)
        {
            Fail("couldn't sign in (" + info.ResultCode + ")");
            return;
        }

        m_localUser = info.LocalUserId;
        m_socket = new SocketId { SocketName = SocketName };

        P2PInterface p2p = m_platform.GetP2PInterface();
        SetPacketQueueSizeOptions queueOptions = new SetPacketQueueSizeOptions
        {
            IncomingPacketQueueMaxSizeBytes = P2PInterface.MAX_QUEUE_SIZE_UNLIMITED,
            OutgoingPacketQueueMaxSizeBytes = P2PInterface.MAX_QUEUE_SIZE_UNLIMITED,
        };
        p2p.SetPacketQueueSize(ref queueOptions);

        AddNotifyPeerConnectionRequestOptions requestOptions = new AddNotifyPeerConnectionRequestOptions { LocalUserId = m_localUser, SocketId = m_socket };
        m_requestNotification = p2p.AddNotifyPeerConnectionRequest(ref requestOptions, null, OnConnectionRequest);

        AddNotifyPeerConnectionClosedOptions closedOptions = new AddNotifyPeerConnectionClosedOptions { LocalUserId = m_localUser, SocketId = m_socket };
        m_closedNotification = p2p.AddNotifyPeerConnectionClosed(ref closedOptions, null, OnConnectionClosed);

        AddNotifyLobbyMemberStatusReceivedOptions memberOptions = new AddNotifyLobbyMemberStatusReceivedOptions();
        m_memberNotification = m_platform.GetLobbyInterface().AddNotifyLobbyMemberStatusReceived(ref memberOptions, null, OnLobbyMemberStatus);

        m_phase = Phase.Ready;
        Debug.Log("Duet EOS: signed in as " + m_localUser);
    }

    void Fail(string reason)
    {
        Debug.LogWarning("Duet EOS: " + reason);
        m_failure = reason;
        m_phase = Phase.Failed;
    }

    void Shutdown()
    {
        foreach (Link link in new List<Link>(m_links.Values))
            link.Close("Online rooms shut down");
        m_links.Clear();

        if (m_room != null)
        {
            m_room.Stop();
            m_room = null;
        }

        // Give EOS a moment to tell everyone, so they see the session end straight away instead of timing out
        if (m_platform != null && m_phase == Phase.Ready)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ShutdownGraceMs)
            {
                TickPlatform();
                System.Threading.Thread.Sleep(10);
            }
        }

        DuetKeepAlive.tick = null;

        if (m_platform != null)
        {
            if (m_requestNotification != 0)
                m_platform.GetP2PInterface().RemoveNotifyPeerConnectionRequest(m_requestNotification);
            if (m_closedNotification != 0)
                m_platform.GetP2PInterface().RemoveNotifyPeerConnectionClosed(m_closedNotification);
            if (m_memberNotification != 0)
                m_platform.GetLobbyInterface().RemoveNotifyLobbyMemberStatusReceived(m_memberNotification);
            m_requestNotification = m_closedNotification = m_memberNotification = 0;

            m_platform.Release();
            m_platform = null;
        }

        if (m_sdkInitialized)
        {
            PlatformInterface.Shutdown();
            m_sdkInitialized = false;
        }

        if (m_library != IntPtr.Zero)
        {
            Bindings.Unhook();
            FreeLibrary(m_library);
            m_library = IntPtr.Zero;
        }

        m_localUser = null;
    }

    // Not OnApplicationQuit: Unity sends that as soon as the window's X is clicked, before Moonscraper has asked whether
    // to quit, so cancelling would still close the room. Application.quitting only comes once it's really quitting.
    void Awake()
    {
        Application.quitting += Shutdown;
    }

    void OnDestroy()
    {
        Application.quitting -= Shutdown;
    }

    #endregion

    #region Every frame

    void Update()
    {
        if (m_platform == null)
            return;

        TickPlatform();

        if (m_phase != Phase.Ready)
            return;

        ReceivePackets();

        foreach (Link link in new List<Link>(m_links.Values))
            link.Flush();
    }

    void TickPlatform()
    {
        lock (DuetKeepAlive.tickLock)
        {
            if (m_platform != null)
                m_platform.Tick();
        }
    }

    void ReceivePackets()
    {
        P2PInterface p2p = m_platform.GetP2PInterface();
        ReceivePacketOptions options = new ReceivePacketOptions { LocalUserId = m_localUser, MaxDataSizeBytes = P2PInterface.MAX_PACKET_SIZE };

        for (int i = 0; i < MaxPacketsPerFrame; ++i)
        {
            ProductUserId peer = null;
            SocketId socket = new SocketId();
            byte channel;
            uint bytesWritten;
            Result result = p2p.ReceivePacket(ref options, ref peer, ref socket, out channel, new ArraySegment<byte>(m_receiveBuffer), out bytesWritten);
            if (result != Result.Success)
                return;

            if (peer == null || socket.SocketName != SocketName)
                continue;

            Link link = FindOrAcceptLink(peer);
            if (link != null)
                link.OnPacket(m_receiveBuffer, (int)bytesWritten);
        }
    }

    #endregion

    #region Connections

    void OnConnectionRequest(ref OnIncomingConnectionRequestInfo info)
    {
        if (info.SocketId == null || info.SocketId.Value.SocketName != SocketName)
            return;

        FindOrAcceptLink(info.RemoteUserId);
    }

    // A known peer's link, or a new one for someone joining the room being hosted. Null for anyone else.
    Link FindOrAcceptLink(ProductUserId peer)
    {
        Link link;
        if (m_links.TryGetValue(peer.ToString(), out link))
            return link;

        if (m_room == null || m_room.isStopped)
            return null;

        Accept(peer);
        link = new Link(this, peer, null);
        m_links[peer.ToString()] = link;
        m_room.Enqueue(link);
        return link;
    }

    void Accept(ProductUserId peer)
    {
        AcceptConnectionOptions options = new AcceptConnectionOptions { LocalUserId = m_localUser, RemoteUserId = peer, SocketId = m_socket };
        m_platform.GetP2PInterface().AcceptConnection(ref options);
    }

    void OnConnectionClosed(ref OnRemoteConnectionClosedInfo info)
    {
        Link link;
        if (m_links.TryGetValue(info.RemoteUserId.ToString(), out link))
            link.Close(DescribeClose(info.Reason));
    }

    void OnLobbyMemberStatus(ref LobbyMemberStatusReceivedCallbackInfo info)
    {
        bool gone = info.CurrentStatus == LobbyMemberStatus.Left || info.CurrentStatus == LobbyMemberStatus.Disconnected
            || info.CurrentStatus == LobbyMemberStatus.Kicked || info.CurrentStatus == LobbyMemberStatus.Closed;
        if (!gone)
            return;

        foreach (Link link in new List<Link>(m_links.Values))
        {
            bool roomClosed = info.CurrentStatus == LobbyMemberStatus.Closed && link.lobbyId == info.LobbyId;
            if (roomClosed || link.peer.ToString() == info.TargetUserId.ToString())
                link.Close(roomClosed ? "The host closed the room" : "Left the room");
        }
    }

    static string DescribeClose(ConnectionClosedReason reason)
    {
        switch (reason)
        {
            case ConnectionClosedReason.ClosedByPeer: return "The other side closed the connection";
            case ConnectionClosedReason.TimedOut: return "Connection timed out";
            case ConnectionClosedReason.ConnectionFailed: return "Couldn't connect to the other PC";
            default: return "Connection closed (" + reason + ")";
        }
    }

    void RemoveLink(Link link)
    {
        m_links.Remove(link.peer.ToString());

        if (m_platform != null && m_localUser != null)
        {
            CloseConnectionOptions options = new CloseConnectionOptions { LocalUserId = m_localUser, RemoteUserId = link.peer, SocketId = m_socket };
            m_platform.GetP2PInterface().CloseConnection(ref options);

            if (link.lobbyId != null)
            {
                LeaveLobbyOptions leaveOptions = new LeaveLobbyOptions { LocalUserId = m_localUser, LobbyId = link.lobbyId };
                m_platform.GetLobbyInterface().LeaveLobby(ref leaveOptions, null, (ref LeaveLobbyCallbackInfo info) => { });
            }
        }
    }

    long OutgoingQueueBytes()
    {
        if (m_platform == null)
            return 0;

        GetPacketQueueInfoOptions options = new GetPacketQueueInfoOptions();
        PacketQueueInfo info;
        if (m_platform.GetP2PInterface().GetPacketQueueInfo(ref options, out info) != Result.Success)
            return 0;
        return (long)info.OutgoingPacketQueueCurrentSizeBytes;
    }

    // Returns false when the outgoing queue is full and the packet should be tried again next frame
    bool TrySendPacket(Link link, byte[] packet, out string error)
    {
        error = null;
        SendPacketOptions options = new SendPacketOptions
        {
            LocalUserId = m_localUser,
            RemoteUserId = link.peer,
            SocketId = m_socket,
            Channel = 0,
            Data = new ArraySegment<byte>(packet),
            AllowDelayedDelivery = true,
            Reliability = PacketReliability.ReliableOrdered,
        };

        Result result = m_platform.GetP2PInterface().SendPacket(ref options);
        if (result == Result.Success)
            return true;
        if (result != Result.LimitExceeded)
            error = "Couldn't send (" + result + ")";
        return false;
    }

    #endregion

    #region Lobbies

    static string NewRoomCode()
    {
        System.Random random = new System.Random();
        char[] code = new char[RoomCodeLength];
        for (int i = 0; i < code.Length; ++i)
            code[i] = RoomCodeLetters[random.Next(RoomCodeLetters.Length)];
        return new string(code);
    }

    void CreateLobby(Room room, string code)
    {
        CreateLobbyOptions options = new CreateLobbyOptions
        {
            LocalUserId = m_localUser,
            MaxLobbyMembers = DuetRooms.MaxMembers,
            PermissionLevel = LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled = false,
            AllowInvites = false,
            BucketId = BucketId,
            DisableHostMigration = true,
            LobbyId = LobbyIdPrefix + code,
            EnableJoinById = true,
        };
        m_platform.GetLobbyInterface().CreateLobby(ref options, room, (ref CreateLobbyCallbackInfo info) =>
        {
            Room callbackRoom = (Room)info.ClientData;
            callbackRoom.OnCreated(info.ResultCode, code, info.LobbyId);
        });
    }

    void DestroyLobby(string lobbyId)
    {
        if (m_platform == null || m_localUser == null)
            return;

        DestroyLobbyOptions options = new DestroyLobbyOptions { LocalUserId = m_localUser, LobbyId = lobbyId };
        m_platform.GetLobbyInterface().DestroyLobby(ref options, null, (ref DestroyLobbyCallbackInfo info) => { });
    }

    void FindLobby(Join join)
    {
        LobbyInterface lobbies = m_platform.GetLobbyInterface();
        CreateLobbySearchOptions searchOptions = new CreateLobbySearchOptions { MaxResults = 1 };
        LobbySearch search;
        Result result = lobbies.CreateLobbySearch(ref searchOptions, out search);
        if (result != Result.Success)
        {
            join.OnFailed("Couldn't search for the room (" + result + ")");
            return;
        }

        LobbySearchSetLobbyIdOptions idOptions = new LobbySearchSetLobbyIdOptions { LobbyId = LobbyIdPrefix + join.code };
        search.SetLobbyId(ref idOptions);

        LobbySearchFindOptions findOptions = new LobbySearchFindOptions { LocalUserId = m_localUser };
        search.Find(ref findOptions, null, (ref LobbySearchFindCallbackInfo info) =>
        {
            Result found = info.ResultCode;
            LobbyDetails details = null;
            if (found == Result.Success)
            {
                LobbySearchGetSearchResultCountOptions countOptions = new LobbySearchGetSearchResultCountOptions();
                if (search.GetSearchResultCount(ref countOptions) > 0)
                {
                    LobbySearchCopySearchResultByIndexOptions copyOptions = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
                    search.CopySearchResultByIndex(ref copyOptions, out details);
                }
            }
            search.Release();

            if (details == null)
            {
                join.OnFailed(found == Result.Success || found == Result.NotFound
                    ? "No room with the code " + join.code + ". Check the code, or ask the host to make a new room."
                    : "Couldn't search for the room (" + found + ")");
                return;
            }

            JoinLobby(join, details);
        });
    }

    void JoinLobby(Join join, LobbyDetails details)
    {
        LobbyDetailsGetLobbyOwnerOptions ownerOptions = new LobbyDetailsGetLobbyOwnerOptions();
        ProductUserId owner = details.GetLobbyOwner(ref ownerOptions);

        JoinLobbyOptions options = new JoinLobbyOptions { LobbyDetailsHandle = details, LocalUserId = m_localUser, PresenceEnabled = false };
        m_platform.GetLobbyInterface().JoinLobby(ref options, null, (ref JoinLobbyCallbackInfo info) =>
        {
            details.Release();

            if (join.isCancelled)
            {
                if (info.ResultCode == Result.Success)
                {
                    LeaveLobbyOptions leaveOptions = new LeaveLobbyOptions { LocalUserId = m_localUser, LobbyId = info.LobbyId };
                    m_platform.GetLobbyInterface().LeaveLobby(ref leaveOptions, null, (ref LeaveLobbyCallbackInfo leaveInfo) => { });
                }
                return;
            }

            if (info.ResultCode != Result.Success)
            {
                join.OnFailed(info.ResultCode == Result.LobbyTooManyPlayers ? "That room is full." : "Couldn't join the room (" + info.ResultCode + ")");
                return;
            }

            if (owner == null || !owner.IsValid() || owner.ToString() == m_localUser.ToString())
            {
                join.OnFailed("That room's host can't be reached. Ask them to make a new room.");
                return;
            }

            Link existing;
            if (m_links.TryGetValue(owner.ToString(), out existing))
                existing.Close("Replaced");

            Accept(owner);
            Link link = new Link(this, owner, info.LobbyId);
            m_links[owner.ToString()] = link;
            join.OnJoined(link);
        });
    }

    #endregion

    #region Credentials and native loading

    // EOS/credentials.txt holds lines of Name=Value for the five values from the Epic Developer Portal
    static bool TryReadCredentials(out Dictionary<string, string> credentials, out string error)
    {
        credentials = new Dictionary<string, string>();
        string path = Path.Combine(EosFolder, "credentials.txt");
        if (!File.Exists(path))
        {
            error = "no file at " + path;
            return false;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            int split = line.IndexOf('=');
            if (split > 0)
                credentials[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
        }

        foreach (string name in new[] { "ProductId", "SandboxId", "DeploymentId", "ClientId", "ClientSecret" })
        {
            string value;
            if (!credentials.TryGetValue(name, out value) || value.Length == 0)
            {
                error = name + " is missing from " + path;
                return false;
            }
        }

        error = null;
        return true;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32", CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    #endregion

    /// <summary>
    /// A connection to one other person. Messages are framed as a byte stream and split into P2P packets,
    /// which arrive reliably and in order, so the receiver just joins them back up.
    /// </summary>
    class Link : IDuetLink
    {
        readonly DuetEos m_owner;
        readonly Queue<byte[]> m_outgoing = new Queue<byte[]>();
        readonly Queue<Dictionary<string, object>> m_incoming = new Queue<Dictionary<string, object>>();
        readonly DuetFraming.Assembler m_assembler = new DuetFraming.Assembler();
        bool m_closeWhenSent = false;

        public readonly ProductUserId peer;
        public readonly string lobbyId;     // Joiners only: the room to leave when this link closes

        public int id { get; set; }
        public string remoteAddress { get { return "an online room"; } }
        public bool isClosed { get; private set; }
        public string closeReason { get; private set; }

        // What's waiting here plus everything in EOS's own outgoing queue, which all links share
        public long queuedBytes
        {
            get
            {
                long waiting = 0;
                foreach (byte[] packet in m_outgoing)
                    waiting += packet.Length;
                return waiting + m_owner.OutgoingQueueBytes();
            }
        }

        public Link(DuetEos owner, ProductUserId peer, string lobbyId)
        {
            m_owner = owner;
            this.peer = peer;
            this.lobbyId = lobbyId;
        }

        public void Send(Dictionary<string, object> message)
        {
            if (isClosed || m_closeWhenSent)
                return;

            byte[] frame = DuetFraming.Encode(message);
            for (int offset = 0; offset < frame.Length; offset += P2PInterface.MAX_PACKET_SIZE)
            {
                int size = Math.Min(P2PInterface.MAX_PACKET_SIZE, frame.Length - offset);
                byte[] packet = new byte[size];
                Buffer.BlockCopy(frame, offset, packet, 0, size);
                m_outgoing.Enqueue(packet);
            }

            Flush();
        }

        public bool TryReceive(out Dictionary<string, object> message)
        {
            if (m_incoming.Count > 0)
            {
                message = m_incoming.Dequeue();
                return true;
            }

            message = null;
            return false;
        }

        public void Flush()
        {
            while (!isClosed && m_outgoing.Count > 0)
            {
                string error;
                if (!m_owner.TrySendPacket(this, m_outgoing.Peek(), out error))
                {
                    if (error != null)
                        Close(error);
                    return;
                }
                m_outgoing.Dequeue();
            }

            if (m_closeWhenSent && m_outgoing.Count <= 0)
                Close(closeReason);
        }

        public void OnPacket(byte[] data, int count)
        {
            if (isClosed)
                return;

            try
            {
                m_owner.m_receivedMessages.Clear();
                m_assembler.Append(data, 0, count, m_owner.m_receivedMessages);
                foreach (Dictionary<string, object> message in m_owner.m_receivedMessages)
                    m_incoming.Enqueue(message);
            }
            catch (Exception e)
            {
                Close("Got data this version doesn't understand: " + e.Message);
            }
        }

        public void Close(string reason = "Disconnected")
        {
            if (isClosed)
                return;

            isClosed = true;
            closeReason = reason;
            m_outgoing.Clear();
            m_owner.RemoveLink(this);
        }

        public void CloseAfterSending(string reason)
        {
            closeReason = reason;
            m_closeWhenSent = true;
            Flush();
        }
    }

    /// <summary>
    /// The room being hosted: a lobby named after its room code, accepting connections from whoever joins it.
    /// </summary>
    class Room : IDuetListener
    {
        const int MaxCreateAttempts = 3;

        readonly DuetEos m_owner;
        readonly Queue<IDuetLink> m_accepted = new Queue<IDuetLink>();
        bool m_createStarted = false;
        int m_attempts = 0;
        string m_lobbyId;

        public string roomCode { get; private set; }
        public string error { get; private set; }
        public bool isStopped { get; private set; }

        public Room(DuetEos owner)
        {
            m_owner = owner;
        }

        public bool TryAccept(out IDuetLink link)
        {
            link = null;
            if (isStopped)
                return false;

            if (m_owner.m_phase == Phase.Failed)
            {
                error = "Couldn't open an online room: " + m_owner.m_failure;
                return false;
            }

            if (m_owner.m_phase == Phase.Ready && !m_createStarted)
            {
                m_createStarted = true;
                ++m_attempts;
                m_owner.CreateLobby(this, NewRoomCode());
            }

            if (m_accepted.Count <= 0)
                return false;

            link = m_accepted.Dequeue();
            return true;
        }

        public void Enqueue(IDuetLink link)
        {
            m_accepted.Enqueue(link);
        }

        public void OnCreated(Result result, string code, string lobbyId)
        {
            if (isStopped)
            {
                if (result == Result.Success)
                    m_owner.DestroyLobby(lobbyId);
                return;
            }

            if (result == Result.Success)
            {
                roomCode = code;
                m_lobbyId = lobbyId;
                return;
            }

            // Another room may already have this code, so try a new one
            if (result == Result.LobbyLobbyAlreadyExists && m_attempts < MaxCreateAttempts)
            {
                m_createStarted = false;
                return;
            }

            error = "Couldn't open an online room (" + result + ")";
        }

        public void Stop()
        {
            if (isStopped)
                return;

            isStopped = true;
            if (m_lobbyId != null)
                m_owner.DestroyLobby(m_lobbyId);

            if (m_owner.m_room == this)
                m_owner.m_room = null;
        }
    }

    /// <summary>
    /// A joiner finding a room by its code, joining it, and opening a connection to its host.
    /// </summary>
    class Join : IDuetConnector
    {
        readonly DuetEos m_owner;
        bool m_started = false;
        IDuetLink m_link;
        string m_error;

        public readonly string code;
        public bool isCancelled { get; private set; }

        public Join(DuetEos owner, string code)
        {
            m_owner = owner;
            this.code = code;
        }

        public bool TryFinish(out IDuetLink link, out string error)
        {
            if (m_link == null && m_error == null)
            {
                if (code.Length != RoomCodeLength)
                    m_error = "A room code has " + RoomCodeLength + " letters and numbers.";
                else if (m_owner.m_phase == Phase.Failed)
                    m_error = m_owner.m_failure;
                else if (m_owner.m_phase == Phase.Ready && !m_started)
                {
                    m_started = true;
                    m_owner.FindLobby(this);
                }
            }

            link = m_link;
            error = m_error;
            return link != null || error != null;
        }

        public void OnJoined(IDuetLink link)
        {
            m_link = link;
        }

        public void OnFailed(string error)
        {
            m_error = error;
        }

        public void Cancel()
        {
            isCancelled = true;
            if (m_link != null)
                m_link.Close("Cancelled");
        }
    }
}

#endif
