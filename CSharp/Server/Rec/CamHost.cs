using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using BitOperations = System.Numerics.BitOperations;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ScpItdMod.Auth;
using ScpItdMod.Core;
using ScpItdMod.Net;
using ScpItdMod.Rp;

namespace ScpItdMod.Rec
{
    public static class CamHost
    {
        public const double SampleSeconds = 0.1;
        public const long TapeBudget = 24L << 20;
        public const long VoiceBudget = 8L << 20;
        public const long SceneBudget = 12L << 20;
        public const double KeepSeconds = 45 * 60;
        public const float DefaultHalfW = 600.0f;
        public const float DefaultHalfH = 360.0f;
        public const float HearRange = 900.0f;
        public const double VisibilityCache = 0.3;
        public const double RescanSeconds = 15.0;
        public const int SendBytesPerSecond = 64 * 1024;
        public const double SealForFetch = 1.0;
        public const float TerminalReach = 260.0f;

        private const string RangePrefix = "scpitd_range_";
        private const float MaxWatchDistanceSq = 1800.0f * 1800.0f;
        private const double WatchDurationSeconds = 2.0;
        private const double ObserveGraceSeconds = 0.3;
        private const double FullSceneRefreshSeconds = 2.0;
        private const double MinScreenResendSeconds = 1.0;
        private const double VisibilityPruneGraceSeconds = 5.0;
        private const double JobAccessCheckInterval = 0.5;
        private const double ScreenAccessCacheSeconds = 0.25;
        private const int MaxScreenCachedLooks = 256;
        private const int MaxChatRecordLength = 400;
        private const float MaxCamHalfW = 3000.0f;
        private const float MaxCamHalfH = 2000.0f;
        private const float GateRate = 8.0f;
        private const float GateBurst = 16.0f;
        private const int MaxPooledObjects = 64;

        private static readonly Identifier CameraTag = "scpitd_camera".ToIdentifier();
        private static readonly Identifier TerminalTag = "scpitd_terminal".ToIdentifier();

        internal struct VisibilityEntry
        {
            public double Until;
            public bool Seen;
        }

        /// <summary>
        /// Плоская структура данных персонажа для быстрого линейного сканирования в L1-кэше.
        /// </summary>
        internal struct HumanTickSample
        {
            public float X;
            public float Y;
            public Character Character;
            public FigurePose Pose;
            public sbyte PoseState; // 0 = не вычислено, 1 = валидно, -1 = ошибка/пропуск
        }

        internal struct VoiceBufferSlot
        {
            public int Id;
            public int Size;
            public byte[] Data;
        }

        public sealed class Cam
        {
            public Item Item;
            public ushort Id;
            public uint Epoch;
            public double LostAt;
            public string Name;
            public Tape Tape => Frames?.Archive;
            public CameraFrames Frames;
            public SceneTape Scene;
            public readonly List<SceneObject> LiveScene = new List<SceneObject>(SceneTape.InitialObjects);
            public bool LiveSceneReady;
            public bool SceneCaptureDisabled;
            public SceneCapture.CaptureFailure SceneFailure;
            public double NextScene;
            public VoiceStore Voice;
            public bool Recording = true;
            public bool Lost;
            public float HalfW = DefaultHalfW;
            public float HalfH = DefaultHalfH;
            public Vector2 At;

            internal float MinX, MinY, MaxX, MaxY;
            internal float HearMinX, HearMinY, HearMaxX, HearMaxY;

            internal ulong LiveSceneHash;
            internal double StatusCheckedAt = -1.0;
            internal bool CachedPresent;
            internal bool CachedPowered;
            internal readonly Dictionary<Character, VisibilityEntry> Visible = new Dictionary<Character, VisibilityEntry>(16);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal void UpdateBounds(Vector2 pos)
            {
                At = pos;
                float hw = HalfW;
                float hh = HalfH;
                MinX = pos.X - hw;
                MaxX = pos.X + hw;
                MinY = pos.Y - hh;
                MaxY = pos.Y + hh;

                float hhw = hw + HearRange;
                float hhh = hh + HearRange;
                HearMinX = pos.X - hhw;
                HearMaxX = pos.X + hhw;
                HearMinY = pos.Y - hhh;
                HearMaxY = pos.Y + hhh;
            }
        }

        private sealed class Job
        {
            public Client Client;
            public Peer Peer;
            public ushort Terminal;
            public Item TerminalItem;
            public double Checked;
            public uint Generation;
            public Cam Cam;
            public double From;
            public double To;
            public double SentStart = double.NegativeInfinity;
            public int NextSegmentIndex;
            public Segment Current;
            public byte[] Data;
            public int Part;
            public readonly List<int> Pending = new List<int>(16);
            public bool VoiceQueried;
            public int VoiceAt;
            public readonly HashSet<int> Looks = new HashSet<int>();
            public readonly List<VoicePacket> Packets = new List<VoicePacket>();
            public readonly List<byte[]> Owners = new List<byte[]>();

            public void Reset()
            {
                Client = null;
                Peer = null;
                Terminal = 0;
                TerminalItem = null;
                Checked = 0;
                Generation = 0;
                Cam = null;
                From = 0;
                To = 0;
                SentStart = double.NegativeInfinity;
                NextSegmentIndex = 0;
                Current = null;
                Data = null;
                Part = 0;
                Pending.Clear();
                VoiceQueried = false;
                VoiceAt = 0;
                Looks.Clear();
                Packets.Clear();
                Owners.Clear();
            }
        }

        private sealed class Peer
        {
            public readonly RequestSessions Requests = new RequestSessions();
            public Gate Gate, UpdateGate;
            public double Credit;
            public double CreditAt;
        }

        private sealed class ScreenWatch
        {
            public Client Client;
            public Peer Peer;
            public ushort Terminal;
            public uint Epoch, Sequence, Session;
            public double Until;
            public double SentAt, SentTime;
            public ushort SentCamera;
            public ScreenState State = ScreenState.Waiting;
            public ScreenMeta View;
            public readonly Dictionary<int, ulong> Looks = new Dictionary<int, ulong>(32);
            public readonly TapeReader Reader = new TapeReader();
            public readonly List<FigurePose> Poses = new List<FigurePose>(Tape.MaxFigures);
            public readonly List<SceneObject> Scene = new List<SceneObject>(SceneTape.InitialObjects);
            public ulong SentSceneHash;
            public double FullSceneAt;
            public readonly WireWriter Packet = new WireWriter(CamWire.MaxChunk);
            public ScreenMeta Pending;
            public int Offset;
            public uint Definitions;
            public ulong SceneHash;
            public bool FullScene, ReusedScene;

            public ushort AccessCameraId;
            public double AccessValidUntil;
            public bool AccessAllowed;

            public void Init(Client client, Peer peer, ushort terminal, uint session, uint epoch, double until)
            {
                Client = client;
                Peer = peer;
                Terminal = terminal;
                Until = until;
                State = ScreenState.Waiting;
                View = default;
                Pending = default;
                SentTime = 0;
                Definitions = 0;
                SceneHash = 0;
                FullScene = false;
                ReusedScene = false;
                ResetSession(session, epoch);
            }

            public void ResetSession(uint newSession, uint newEpoch)
            {
                SentSceneHash = 0;
                FullSceneAt = double.NegativeInfinity;
                SentCamera = 0;
                SentAt = double.NegativeInfinity;
                Session = newSession;
                Epoch = newEpoch;
                Sequence = 0;
                AccessCameraId = 0;
                AccessValidUntil = -1.0;
                AccessAllowed = false;
                Looks.Clear();
                Reader.Reset();
                Packet.Clear();
                Offset = 0;
            }

            public void ClearForPool()
            {
                Client = null;
                Peer = null;
                Looks.Clear();
                Reader.Reset();
                Poses.Clear();
                Scene.Clear();
                Packet.Clear();
            }
        }

