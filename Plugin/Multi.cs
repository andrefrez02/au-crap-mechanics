using System;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.24.0-mp — multiplayer. The game is NOT lockstep: the logic runs on every machine and the server corrects what
    /// has to match. What the server rules: the pose and velocity of each part (NetcoreSpaceshipPartPosVelSyncSystem writes
    /// LocalTransform and PhysicsVelocity of every entity with SpaceshipPartNetcorePosSynchronizerOnClient) and the creation/destruction of
    /// parts and blocks (CreateSpaceshipPart / CreateAndAwakeSC / AssignSCToNewSP / DestroySC events). Port values, power, fuel and the
    /// actionables (knob, lever, button, switch) also go through events. Among the ship systems, only SpaceshipPostCheckConsistency
    /// (the one that splits parts) is netcore-aware — decoupler, strain, circuits, ports and knobs run the same for everyone.
    ///
    /// 0.28.0 — the AUTHORITY RULE is per MACHINE: the server (or a solo game) runs the mod's physics; a client never does. 0.24.0-mp
    /// decided per part from SpaceshipPartNetcorePosSynchronizerOnClient._use, but the game's sync job takes that component read-write and
    /// applies the pose it carries: _use is "a server update is waiting", not "the server owns this part" — most frames it read false
    /// and a client would have driven (and, at launch, split) the server's parts. Also, before the joint existed (state 0) partA/partB were
    /// null and counted as "mine", so a client broke the game's joints of every piston/bearing on its own copy of the ship.
    /// On a client the mod now only draws (piston rods, turret seat) from the poses the server sends, and sends the turret aim of the
    /// player seated there to the server (ModNet.cs). In a solo game nothing of this applies.
    /// 0.28.2 — a solo game is ServerOpen too (the game always hosts, so friends can join): "on a network" is being a client, or hosting
    /// with at least one other player in the game (a NetcoreClient that isn't this machine). 0.28.0/0.28.1 took ServerOpen alone as a
    /// network game, so a solo game got the network rules (recoupler without merging, beacons not exempt, replay without freezing).</summary>
    internal static class Multi
    {
        public enum Status { Off = 0, On = 1, ServerOpen = 2, ServerClosed = 3, Client = 4 }

        /// <summary>[Multiplayer] from the .cfg (CrapMechanicsPlugin.Apply).</summary>
        public static bool OwnerOnly = true;        // host_only: physics/joints only on the server
        public static bool FakeClient = false; // fake_client (debug): pretends this machine is a client, with no network at all
        public static bool BreakOnClient = false;  // break_on_client (debug): a client also breaks the game's joints at launch
        public static bool RingOnClient = true;      // ring_on_client: the Slip Ring bridge (data/power) runs on the client too

        static TypeIndex tiNet; static EntityQuery qNet; static bool ok, failed;
        static Status latest = (Status)(-1); static float tLog = 9f; static int ourParts, theirParts;
        static int frame = -1; static Status cached; static bool cachedServer;
        static TypeIndex tiClient; static EntityQuery qClients; static bool cOk, cFailed; static ulong me; static int meTries; static int others, latestOthers = -1;   // 0.28.2

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiNet = TypeManager.GetTypeIndex(Il2CppType.Of<Netcore.Singleton>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1);
                a[0] = ComponentType.ReadOnly(tiNet); qNet = em.CreateEntityQuery(a);
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Warn("multiplayer: Netcore.Singleton not available (single player only): " + ex.Message); return false; }
        }

        /// <summary>The network state now (read once per frame). Off = solo game.</summary>
        public static Status Now(EntityManager em)
        {
            if (FakeClient) return Status.Client;
            int f = UnityEngine.Time.frameCount;
            if (f != frame) { frame = f; Read(em, out cached, out cachedServer); others = cached == Status.ServerOpen || cached == Status.ServerClosed ? CountOthers(em) : 0; }
            return cached;
        }
        /// <summary>0.28.2: the other players in this game (NetcoreClient entities that aren't this machine).</summary>
        static unsafe int CountOthers(EntityManager em)
        {
            if (!cOk)
            {
                if (cFailed) return 1;
                try
                {
                    tiClient = TypeManager.GetTypeIndex(Il2CppType.Of<NetcoreClient>());
                    var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1);
                    a[0] = ComponentType.ReadOnly(tiClient); qClients = em.CreateEntityQuery(a); cOk = true;
                }
                catch (Exception ex) { cFailed = true; ModLog.Warn("multiplayer: NetcoreClient not available (" + ex.Message + ") — hosting counts as a network game"); return 1; }
            }
            if (me == 0 && meTries < 100) { meTries++; try { me = Steamworks.SteamUser.GetSteamID().m_SteamID; } catch (Exception) { } }
            int n = 0;
            try
            {
                foreach (var e in Ecs.QueryList(qClients))
                {
                    var c = (byte*)em.GetComponentDataRawRO(e, tiClient); if (c == null) continue;
                    ulong id = *(ulong*)(c + Layout.ClientSteam);   // NetcoreClient._steamID
                    if (id != 0 && id != me) n++;
                }
            }
            catch (Exception) { }
            return n;
        }
        static unsafe void Read(EntityManager em, out Status s, out bool server)
        {
            s = Status.Off; server = false;
            if (!Prepare(em)) return;
            try
            {
                foreach (var e in Ecs.QueryList(qNet))
                {
                    var p = (byte*)em.GetComponentDataRawRO(e, tiNet); if (p == null) continue;
                    server = p[Layout.NetServer] != 0;   // Netcore.Singleton._isServer
                    s = (Status)(*(int*)(p + Layout.NetStatus));   // _networkStatus
                    return;
                }
            }
            catch (Exception) { }
        }
        public static bool Networked(EntityManager em) { var s = Now(em); return s == Status.Client || ((s == Status.ServerOpen || s == Status.ServerClosed) && others > 0); }
        public static bool IAmClient(EntityManager em) => Now(em) == Status.Client;
        /// <summary>Does this machine run the mod's physics (joints, motors, splitting, merging, knob restore)? The server and a solo game do;
        /// a client doesn't (with host_only off, everyone does — the pre-0.24 behaviour).</summary>
        public static bool Authoritative(EntityManager em) => !OwnerOnly || !IAmClient(em);

        /// <summary>Can I apply physics to this part? Per machine since 0.28.0 (see the summary); kept per part for the call sites.</summary>
        public static bool MyPart(EntityManager em, Entity part) => Authoritative(em);
        /// <summary>Are both parts of a joint mine? (the joint links the two, so both have to be)</summary>
        public static bool MyJoint(EntityManager em, Entity partA, Entity partB) => Authoritative(em);

        /// <summary>Log of the network state (when it changes) and of how many joints this machine drives (every 5 s on a network).</summary>
        public static void Step(EntityManager em, float dt)
        {
            var s = Now(em);
            bool hosting = s == Status.ServerOpen || s == Status.ServerClosed;
            if (s != latest || (hosting && (others > 0) != (latestOthers > 0)))
            {
                latest = s; latestOthers = others;
                ModLog.Info("multiplayer: network is " + s + (s == Status.Client || s == Status.Off || s == Status.On ? "" : cachedServer ? " (this machine is the server)" : "")
                    + (hosting ? (others > 0 ? ", " + others + " other player(s) in the game — network rules on" : ", nobody else in the game — solo rules") : "")
                    + (FakeClient ? " (FAKE CLIENT — [Multiplayer] fake_client is on)" : "")
                    + "; the mod " + (Authoritative(em) ? "drives the physics of the mod's parts" : "only draws them: the server drives their physics"));
            }
            // Step runs once per frame, after the joints loop: what's in the counters is the picture of THIS frame
            int oursSet = ourParts, theirs = theirParts; ourParts = 0; theirParts = 0;
            if (!Networked(em)) return;
            tLog += dt; if (tLog < 5f) return; tLog = 0f;
            if (oursSet + theirs > 0) ModLog.Info("multiplayer: " + oursSet + " joint(s) driven here, " + theirs + " drawn from the server's poses");
        }
        /// <summary>Called once per joint, per frame (Step resets at the end of the frame).</summary>
        public static void Tally(bool ours) { if (ours) ourParts++; else theirParts++; }
        public static void Forget() { latest = (Status)(-1); latestOthers = -1; ourParts = 0; theirParts = 0; frame = -1; }
    }
}