        private sealed class VoicePeer
        {
            public VoiceConsent Consent;
            public readonly ConditionalWeakTable<Client, VoiceCursor> Listeners = new ConditionalWeakTable<Client, VoiceCursor>();
            public Client LastRecipient;
            public VoiceCursor LastCursor;
        }

        private sealed class VoiceCursor
        {
            public VoiceConsent Consent;
        }

        private readonly struct VoiceDelivery
        {
            public readonly Client Sender, Recipient;
            public readonly bool Radio;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public VoiceDelivery(Client sender, Client recipient, bool radio)
            {
                Sender = sender;
                Recipient = recipient;
                Radio = radio;
            }
        }

        private static readonly ConditionalWeakTable<Client, VoicePeer> voicePeers = new ConditionalWeakTable<Client, VoicePeer>();
        private static Client lastVoiceClient;
        private static VoicePeer lastVoicePeer;

        private static readonly List<VoiceDelivery> voiceDeliveries = new List<VoiceDelivery>(32);
        private static readonly WireWriter voiceWriter = new WireWriter(VoiceStore.MaxPacket + 32);
        private static readonly VoiceBufferSlot[] voiceBufferCache = new VoiceBufferSlot[VoipQueue.BUFFER_COUNT];

        private static readonly LookTable looks = new LookTable(CamWire.MaxLooks);
        private static readonly Credentials credentials = new Credentials();
        private static Client lastCredClient;
        private static double lastCredTime = -1.0;
        private static bool lastCredValid;
        private static bool lastCredIsGm;

        private static readonly Capture capture = new Capture(looks, _ => ScpItdMod.People.Identity.UnseenLabel);

        private static readonly List<ScreenWatch> screens = new List<ScreenWatch>(16);
        private static readonly Stack<ScreenWatch> watchPool = new Stack<ScreenWatch>(16);
        private static readonly WireWriter screenPart = new WireWriter(CamWire.MaxChunk);

        private static uint screenEpoch = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        private static uint cameraEpoch = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        private static int screenCursor;

        private static readonly List<Cam> cams = new List<Cam>(32);
        private static readonly Dictionary<ushort, Cam> camsById = new Dictionary<ushort, Cam>(32);
        private static ushort lastFoundCamId;
        private static Cam lastFoundCam;

        private static ushort lastTerminalId;
        private static Item lastTerminalItem;
        private static double lastTerminalCheckTime = -1.0;

        private static readonly List<Cam> activeCamsInRange = new List<Cam>(16);
        private static readonly List<Cam> allowedCams = new List<Cam>(32);

        private static HumanTickSample[] humanSamples = new HumanTickSample[64];
        private static int humanSampleCount;
        private static readonly List<Character> staleCharacters = new List<Character>(16);

        private static readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>(32);
        private static ulong lastPeerAccount;
        private static Peer lastPeer;

        private static readonly List<Job> jobs = new List<Job>(8);
        private static readonly Stack<Job> jobPool = new Stack<Job>(8);

        private static readonly WireWriter out_ = new WireWriter(CamWire.MaxChunk + 512);
        private static readonly WireWriter part = new WireWriter(CamWire.MaxChunk);

        private static double nextSample;
        private static double nextScan;
        private static int cameraSet = -1;
        private static int scannedRevision = -1;

        public static IReadOnlyList<Cam> Cameras => cams;

        public static long Bytes
        {
            get
            {
                long total = looks.Bytes;
                ReadOnlySpan<Cam> span = CollectionsMarshal.AsSpan(cams);
                for (int i = 0; i < span.Length; i++)
                {
                    Cam c = span[i];
                    total += c.Tape.Bytes + c.Voice.Bytes + (c.Scene?.Bytes ?? 0);
                }
                return total;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Cam Find(ushort id)
        {
            if (id == lastFoundCamId && lastFoundCam != null)
            {
                return lastFoundCam;
            }
            if (camsById.TryGetValue(id, out Cam cam))
            {
                lastFoundCamId = id;
                lastFoundCam = cam;
                return cam;
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CanView(Client client, ushort camera)
        {
            Cam cam = Find(camera);
            return cam != null && Allowed(client, cam);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetTerminal(ushort terminalId, double now, out Item terminal)
        {
            if (terminalId == lastTerminalId && lastTerminalCheckTime == now)
            {
                terminal = lastTerminalItem;
                return terminal != null;
            }
            lastTerminalId = terminalId;
            lastTerminalCheckTime = now;
            if (Entity.FindEntityByID(terminalId) is Item item && !item.Removed && item.HasTag(TerminalTag))
            {
                lastTerminalItem = item;
                terminal = item;
                return true;
            }
            lastTerminalItem = null;
            terminal = null;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetVoicePeer(Client client, out VoicePeer peer)
        {
            if (ReferenceEquals(client, lastVoiceClient) && lastVoicePeer != null)
            {
                peer = lastVoicePeer;
                return true;
            }
            if (voicePeers.TryGetValue(client, out peer))
            {
                lastVoiceClient = client;
                lastVoicePeer = peer;
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RefreshStatusIfNeeded(Cam c, double now)
        {
            if (c.StatusCheckedAt == now) { return; }
            c.StatusCheckedAt = now;
            Item item = c.Item;
            if (!c.Lost && item != null && !item.Removed && item.HasTag(CameraTag))
            {
                c.CachedPresent = true;
                c.CachedPowered = SwipeWorld.Powered(item);
                c.UpdateBounds(item.WorldPosition);
            }
            else
            {
                c.CachedPresent = false;
                c.CachedPowered = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Present(Cam c)
        {
            RefreshStatusIfNeeded(c, Timing.TotalTime);
            return c.CachedPresent;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Available(Cam c)
        {
            RefreshStatusIfNeeded(c, Timing.TotalTime);
            return c.CachedPowered;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Live(Cam c) => c.Recording && Available(c);

        private static bool CollectHearingCameras(Vector2 at, double now)
        {
            activeCamsInRange.Clear();
            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                Cam c = camSpan[i];
                if (!c.Recording || c.Lost) { continue; }
                Item item = c.Item;
                if (item == null || item.Removed) { continue; }

                if (c.StatusCheckedAt != now)
                {
                    Vector2 pos = item.WorldPosition;
                    if (MathF.Abs(at.X - pos.X) > c.HalfW + HearRange || MathF.Abs(at.Y - pos.Y) > c.HalfH + HearRange)
                    {
                        continue;
                    }
                    RefreshStatusIfNeeded(c, now);
                }
                else if (at.X < c.HearMinX || at.X > c.HearMaxX || at.Y < c.HearMinY || at.Y > c.HearMaxY)
                {
                    continue;
                }

                if (c.CachedPowered)
                {
                    activeCamsInRange.Add(c);
                }
            }
            return activeCamsInRange.Count > 0;
        }

        private static bool CollectActiveCamerasInRange(Vector2 at, float extraRange, double now)
        {
            if (extraRange == HearRange)
            {
                return CollectHearingCameras(at, now);
            }

            activeCamsInRange.Clear();
            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                Cam c = camSpan[i];
                if (!c.Recording || c.Lost) { continue; }
                Item item = c.Item;
                if (item == null || item.Removed) { continue; }

                Vector2 pos = c.StatusCheckedAt == now ? c.At : item.WorldPosition;
                if (MathF.Abs(at.X - pos.X) > c.HalfW + extraRange || MathF.Abs(at.Y - pos.Y) > c.HalfH + extraRange)
                {
                    continue;
                }

                RefreshStatusIfNeeded(c, now);
                if (c.CachedPowered)
                {
                    activeCamsInRange.Add(c);
                }
            }
            return activeCamsInRange.Count > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint NextNonZeroEpoch(ref uint epoch)
        {
            if (++epoch == 0) { epoch++; }
            return epoch;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SwapRemoveAt<T>(List<T> list, int index)
        {
            int last = list.Count - 1;
            if (index < last)
            {
                list[index] = list[last];
            }
            list.RemoveAt(last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Credit(Peer peer, double now)
        {
            double elapsed = now - peer.CreditAt;
            if (elapsed > 0.0)
            {
                peer.Credit = Math.Min(SendBytesPerSecond, peer.Credit + elapsed * SendBytesPerSecond);
            }
            peer.CreditAt = now;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Peer GetOrCreatePeer(ulong account)
        {
            if (account == lastPeerAccount && lastPeer != null)
            {
                return lastPeer;
            }
            ref Peer peer = ref CollectionsMarshal.GetValueRefOrAddDefault(peers, account, out bool exists);
            if (!exists)
            {
                peer = new Peer();
            }
            lastPeerAccount = account;
            lastPeer = peer;
            return peer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ScreenWatch RentWatch(Client client, Peer peer, ushort terminal, uint session, uint epoch, double until)
        {
            ScreenWatch watch = watchPool.Count > 0 ? watchPool.Pop() : new ScreenWatch();
            watch.Init(client, peer, terminal, session, epoch, until);
            return watch;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnWatch(ScreenWatch watch)
        {
            watch.ClearForPool();
            if (watchPool.Count < MaxPooledObjects)
            {
                watchPool.Push(watch);
            }
        }

        private static void Watch(Client client, Peer peer, ushort terminal, uint session, double now)
        {
            if (session == 0 || client.Character == null) { return; }
            if (!TryGetTerminal(terminal, now, out Item item)) { return; }
            if (Vector2.DistanceSquared(client.Character.WorldPosition, item.WorldPosition) > MaxWatchDistanceSq) { return; }

            double expiration = now + WatchDurationSeconds;
            int clientWatchCount = 0;

            ReadOnlySpan<ScreenWatch> screenSpan = CollectionsMarshal.AsSpan(screens);
            for (int i = 0; i < screenSpan.Length; i++)
            {
                ScreenWatch screen = screenSpan[i];
                if (!ReferenceEquals(screen.Client, client)) { continue; }

                clientWatchCount++;
                if (screen.Terminal != terminal) { continue; }

                if (screen.Session != session)
                {
                    if (unchecked((int)(session - screen.Session)) <= 0) { return; }
                    screen.ResetSession(session, NextNonZeroEpoch(ref screenEpoch));
                }
                screen.Peer = peer;
                screen.Until = expiration;
                return;
            }

            if (clientWatchCount >= ScreenFrameWire.MaxViews) { return; }

            screens.Add(RentWatch(client, peer, terminal, session, NextNonZeroEpoch(ref screenEpoch), expiration));
        }

        public static bool Observes(Client client, Item item)
        {
            if (screens.Count == 0 || client == null || item == null || item.Removed)
            {
                return false;
            }

            Rectangle bounds = item.WorldRect;
            double now = Timing.TotalTime;
            ReadOnlySpan<ScreenWatch> screenSpan = CollectionsMarshal.AsSpan(screens);

            for (int i = 0; i < screenSpan.Length; i++)
            {
                ScreenWatch screen = screenSpan[i];
                if (ReferenceEquals(screen.Client, client) &&
                    screen.Until >= now &&
                    now - screen.SentAt <= ObserveGraceSeconds &&
                    screen.View.Live &&
                    screen.View.Intersects(bounds.X, bounds.Y, bounds.Width, bounds.Height))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CheckScreenAccess(ScreenWatch screen, Cam cam, double now)
        {
            if (screen.AccessCameraId == cam.Id && now < screen.AccessValidUntil)
            {
                return screen.AccessAllowed;
            }
            bool allowed = Allowed(screen.Client, cam, now);
            screen.AccessCameraId = cam.Id;
            screen.AccessValidUntil = now + ScreenAccessCacheSeconds;
            screen.AccessAllowed = allowed;
            return allowed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ScreenState EvaluateScreenState(ScreenWatch screen, bool hasSource, Cam cam, bool isLive, double now)
        {
            if (!hasSource) { return ScreenState.Waiting; }
            if (cam == null) { return ScreenState.Lost; }
            if (!CheckScreenAccess(screen, cam, now)) { return ScreenState.NoRights; }
            if (isLive)
            {
                RefreshStatusIfNeeded(cam, now);
                if (!cam.CachedPresent) { return ScreenState.Lost; }
                if (!cam.CachedPowered) { return ScreenState.NoPower; }
            }
            return ScreenState.Picture;
        }

        private static void Screens(double now)
        {
            for (int i = screens.Count - 1; i >= 0; i--)
            {
                ScreenWatch screen = screens[i];
                if (screen.Until >= now && screen.Client.Connection != null) { continue; }
                SwapRemoveAt(screens, i);
                ReturnWatch(screen);
            }

            int count = screens.Count;
            if (count == 0) { return; }

            for (int offset = 0; offset < count; offset++)
            {
                ScreenWatch screen = screens[(screenCursor + offset) % count];
                Peer peer = screen.Peer;
                if (peer == null) { continue; }

                Credit(peer, now);

                bool hasSource = Pc.PcStations.CameraSource(screen.Client, screen.Terminal, out ushort id, out double time);
                Cam cam = hasSource ? Find(id) : null;
                bool isLive = time == double.MaxValue;
                ushort camId = cam?.Id ?? 0;

                ScreenState state = EvaluateScreenState(screen, hasSource, cam, isLive, now);

                if (screen.Packet.Length > 0)
                {
                    if (screen.Pending.Camera == camId && screen.Pending.Live == isLive && screen.Pending.State == state)
                    {
                        SendScreen(screen, peer, now);
                        continue;
                    }
                    screen.Packet.Clear();
                    screen.Offset = 0;
                }

                screen.Poses.Clear();
                if (state != ScreenState.Picture)
                {
                    screen.View = default;
                }
                else
                {
                    state = cam.Frames.Read(time, screen.Reader, screen.Poses, out time);
                }

                List<SceneObject> activeScene = null;
                ulong currentHash = 0;
                float historicX = cam?.At.X ?? 0.0f;
                float historicY = cam?.At.Y ?? 0.0f;

                if (state == ScreenState.Picture && cam?.Scene != null)
                {
                    if (isLive)
                    {
                        if (cam.LiveSceneReady)
                        {
                            activeScene = cam.LiveScene;
                            currentHash = cam.LiveSceneHash;
                        }
                    }
                    else
                    {
                        screen.Scene.Clear();
                        if (cam.Scene.Read(time, screen.Scene, out float recordedX, out float recordedY))
                        {
                            activeScene = screen.Scene;
                            currentHash = SceneWire.Hash(screen.Scene);
                            historicX = recordedX;
                            historicY = recordedY;
                        }
                    }
                }

                if (screen.Sequence != 0 &&
                    screen.SentCamera == camId &&
                    screen.State == state &&
                    (state != ScreenState.Picture || screen.SentTime == time) &&
                    now - screen.SentAt < MinScreenResendSeconds)
                {
                    continue;
                }

                NextNonZeroEpoch(ref screen.Sequence);

                var meta = new ScreenMeta
                {
                    Terminal = screen.Terminal,
                    Camera = camId,
                    Epoch = screen.Epoch,
                    Sequence = screen.Sequence,
                    Session = screen.Session,
                    State = state,
                    Time = double.IsFinite(time) && time != double.MaxValue ? time : now,
                    X = historicX,
                    Y = historicY,
                    HalfW = cam?.HalfW ?? 0.0f,
                    HalfH = cam?.HalfH ?? 0.0f,
                    Live = isLive
                };

                bool reference = activeScene != null &&
                                 currentHash == screen.SentSceneHash &&
                                 screen.SentCamera == meta.Camera &&
                                 now - screen.FullSceneAt < FullSceneRefreshSeconds;

                screen.Definitions = ScreenFrameWire.Write(
                    screen.Packet, meta, screen.Poses, looks, screen.Looks,
                    activeScene, reference, out screen.FullScene);

                screen.Pending = meta;
                screen.Offset = 0;
                screen.SceneHash = currentHash;
                screen.ReusedScene = reference;

                SendScreen(screen, peer, now);
            }

            screenCursor = (screenCursor + 1) % count;
        }

        private static void SendScreen(ScreenWatch screen, Peer peer, double now)
        {
            ScreenMeta meta = screen.Pending;
            bool fragmented = screen.Packet.Length > CamWire.MaxChunk;
            int length = fragmented ? ScreenFrameParts.Write(screenPart, meta, screen.Packet.Span, screen.Offset) : screen.Packet.Length;
            ReadOnlySpan<byte> packet = fragmented ? screenPart.Span : screen.Packet.Span;

            if (meta.State == ScreenState.Picture && peer.Credit < packet.Length) { return; }

            bool sendReliable = fragmented || screen.Definitions != 0 || screen.FullScene || meta.Sequence == 1 || screen.State != meta.State;
            ModNet.SendBytes(CamWire.Channel, screen.Client.Connection, packet, sendReliable ? DeliveryMethod.Reliable : DeliveryMethod.Unreliable);

            peer.Credit = Math.Max(0.0, peer.Credit - packet.Length);
            screen.Offset += length;
            if (screen.Offset < screen.Packet.Length) { return; }

            screen.SentAt = now;
            screen.SentTime = meta.Time;
            screen.SentCamera = meta.Camera;
            screen.State = meta.State;
            screen.View = meta;
            screen.SentSceneHash = (screen.FullScene || screen.ReusedScene) ? screen.SceneHash : 0;

            if (screen.FullScene) { screen.FullSceneAt = now; }

            uint defs = screen.Definitions;
            if (defs != 0)
            {
                ReadOnlySpan<FigurePose> poseSpan = CollectionsMarshal.AsSpan(screen.Poses);
                Dictionary<int, ulong> screenLooks = screen.Looks;
                while (defs != 0)
                {
                    int i = BitOperations.TrailingZeroCount(defs);
                    defs &= defs - 1;
                    if ((uint)i < (uint)poseSpan.Length)
                    {
                        int lookId = poseSpan[i].Look;
                        LookDesc look = looks.Get(lookId);
                        if (look != null)
                        {
                            CollectionsMarshal.GetValueRefOrAddDefault(screenLooks, lookId, out _) = look.Hash;
                        }
                    }
                }
                if (screenLooks.Count > MaxScreenCachedLooks)
                {
                    screenLooks.Clear();
                }
            }

            screen.Packet.Clear();
            screen.Offset = 0;
        }

        public static void Attach(Harmony harmony)
        {
            cameraSet = Tagged.Watch(CameraTag);
            ModNet.ReceiveBytes(CamWire.Channel, CamWire.MaxRequest, OnRequest);
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.Update)),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(Tick)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Kill)),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(KillPostfix)));
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), nameof(RangedWeapon.Use)),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(ShotPostfix)));
            harmony.Patch(AccessTools.Method(typeof(VoipServer), nameof(VoipServer.Read)),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(VoicePostfix)));
            harmony.Patch(AccessTools.Method(typeof(VoipServer), "CanReceive"),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(VoiceCanReceivePostfix)));
            harmony.Patch(AccessTools.Method(typeof(VoipServer), nameof(VoipServer.SendToClients)),
                prefix: ModuleHost.Bind(typeof(CamHost), nameof(VoiceSendPrefix)),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(VoiceSendPostfix)),
                finalizer: ModuleHost.Bind(typeof(CamHost), nameof(VoiceSendFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.SendChatMessage)),
                postfix: ModuleHost.Bind(typeof(CamHost), nameof(ChatPostfix)));
            RpHost.EmoteStarted += OnEmote;
            ModCommands.Register("scpitd_cams", "server: camera archive memory and recording state", _ => Report());
        }

        public static void RoundStart()
        {
            for (int i = jobs.Count - 1; i >= 0; i--) { DropJob(i); }
            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                Cam c = camSpan[i];
                c.Tape?.Clear();
                c.Scene?.Clear();
                c.Voice?.Clear();
                c.Visible.Clear();
            }
            cams.Clear();
            camsById.Clear();
            lastFoundCamId = 0;
            lastFoundCam = null;

            lastTerminalId = 0;
            lastTerminalItem = null;
            lastTerminalCheckTime = -1.0;

            lastCredClient = null;
            lastCredTime = -1.0;

            activeCamsInRange.Clear();
            allowedCams.Clear();
            Array.Clear(humanSamples, 0, humanSampleCount);
            humanSampleCount = 0;
            staleCharacters.Clear();
            capture.Clear();
            SceneCapture.Reset();
            looks.Clear();
            voicePeers.Clear();
            lastVoiceClient = null;
            lastVoicePeer = null;
            voiceDeliveries.Clear();
            Array.Clear(voiceBufferCache, 0, voiceBufferCache.Length);

            for (int i = 0; i < screens.Count; i++)
            {
                ReturnWatch(screens[i]);
            }
            screens.Clear();
            screenCursor = 0;
            peers.Clear();
            lastPeerAccount = 0;
            lastPeer = null;
            nextSample = 0;
            scannedRevision = -1;
            nextScan = 0;
        }

        public static void Release()
        {
            RoundStart();
            RpHost.EmoteStarted -= OnEmote;
        }

        private static void Scan()
        {
            double now = Timing.TotalTime;
            bool countChanged = false;

            for (int i = cams.Count - 1; i >= 0; i--)
            {
                Cam c = cams[i];
                if (c.Lost && now - c.LostAt > KeepSeconds)
                {
                    Retire(c);
                    countChanged = true;
                }
            }

            ReadOnlySpan<Cam> currentCams = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < currentCams.Length; i++)
            {
                Cam c = currentCams[i];
                if (!c.Lost && (c.Item == null || c.Item.Removed || !c.Item.HasTag(CameraTag)))
                {
                    c.Lost = true;
                    c.LostAt = now;
                    c.CachedPresent = false;
                    c.CachedPowered = false;
                    c.Tape.MarkGap(now);
                    c.Visible.Clear();
                }
            }

            IReadOnlyList<Item> tagged = Tagged.Of(cameraSet);
            for (int t = 0; t < tagged.Count; t++)
            {
                Item item = tagged[t];
                if (item.Removed) { continue; }

                Cam existing = Find(item.ID);
                if (existing != null)
                {
                    if (ReferenceEquals(existing.Item, item) && !existing.Lost) { continue; }
                    Retire(existing);
                    countChanged = true;
                }

                if (cams.Count >= CamWire.MaxCameras) { break; }

                var cam = new Cam
                {
                    Item = item,
                    Id = item.ID,
                    Epoch = NextNonZeroEpoch(ref cameraEpoch),
                    Name = item.Name
                };

                ParseCameraRangeTags(item, cam);
                cam.UpdateBounds(item.WorldPosition);
                cams.Add(cam);
                camsById[cam.Id] = cam;
                lastFoundCamId = cam.Id;
                lastFoundCam = cam;
                countChanged = true;
            }

            if (countChanged)
            {
                Rebudget();
            }
        }

        private static void ParseCameraRangeTags(Item item, Cam cam)
        {
            foreach (Identifier tag in item.GetTags())
            {
                string val = tag.Value;
                if (val.StartsWith(RangePrefix, StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(val.AsSpan(RangePrefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                    r > 0.0f)
                {
                    cam.HalfW = Math.Min(r, MaxCamHalfW);
                    cam.HalfH = Math.Min(r * 0.6f, MaxCamHalfH);
                }
            }
        }

        private static void Retire(Cam cam)
        {
            for (int i = jobs.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(jobs[i].Cam, cam)) { DropJob(i); }
            }
            cam.Tape?.Clear();
            cam.Scene?.Clear();
            cam.Voice?.Clear();
            cam.Visible.Clear();
            cams.Remove(cam);
            camsById.Remove(cam.Id);
            if (ReferenceEquals(lastFoundCam, cam))
            {
                lastFoundCam = null;
                lastFoundCamId = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Job RentJob(Client client, Peer peer, ushort terminal, Item terminalItem, double now, uint generation, Cam cam, double from, double to)
        {
            Job job = jobPool.Count > 0 ? jobPool.Pop() : new Job();
            job.Client = client;
            job.Peer = peer;
            job.Terminal = terminal;
            job.TerminalItem = terminalItem;
            job.Checked = now;
            job.Generation = generation;
            job.Cam = cam;
            job.From = from;
            job.To = to;
            job.SentStart = double.NegativeInfinity;
            job.NextSegmentIndex = 0;
            return job;
        }

        private static void DropJob(int index)
        {
            Job job = jobs[index];
            foreach (int id in job.Looks)
            {
                looks.Release(id);
            }
            SwapRemoveAt(jobs, index);
            job.Reset();
            if (jobPool.Count < MaxPooledObjects)
            {
                jobPool.Push(job);
            }
        }

        private static void Rebudget()
        {
            int n = Math.Max(4, cams.Count);
            long tapeBudget = TapeBudget / n;
            long voiceBudget = VoiceBudget / n;
            long sceneBudget = SceneBudget / n;

            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                Cam c = camSpan[i];
                if (c.Frames == null) { c.Frames = new CameraFrames(new Tape(tapeBudget, KeepSeconds, looks)); }
                else if (c.Tape.Budget != tapeBudget) { c.Tape.Resize(tapeBudget); }

                if (c.Scene == null) { c.Scene = new SceneTape(sceneBudget, KeepSeconds); }
                else if (c.Scene.Budget != sceneBudget) { c.Scene.Resize(sceneBudget); }

                if (c.Voice == null) { c.Voice = new VoiceStore(voiceBudget, KeepSeconds); }
                else if (c.Voice.Budget != voiceBudget) { c.Voice.Resize(voiceBudget); }
            }
        }

        public static void Tick()
        {
            double now = Timing.TotalTime;
            Pump(now);

            int revision = Tagged.Revision(cameraSet);
            if (revision != scannedRevision)
            {
                scannedRevision = revision;
                Scan();
            }

            if (now >= nextScan)
            {
                nextScan = now + RescanSeconds;
                Scan();
                capture.Prune();
                PruneVisibility(now);
            }

            if (now < nextSample) { return; }
            nextSample = now + SampleSeconds;

            if (cams.Count == 0)
            {
                Screens(now);
                return;
            }

            PopulateHumansFlat();

            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                SampleCamera(camSpan[i], now);
            }

            Screens(now);
        }

        /// <summary>
        /// Собирает всех записываемых и не скрытых людей в плоский массив структур в L1-кэше.
        /// Полностью заменяет тяжелый Grid&lt;Character&gt; и Dictionary.
        /// </summary>
        private static void PopulateHumansFlat()
        {
            IReadOnlyList<Character> humans = Humans.All;
            int total = humans.Count;
            if (humanSamples.Length < total)
            {
                int newSize = (int)BitOperations.RoundUpToPowerOf2((uint)total);
                humanSamples = new HumanTickSample[newSize];
            }

            int count = 0;
            for (int i = 0; i < total; i++)
            {
                Character c = humans[i];
                if (Capture.Recordable(c) && !RpWorld.IsHidden(c))
                {
                    Vector2 pos = c.WorldPosition;
                    ref HumanTickSample s = ref humanSamples[count++];
                    s.X = pos.X;
                    s.Y = pos.Y;
                    s.Character = c;
                    s.PoseState = 0;
                }
            }
            humanSampleCount = count;
        }

        private static void SampleCamera(Cam cam, double now)
        {
            RefreshStatusIfNeeded(cam, now);
            if (!cam.Frames.Begin(now, cam.CachedPowered, cam.Recording))
            {
                cam.Scene?.Gap(now);
                cam.LiveScene.Clear();
                cam.LiveSceneReady = false;
                cam.LiveSceneHash = 0;
                return;
            }

            SampleCameraScene(cam, now, cam.At);

            int hCount = humanSampleCount;
            if (hCount > 0)
            {
                float minX = cam.MinX, maxX = cam.MaxX;
                float minY = cam.MinY, maxY = cam.MaxY;
                Span<HumanTickSample> samples = humanSamples.AsSpan(0, hCount);

                for (int k = 0; k < samples.Length && cam.Frames.Count < Tape.MaxFigures; k++)
                {
                    ref HumanTickSample sample = ref samples[k];
                    if (sample.X < minX || sample.X > maxX || sample.Y < minY || sample.Y > maxY)
                    {
                        continue;
                    }

                    if (!CheckVisibilityCached(cam, sample.Character, now))
                    {
                        continue;
                    }

                    if (sample.PoseState == 0)
                    {
                        sample.PoseState = capture.Pose(sample.Character, out sample.Pose) ? (sbyte)1 : (sbyte)-1;
                    }

                    if (sample.PoseState > 0)
                    {
                        cam.Frames.Figure(sample.Pose);
                    }
                }
            }

            cam.Frames.End();
        }

        private static void SampleCameraScene(Cam cam, double now, Vector2 at)
        {
            if (cam.SceneCaptureDisabled)
            {
                cam.LiveScene.Clear();
                cam.LiveSceneReady = false;
                cam.LiveSceneHash = 0;
                cam.Scene?.Gap(now);
                return;
            }

            if (now < cam.NextScene) { return; }
            cam.NextScene = now + SceneTape.SampleSeconds;

            cam.LiveSceneReady = SceneCapture.Collect(now, at.X, at.Y, cam.HalfW, cam.HalfH, cam.LiveScene);
            SceneCapture.CaptureFailure failure = SceneCapture.Failure;

            if (!cam.LiveSceneReady && !failure.Equals(cam.SceneFailure))
            {
                ModLog.Error($"camera #{cam.Id} scene capture failed: {failure}");
            }
            cam.SceneFailure = failure;

            if (!cam.LiveSceneReady)
            {
                if (failure.Reason == SceneCapture.ObjectLimitReason)
                {
                    cam.SceneCaptureDisabled = true;
                }
                cam.LiveScene.Clear();
                cam.LiveSceneHash = 0;
                cam.Scene?.Gap(now);
                return;
            }

            cam.LiveSceneHash = SceneWire.Hash(cam.LiveScene);
            if (cam.Recording)
            {
                cam.Scene.Sample(now, at.X, at.Y, cam.LiveScene);
            }
            else
            {
                cam.Scene.Gap(now);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CheckVisibilityCached(Cam cam, Character c, double now)
        {
            ref VisibilityEntry entry = ref CollectionsMarshal.GetValueRefOrAddDefault(cam.Visible, c, out bool exists);
            if (exists && now < entry.Until)
            {
                return entry.Seen;
            }

            bool seen = ISpatialEntity.IsCharacterVisible(c, cam.Item, seeThroughWindows: true, checkFacing: false);
            entry.Seen = seen;
            entry.Until = now + VisibilityCache;
            return seen;
        }

        private static void PruneVisibility(double now)
        {
            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                Dictionary<Character, VisibilityEntry> vis = camSpan[i].Visible;
                if (vis.Count == 0) { continue; }

                staleCharacters.Clear();
                foreach (var pair in vis)
                {
                    if (now > pair.Value.Until + VisibilityPruneGraceSeconds || pair.Key.Removed)
                    {
                        staleCharacters.Add(pair.Key);
                    }
                }

                ReadOnlySpan<Character> staleSpan = CollectionsMarshal.AsSpan(staleCharacters);
                for (int k = 0; k < staleSpan.Length; k++)
                {
                    vis.Remove(staleSpan[k]);
                }
            }
            staleCharacters.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchNoteToActiveCams(in TapeNote note)
        {
            ReadOnlySpan<Cam> span = CollectionsMarshal.AsSpan(activeCamsInRange);
            for (int i = 0; i < span.Length; i++)
            {
                span[i].Tape.Note(note);
            }
        }

        public static void KillPostfix(Character __instance)
        {
            if (cams.Count == 0 || (!Capture.Recordable(__instance) && __instance?.IsHuman != true)) { return; }
            double now = Timing.TotalTime;
            if (!CollectActiveCamerasInRange(__instance.WorldPosition, 0.0f, now)) { return; }

            DispatchNoteToActiveCams(new TapeNote
            {
                Time = now,
                Kind = TapeEvent.Death,
                Subject = Capture.SubjectOf(__instance)
            });
        }

        public static void ShotPostfix(RangedWeapon __instance, Character character, bool __result)
        {
            if (!__result || cams.Count == 0 || __instance?.Item == null) { return; }
            double now = Timing.TotalTime;
            Item weaponItem = __instance.Item;
            if (!CollectHearingCameras(weaponItem.WorldPosition, now)) { return; }

            DispatchNoteToActiveCams(new TapeNote
            {
                Time = now,
                Kind = TapeEvent.Shot,
                Subject = character != null ? Capture.SubjectOf(character) : default,
                Value = weaponItem.ID,
                Text = weaponItem.Prefab.Identifier.Value
            });
        }

        private static void OnEmote(Character actor, EmoteDef def, int variant)
        {
            if (cams.Count == 0) { return; }
            double now = Timing.TotalTime;
            if (!CollectActiveCamerasInRange(actor.WorldPosition, def.Range, now)) { return; }

            DispatchNoteToActiveCams(new TapeNote
            {
                Time = now,
                Kind = TapeEvent.Emote,
                Subject = Capture.SubjectOf(actor),
                Value = (uint)(def.Index | (variant << 8)),
                Text = def.Id
            });
        }

        public static void ChatPostfix(string message, ChatMessageType? type, Client senderClient, Character senderCharacter)
        {
            if (cams.Count == 0) { return; }
            Character speaker = senderCharacter ?? senderClient?.Character;
            if (speaker == null || speaker.IsDead) { return; }
            if (type != ChatMessageType.Default && type != null) { return; }
            if (string.IsNullOrWhiteSpace(message) || message.Length > MaxChatRecordLength || message[0] == '/')
            {
                return;
            }

            double now = Timing.TotalTime;
            if (!CollectHearingCameras(speaker.WorldPosition, now)) { return; }

            DispatchNoteToActiveCams(new TapeNote
            {
                Time = now,
                Kind = TapeEvent.Speech,
                Subject = Capture.SubjectOf(speaker),
                Text = message
            });
        }

        public static void VoicePostfix(Client connectedClient)
        {
            Character speaker = connectedClient?.Character;
            VoipQueue queue = connectedClient?.VoipQueue;
            if (speaker == null || queue == null || speaker.IsDead || connectedClient.Muted || cams.Count == 0) { return; }
            if (!TryGetVoicePeer(connectedClient, out VoicePeer peer) || !peer.Consent.Allowed) { return; }

            ushort latest = queue.LatestBufferID;
            if (!peer.Consent.Take(latest, VoipQueue.BUFFER_COUNT, out ushort first, out int count)) { return; }

            double now = Timing.TotalTime;
            if (!CollectHearingCameras(speaker.WorldPosition, now)) { return; }

            Subject subject = Capture.SubjectOf(speaker);
            const double secondsPerBuffer = (double)VoipConfig.BUFFER_SIZE / VoipConfig.FREQUENCY;
            ReadOnlySpan<Cam> hearSpan = CollectionsMarshal.AsSpan(activeCamsInRange);

            for (int k = 0; k < count; k++)
            {
                ushort id = (ushort)(first + k);
                queue.RetrieveBuffer(id, out int size, out byte[] data);
                if (size <= 0 || data == null) { continue; }

                double timestamp = now - (ushort)(latest - id) * secondsPerBuffer;
                ReadOnlySpan<byte> payload = data.AsSpan(0, size);

                for (int i = 0; i < hearSpan.Length; i++)
                {
                    hearSpan[i].Voice.Add(timestamp, subject, 0, payload);
                }
            }
        }

        public static void VoiceSendPrefix() => voiceDeliveries.Clear();
        public static void VoiceSendFinalizer() => voiceDeliveries.Clear();

        public static void VoiceCanReceivePostfix(Client sender, Client recipient, bool isRadio, bool __result)
        {
            if (__result &&
                !ReferenceEquals(sender, recipient) &&
                sender.Character != null &&
                recipient.Character != null &&
                !recipient.Character.IsDead &&
                Screen.Selected == GameMain.GameScreen &&
                TryGetVoicePeer(sender, out VoicePeer peer) &&
                peer.Consent.Allowed)
            {
                voiceDeliveries.Add(new VoiceDelivery(sender, recipient, isRadio));
            }
        }

        public static void VoiceSendPostfix()
        {
            ReadOnlySpan<VoiceDelivery> deliveries = CollectionsMarshal.AsSpan(voiceDeliveries);
            if (deliveries.Length == 0) { return; }

            const float secondsPerBuffer = (float)VoipConfig.BUFFER_SIZE / VoipConfig.FREQUENCY;
            double now = Timing.TotalTime;

            Client cachedSender = null;
            VoicePeer cachedSource = null;
            Subject cachedSubject = default;

            int serializedBufferId = -1;
            bool serializedRadio = false;

            for (int d = 0; d < deliveries.Length; d++)
            {
                ref readonly VoiceDelivery delivery = ref deliveries[d];
                Client sender = delivery.Sender;
                Client recipient = delivery.Recipient;

                if (recipient.Connection == null || sender.Character == null || sender.VoipQueue == null) { continue; }

                if (!ReferenceEquals(sender, cachedSender))
                {
                    if (!TryGetVoicePeer(sender, out cachedSource) || !cachedSource.Consent.Allowed)
                    {
                        cachedSender = null;
                        continue;
                    }
                    cachedSender = sender;
                    cachedSubject = Capture.SubjectOf(sender.Character);
                    serializedBufferId = -1;
                    for (int i = 0; i < voiceBufferCache.Length; i++)
                    {
                        voiceBufferCache[i].Id = -1;
                    }
                }

                VoiceCursor cursor;
                if (ReferenceEquals(recipient, cachedSource.LastRecipient) && cachedSource.LastCursor != null)
                {
                    cursor = cachedSource.LastCursor;
                }
                else
                {
                    cursor = cachedSource.Listeners.GetValue(recipient, static _ => new VoiceCursor());
                    cachedSource.LastRecipient = recipient;
                    cachedSource.LastCursor = cursor;
                }

                if (cursor.Consent.Revision != cachedSource.Consent.Revision)
                {
                    cursor.Consent = cachedSource.Consent.Fork();
                }

                VoipQueue queue = sender.VoipQueue;
                ushort latest = queue.LatestBufferID;
                if (!cursor.Consent.Take(latest, VoipQueue.BUFFER_COUNT, out ushort first, out int count)) { continue; }

                ulong account = Access.Id(recipient);
                if (account == 0) { continue; }

                Peer budget = GetOrCreatePeer(account);
                Credit(budget, now);

                for (int k = 0; k < count; k++)
                {
                    ushort id = (ushort)(first + k);

                    if (serializedBufferId != id || serializedRadio != delivery.Radio)
                    {
                        ref VoiceBufferSlot slot = ref voiceBufferCache[id % VoipQueue.BUFFER_COUNT];
                        int size;
                        byte[] data;

                        if (slot.Id == id)
                        {
                            size = slot.Size;
                            data = slot.Data;
                        }
                        else
                        {
                            queue.RetrieveBuffer(id, out size, out data);
                            slot.Id = id;
                            slot.Size = size;
                            slot.Data = data;
                        }

                        if (data == null || size <= 0 || size > VoipConfig.MAX_COMPRESSED_SIZE)
                        {
                            continue;
                        }

                        float lag = (ushort)(latest - id) * secondsPerBuffer;
                        var frame = new VoiceFrame(sender.SessionId, cachedSubject, id, lag, delivery.Radio, data.AsSpan(0, size));
                        VoiceWire.Write(voiceWriter, frame);
                        serializedBufferId = id;
                        serializedRadio = delivery.Radio;
                    }

                    if (budget.Credit < voiceWriter.Length) { continue; }
                    ModNet.SendBytes(CamWire.Channel, recipient.Connection, voiceWriter.Span, DeliveryMethod.Unreliable);
                    budget.Credit -= voiceWriter.Length;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool PrepareClientCredentials(Client client, double now, out bool isGm)
        {
            if (ReferenceEquals(client, lastCredClient) && lastCredTime == now)
            {
                isGm = lastCredIsGm;
                return lastCredValid;
            }

            lastCredClient = client;
            lastCredTime = now;

            isGm = Access.Gm(client);
            lastCredIsGm = isGm;
            if (isGm)
            {
                lastCredValid = true;
                return true;
            }

            Character me = client.Character;
            if (me == null)
            {
                lastCredValid = false;
                return false;
            }

            Item card = SwipeWorld.Card(me);
            SwipeWorld.CredentialsOf(card, credentials);
            lastCredValid = true;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AllowedWithPreparedCredentials(Cam cam, bool isGm)
        {
            if (isGm) { return true; }
            return cam.Item != null && AuthWorld.ReaderPolicy(cam.Item).Allows(credentials);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Allowed(Client client, Cam cam, double now)
        {
            return PrepareClientCredentials(client, now, out bool isGm) && AllowedWithPreparedCredentials(cam, isGm);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Allowed(Client client, Cam cam)
        {
            return Allowed(client, cam, Timing.TotalTime);
        }

        private static CamDeny AtTerminal(Client client, ushort terminalId, double now, out Item terminalItem)
        {
            terminalItem = null;
            if (Access.Gm(client) && terminalId == 0) { return CamDeny.None; }

            Character me = client.Character;
            if (me == null || me.IsDead || !RpWorld.Conscious(me)) { return CamDeny.NoTerminal; }
            if (!TryGetTerminal(terminalId, now, out terminalItem))
            {
                return CamDeny.NoTerminal;
            }

            return Pc.PcStations.Use(client, terminalId) switch
            {
                Pc.PcError.Ok => CamDeny.None,
                Pc.PcError.NoPower => CamDeny.NoPower,
                Pc.PcError.Busy => CamDeny.Busy,
                _ => CamDeny.TooFar
            };
        }

        private static CamDeny CheckJobTerminal(Job job, double now)
        {
            Client client = job.Client;
            ushort terminalId = job.Terminal;
            if (Access.Gm(client) && terminalId == 0) { return CamDeny.None; }

            Character me = client.Character;
            if (me == null || me.IsDead || !RpWorld.Conscious(me)) { return CamDeny.NoTerminal; }

            Item term = job.TerminalItem;
            if (term == null || term.Removed || !term.HasTag(TerminalTag))
            {
                if (!TryGetTerminal(terminalId, now, out term))
                {
                    job.TerminalItem = null;
                    return CamDeny.NoTerminal;
                }
                job.TerminalItem = term;
            }

            return Pc.PcStations.Use(client, terminalId) switch
            {
                Pc.PcError.Ok => CamDeny.None,
                Pc.PcError.NoPower => CamDeny.NoPower,
                Pc.PcError.Busy => CamDeny.Busy,
                _ => CamDeny.TooFar
            };
        }

        private static void OnRequest(Client sender, uint nonce, uint seq, byte[] payload)
        {
            ulong account = Access.Id(sender);
            if (account == 0 || payload.Length < 2 || payload[0] != CamWire.Proto) { return; }

            bool isWatchUpdate = payload[1] == (byte)CamMsg.ScreenWatch;
            Peer peer = GetOrCreatePeer(account);
            double now = Timing.TotalTime;

            bool gatePassed = isWatchUpdate
                ? peer.UpdateGate.Take(now, GateRate, GateBurst)
                : peer.Gate.Take(now, GateRate, GateBurst);
            if (!gatePassed) { return; }

            if (!peer.Requests.Accept(sender, nonce, seq, isWatchUpdate, out bool connectionChanged)) { return; }
            if (connectionChanged)
            {
                for (int i = screens.Count - 1; i >= 0; i--)
                {
                    ScreenWatch w = screens[i];
                    if (ReferenceEquals(w.Client, sender))
                    {
                        SwapRemoveAt(screens, i);
                        ReturnWatch(w);
                    }
                }
            }

            var r = new WireReader(payload);
            if (r.U8() != CamWire.Proto) { return; }

            var msg = (CamMsg)r.U8();
            uint generation = r.U32();
            uint terminal = r.U32();
            if (r.Failed || terminal > ushort.MaxValue) { return; }

            if (msg == CamMsg.VoiceConsent)
            {
                bool allowed = r.Bool();
                if (!r.Failed && r.End && terminal == 0)
                {
                    VoicePeer voicePeer = voicePeers.GetValue(sender, static _ => new VoicePeer());
                    voicePeer.Consent.Set(allowed, sender.VoipQueue?.LatestBufferID ?? 0);
                    lastVoiceClient = sender;
                    lastVoicePeer = voicePeer;
                }
                return;
            }

            if (msg == CamMsg.ScreenWatch)
            {
                if (r.End && terminal != 0)
                {
                    Watch(sender, peer, (ushort)terminal, generation, now);
                }
                return;
            }

            CamDeny where = AtTerminal(sender, (ushort)terminal, now, out Item terminalItem);
            switch (msg)
            {
                case CamMsg.List:
                    if (!r.End) { return; }
                    if (where != CamDeny.None)
                    {
                        Deny(sender, generation, where);
                        return;
                    }
                    SendList(sender, generation, now);
                    break;

                case CamMsg.Control:
                    HandleControlRequest(sender, ref r, generation, where, now);
                    break;

                case CamMsg.Fetch:
                    HandleFetchRequest(sender, peer, ref r, (ushort)terminal, terminalItem, generation, where, now);
                    break;
            }
        }

        private static void HandleControlRequest(Client sender, ref WireReader r, uint generation, CamDeny where, double now)
        {
            uint id = r.U32();
            bool record = r.Bool();
            if (r.Failed || !r.End || id > ushort.MaxValue) { return; }

            Cam cam = Find((ushort)id);
            CamDeny deny = where != CamDeny.None ? where
                : (cam == null || cam.Lost) ? CamDeny.NoCamera
                : !Allowed(sender, cam, now) ? CamDeny.NoRights
                : CamDeny.None;

            if (deny != CamDeny.None)
            {
                Deny(sender, generation, deny);
                return;
            }

            cam.Recording = record;
            SendList(sender, generation, now);
        }

        private static void HandleFetchRequest(Client sender, Peer peer, ref WireReader r, ushort terminal, Item terminalItem, uint generation, CamDeny where, double now)
        {
            uint id = r.U32();
            double from = BitConverter.Int64BitsToDouble((long)r.U64());
            double to = BitConverter.Int64BitsToDouble((long)r.U64());
            if (r.Failed || !r.End || id > ushort.MaxValue || !double.IsFinite(from) || !double.IsFinite(to)) { return; }

            Cam cam = Find((ushort)id);
            CamDeny deny = where != CamDeny.None ? where
                : cam == null ? CamDeny.NoCamera
                : !Allowed(sender, cam, now) ? CamDeny.NoRights
                : (to <= from || to - from > CamWire.MaxSpan) ? CamDeny.Range
                : CamDeny.None;

            if (deny != CamDeny.None)
            {
                Deny(sender, generation, deny);
                return;
            }

            for (int j = jobs.Count - 1; j >= 0; j--)
            {
                if (ReferenceEquals(jobs[j].Client, sender)) { DropJob(j); }
            }

            Segment open = cam.Tape.Open;
            if (open != null && !open.Gap && open.End >= from && open.Start <= to && open.End - open.Start >= SealForFetch)
            {
                cam.Tape.Seal();
            }

            jobs.Add(RentJob(sender, peer, terminal, terminalItem, now, generation, cam, from, to));
        }

        private static void SendList(Client client, uint generation, double now)
        {
            CamWire.Header(out_, CamMsg.Cameras);
            out_.U32(generation);
            out_.U64((ulong)BitConverter.DoubleToInt64Bits(now));

            allowedCams.Clear();
            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            if (PrepareClientCredentials(client, now, out bool isGm))
            {
                for (int i = 0; i < camSpan.Length; i++)
                {
                    Cam c = camSpan[i];
                    if (AllowedWithPreparedCredentials(c, isGm))
                    {
                        allowedCams.Add(c);
                    }
                }
            }

            ReadOnlySpan<Cam> allowedSpan = CollectionsMarshal.AsSpan(allowedCams);
            out_.Var((ulong)allowedSpan.Length);
            for (int i = 0; i < allowedSpan.Length; i++)
            {
                Cam c = allowedSpan[i];
                RefreshStatusIfNeeded(c, now);
                new CamInfo
                {
                    Item = c.Id,
                    Epoch = c.Epoch,
                    Name = c.Name,
                    Recording = c.Recording,
                    Powered = c.CachedPowered,
                    Lost = c.Lost,
                    Control = true,
                    Oldest = c.Tape.Oldest,
                    Newest = c.Tape.Newest,
                    X = c.At.X,
                    Y = c.At.Y,
                    HalfW = c.HalfW,
                    HalfH = c.HalfH
                }.Write(out_);
            }

            ModNet.SendBytes(CamWire.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static void Deny(Client client, uint generation, CamDeny deny)
        {
            CamWire.Header(out_, CamMsg.Denied);
            out_.U32(generation);
            out_.U8((byte)deny);
            ModNet.SendBytes(CamWire.Channel, client.Connection, out_.Span, DeliveryMethod.Reliable);
        }

        private static void Pump(double now)
        {
            if (jobs.Count == 0) { return; }

            for (int j = jobs.Count - 1; j >= 0; j--)
            {
                Job job = jobs[j];
                Peer peer = job.Peer;
                if (peer == null || job.Client.Connection == null)
                {
                    DropJob(j);
                    continue;
                }

                if (now - job.Checked > JobAccessCheckInterval)
                {
                    job.Checked = now;
                    CamDeny deny = CheckJobTerminal(job, now);
                    if (deny == CamDeny.None && !Allowed(job.Client, job.Cam, now))
                    {
                        deny = CamDeny.NoRights;
                    }
                    if (deny != CamDeny.None)
                    {
                        Deny(job.Client, job.Generation, deny);
                        DropJob(j);
                        continue;
                    }
                }

                Credit(peer, now);
                while (peer.Credit > 0)
                {
                    int sent = Step(job);
                    if (sent <= 0)
                    {
                        DropJob(j);
                        break;
                    }
                    peer.Credit -= sent;
                }
            }
        }

        private static Segment NextSegment(Job job)
        {
            IReadOnlyList<Segment> list = job.Cam.Tape.Sealed;
            int count = list.Count;
            if (count == 0) { return null; }

            double sentStart = job.SentStart;
            int idx = job.NextSegmentIndex;

            if ((uint)idx >= (uint)count || list[idx].Start <= sentStart || (idx > 0 && list[idx - 1].Start > sentStart))
            {
                int lo = 0;
                int hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (list[mid].Start <= sentStart)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                idx = lo;
            }

            double from = job.From;
            double to = job.To;
            for (int i = idx; i < count; i++)
            {
                Segment s = list[i];
                if (s.Start > to) { break; }
                if (s.End >= from)
                {
                    job.NextSegmentIndex = i + 1;
                    return s;
                }
            }

            job.NextSegmentIndex = count;
            return null;
        }

        private static int Step(Job job)
        {
            if (job.Current == null)
            {
                Segment next = NextSegment(job);
                if (next != null)
                {
                    job.Current = next;
                    job.Data = job.Cam.Tape.DataOf(next).ToArray();
                    job.Part = 0;
                    foreach (int l in next.Looks)
                    {
                        if (job.Looks.Add(l))
                        {
                            looks.Retain(l);
                            job.Pending.Add(l);
                        }
                    }
                }
            }

            if (job.Pending.Count > 0)
            {
                CamWire.Chunk(out_, job.Generation, job.Cam.Id, ChunkKind.Looks);
                int countAt = out_.Length;
                out_.U32(0);
                int count = 0;

                ReadOnlySpan<int> pendingSpan = CollectionsMarshal.AsSpan(job.Pending);
                int idx = pendingSpan.Length;
                while (idx > 0 && out_.Length < CamWire.MaxChunk - 1024)
                {
                    int id = pendingSpan[--idx];
                    LookDesc d = looks.Get(id);
                    if (d == null) { continue; }
                    out_.Var((ulong)id);
                    d.Write(out_);
                    count++;
                }
                CollectionsMarshal.SetCount(job.Pending, idx);

                BinaryPrimitives.WriteUInt32LittleEndian(out_.Buffer.AsSpan(countAt), (uint)count);
                return Send(job);
            }

            if (job.Current != null)
            {
                Segment s = job.Current;
                int parts = CamWire.SegmentPart(out_, part, job.Generation, job.Cam.Id, s, job.Data, job.Part);
                job.Part++;
                if (job.Part >= parts)
                {
                    job.SentStart = s.Start;
                    job.Current = null;
                    job.Data = null;
                    job.Part = 0;
                }
                return Send(job);
            }

            if (!job.VoiceQueried)
            {
                job.VoiceQueried = true;
                job.Cam.Voice.Query(job.From, job.To, null, job.Packets, job.Owners);
            }

            if (job.VoiceAt < job.Packets.Count)
            {
                CamWire.Chunk(out_, job.Generation, job.Cam.Id, ChunkKind.Voice);
                int countAt = out_.Length;
                out_.U32(0);
                int n = 0;

                ReadOnlySpan<VoicePacket> packetSpan = CollectionsMarshal.AsSpan(job.Packets);
                ReadOnlySpan<byte[]> ownerSpan = CollectionsMarshal.AsSpan(job.Owners);

                while (job.VoiceAt < packetSpan.Length && out_.Length < CamWire.MaxChunk - 400)
                {
                    ref readonly VoicePacket p = ref packetSpan[job.VoiceAt];
                    out_.U64((ulong)BitConverter.DoubleToInt64Bits(p.Time));
                    out_.Var(p.Subject.Entity);
                    out_.Var(p.Subject.Serial);
                    out_.Bytes(job.Cam.Voice.Payload(ownerSpan[job.VoiceAt], p));
                    n++;
                    job.VoiceAt++;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(out_.Buffer.AsSpan(countAt), (uint)n);
                return Send(job);
            }

            CamWire.Done(out_, job.Generation, job.Cam.Id);
            Send(job);
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Send(Job job)
        {
            ModNet.SendBytes(CamWire.Channel, job.Client.Connection, out_.Span, DeliveryMethod.Reliable);
            return out_.Length;
        }

        private static void Report()
        {
            var b = new StringBuilder(512);
            b.Append($"{cams.Count} cameras, {Bytes / 1024} KB (looks {looks.Live}, {looks.Bytes / 1024} KB), jobs {jobs.Count}");
            ReadOnlySpan<Cam> camSpan = CollectionsMarshal.AsSpan(cams);
            for (int i = 0; i < camSpan.Length; i++)
            {
                Cam c = camSpan[i];
                b.Append($"\n#{c.Id} {c.Name}: {(Live(c) ? "rec" : "off")}{(c.Lost ? " lost" : "")} " +
                         $"tape {c.Tape.Bytes / 1024}/{c.Tape.Budget / 1024} KB segs {c.Tape.Segments} thinned {c.Tape.ThinnedCount} evicted {c.Tape.Evicted}, " +
                         $"scene {c.Scene?.Bytes / 1024 ?? 0}/{c.Scene?.Budget / 1024 ?? 0} KB frames {c.Scene?.Frames ?? 0}, " +
                         $"voice {c.Voice.Packets} pk {c.Voice.Bytes / 1024} KB; " +
                         $"scene_ready={c.LiveSceneReady} objects={c.LiveScene.Count} failure={c.SceneFailure} scene_disabled={c.SceneCaptureDisabled}");
            }
            ModLog.Message(b.ToString());
        }
    }
}
