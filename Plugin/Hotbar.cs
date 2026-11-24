using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CrapMechanics
{
    /// <summary>Remote control, delivery 2: the in-flight hotbar (10 slots) and the phone in the hand.
    /// The game has a hotbar only in the garage (UIHandComponentsList, the part ghost / weld / paint tool in GarageGrabber._handPrefab); in flight
    /// it hides it (UIManager.OnSpaceshipLaunch -> IncrementalSetActive(false)) and has no held item at all, so in flight its keys 1..0 and
    /// "empty hand" (InventoryEmptyHand, default Backquote = the key left of 1, "'" on a Brazilian keyboard) do nothing
    /// (research remoto_e2_pesquisa.md, section 1). The mod keeps its own state and MIRRORS the vanilla hotbar with the SAME bound keys
    /// (read from OptionsData, so a remap is followed): empty-hand key = empty hand (the item stays in its slot), Hotbar1..9/0 = slot 1..10,
    /// Ctrl + digit = nothing (vanilla: changes the row), empty hand wins over a digit in the same frame, same sound. It never consumes a key and
    /// never writes the game's input; in the garage it reads none of these keys (the vanilla hotbar rules there).
    /// Active only in flight, on the host / solo, standing (not seated), alive, holding nothing (ActionablesController._assignedEntity Null) and with
    /// no game menu open. Items are typed (Kind) so more kinds can come later; for now only the dock's phone.
    /// The phone in the hand is drawn in 3D: the CRP draws only ECS entities, so the mod makes ONE render entity with the dock phone's mesh and
    /// material (a copy of its CRP components, no transform hierarchy, no physics, no actionable) and writes its LocalToWorld every frame in the
    /// PresentationSystemGroup prefix, in front of the camera. Fallback: a phone drawn by the mod's UI in the lower right ([Remote Computer] hand_3d).
    /// Never saved: a session end, a world unload, the return to the garage or a time rewind to before the take puts every phone back on its dock.
    /// Round 0.31.35 (Andre: "same look as the game's own garage hotbar"): the bar is a CLONE of the game's own hotbar object
    /// (UIManager._handComponentsList, which still exists in flight, only inactive). It is instantiated under an inactive holder, so no Awake /
    /// OnEnable of the game's scripts ever runs on the copy (the real Awake would set the garage hand and load a sound; OnDestroy would release
    /// them); the game's scripts and the buttons are destroyed on the copy before it is ever activated; then the copy sits next to the original
    /// (same parent, anchors and canvas = same place, frames, numbers, fonts and colours) and the mod only sets its icons, selection frames
    /// and the name text. The original is never touched. Shown only while something is carried, as before. If anything fails (or
    /// [Remote Computer] hotbar_game_look = false) the mod's own drawn bar is used. The first copy logs its UI tree ("hotbar ui: ...").
    /// The phone in the hand now rests lower ([Remote Computer] hand_low), about a third of it below the bottom edge, so the missing hand
    /// is not noticed; the focus position is unchanged.
    /// Round 0.31.36 (Andre's screenshots: the copy's slots were opaque light grey with a white outline, the game's are dark and
    /// translucent): the slot's dark look is not in the Image (its colour is (0.5, 0.5, 0.5, 1)) — the game's Button on each slot tints it
    /// at runtime through the CanvasRenderer (Selectable colour tint: normalColor with an item, disabledColor on an empty slot, which the
    /// game sets with interactable = false), and the copy's buttons were destroyed before they ever ran. Now (1) each button's colours are
    /// read before it is destroyed; (2) while the game's own bar is on screen in the garage it is SAMPLED: per graphic, by its place in
    /// the tree, the CanvasRenderer colour, the graphic's colour, enabled / active, and the bar's rectangle; (3) the copy gets the sample
    /// (the buttons' colours when there is no sample yet in this session) every time it is shown or a slot changes. The mod's own parts
    /// (icons, selection frames, amount texts, name, row number) are never overwritten. The game's UI blur switch
    /// (CRPUIBlurActivator: only a use counter) stays on the copy when its images use a material of their own, as on the game's bar.</summary>
    internal static class Hotbar
    {
        public const int Slots = 10;
        public static bool Hand3D = true;
        /// <summary>[Remote Computer] hotbar_game_look: the bar is a copy of the game's own hotbar object (false = the mod's own drawn bar).</summary>
        public static bool GameLook = true;
        /// <summary>[Remote Computer] hand_low: screen height of the resting phone's centre (-1 = the bottom edge, 0 = the middle).</summary>
        public static float HandLow = -0.88f;
        internal enum Kind { Phone }
        internal sealed class Item { public Kind kind; public Entity dock; public string guid; public int takenTick = -1; public float tRebind; }
        static readonly Item[] slots = new Item[Slots];
        static int selected = -1;
        static bool wasFlight, host, blocked, busy, barVisible, handWanted, handVisible, focus, draw2D;
        static int lastFrame = -1; static float showUntil, tKeys = 9f;
        const uint MenuMask = 0x3FCF;   // the game's own menus/overlays (Phone.MenuMask); bit 20 = the mod's phone screen

        // ------------------------------------------------------------------ game types
        static TypeIndex tiOverlay, tiCtrl, tiOptions, tiPCD, tiAttached, tiLayer, tiCam, tiAWW, tiHeader;
        static EntityQuery qOverlay, qCtrl, qOptions, qMine; static bool ok, failed, attachedOk, camOk, headerOk;
        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiOverlay = TypeManager.GetTypeIndex(Il2CppType.Of<OverlaySingleton>());
                tiCtrl = TypeManager.GetTypeIndex(Il2CppType.Of<ActionablesController>());
                tiOptions = TypeManager.GetTypeIndex(Il2CppType.Of<OptionsData>());
                tiPCD = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>());
                tiLayer = TypeManager.GetTypeIndex(Il2CppType.Of<CRPLayerID>());
                tiAWW = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>());
                qOverlay = Q1(em, tiOverlay); qCtrl = Q1(em, tiCtrl); qOptions = Q1(em, tiOptions);
                var m = new Il2CppStructArray<ComponentType>(2); m[0] = ComponentType.ReadOnly(TypeManager.GetTypeIndex(Il2CppType.Of<MyPlayerController>())); m[1] = ComponentType.ReadOnly(tiPCD);
                qMine = em.CreateEntityQuery(m);
                try { tiAttached = TypeManager.GetTypeIndex(Il2CppType.Of<DynamicObjectAttachedTo>()); attachedOk = true; } catch (Exception ex) { ModLog.Warn("hotbar: DynamicObjectAttachedTo not found (" + ex.Message + ") — seated is read from the turret seat only"); }
                try { tiCam = TypeManager.GetTypeIndex(Il2CppType.Of<CRPCameraData>()); camOk = true; } catch (Exception) { }
                try { tiHeader = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRenderMemoryHeader>()); headerOk = true; } catch (Exception) { }
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Error("hotbar: game types not found, the hotbar is off: " + ex.Message); return false; }
        }
        static EntityQuery Q1(EntityManager em, TypeIndex ti) { var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(ti); return em.CreateEntityQuery(a); }
        static Entity First(EntityQuery q) { foreach (var e in Ecs.QueryList(q)) return e; return Entity.Null; }
        static unsafe byte* RO(EntityManager em, EntityQuery q, TypeIndex ti) { var e = First(q); return Ecs.HasC(em, e, ti) ? (byte*)Ecs.RO(em, e, ti) : null; }
        static bool InFlight() { try { return Ecs.Ok && Ecs.QSing.CalculateEntityCountWithoutFiltering() > 0; } catch (Exception) { return false; } }
        static unsafe uint Overlay(EntityManager em) { var p = RO(em, qOverlay, tiOverlay); return p == null ? 0u : *(uint*)p; }
        /// <summary>ActionablesController._assignedEntity (+0): a package, lever, whiteboard... held by the player.</summary>
        static unsafe Entity Assigned(EntityManager em) { var p = RO(em, qCtrl, tiCtrl); return p == null ? Entity.Null : *(Entity*)p; }
        /// <summary>The local player sits on a seat: DynamicObjectAttachedTo._attachedTo (+0) of the MyPlayerController entity (what the game's
        /// SeatUpdateSystem reads; TurretSeat.CurrentSeat does the same).</summary>
        public static bool SeatedNow(EntityManager em) { try { return Prepare(em) && Seated(em); } catch (Exception) { return false; } }
        static unsafe bool Seated(EntityManager em)
        {
            if (JointSystem.SeatedTurret != null) return true;
            if (!attachedOk) return false;
            try { var p = First(qMine); return Ecs.HasC(em, p, tiAttached) && !Ecs.IsNull(*(Entity*)Ecs.RO(em, p, tiAttached)); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ the game's bound keys (OptionsData._controls, +148)
        // CRPKey uses the Input System's Key numbers for the keyboard; mouse buttons 118 left, 119 right, 120 middle, 121/122 side.
        static int keyEmpty = (int)Key.Backquote, keyCtrl = (int)Key.LeftCtrl; static readonly int[] keyHot = new int[Slots]; static bool swapLR, keysRead;
        static unsafe void ReadBindings(EntityManager em)
        {
            try
            {
                var p = RO(em, qOptions, tiOptions); if (p == null) return;
                // layout 21/11 (ga_types_layout.txt OptionsData / OptionsData+Controls): _keyInventoryEmptyHand +148+48, _keyControl +148+60,
                // _keyHotbar0..9 +148+132+4i, _swapLMBRMB +148+180
                int e = *(int*)(p + 196), c = *(int*)(p + 208); bool sw = *(p + 328) != 0;
                var h = new int[Slots]; for (int i = 0; i < Slots; i++) h[i] = *(int*)(p + 280 + 4 * i);
                bool changed = !keysRead || e != keyEmpty || c != keyCtrl || sw != swapLR; for (int i = 0; i < Slots; i++) changed |= h[i] != keyHot[i];
                keyEmpty = e; keyCtrl = c; swapLR = sw; for (int i = 0; i < Slots; i++) keyHot[i] = h[i];
                if (changed) ModLog.Info("hotbar: game keys — empty hand " + KeyName(e) + ", slots 1..0 " + KeyName(h[1]) + ".." + KeyName(h[0]) + ", Ctrl " + KeyName(c) + (sw ? ", mouse buttons swapped" : ""));
                keysRead = true;
            }
            catch (Exception ex) { if (!keysWarned) { keysWarned = true; ModLog.Warn("hotbar: game keys not read (" + ex.Message + ") — using the defaults (Backquote, 1..0)"); } }
        }
        static bool keysWarned;
        static string KeyName(int k) => k >= 118 && k <= 122 ? "Mouse" + (k - 118) : k > 0 && Enum.IsDefined(typeof(Key), k) ? ((Key)k).ToString() : k == 0 ? "none" : "#" + k;
        static bool Mouse(int k, bool down)
        {
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current; if (m == null) return false;
                var b = k == 118 ? m.leftButton : k == 119 ? m.rightButton : k == 120 ? m.middleButton : k == 121 ? m.backButton : m.forwardButton;
                return down ? b.wasPressedThisFrame : b.isPressed;
            }
            catch (Exception) { return false; }
        }
        static bool KeyDown(int k) { if (k <= 0) return false; if (k >= 118 && k <= 122) return Mouse(k, true); return Enum.IsDefined(typeof(Key), k) && InputKeys.Pressed((Key)k); }
        static bool KeyHeld(int k) { if (k <= 0) return false; if (k >= 118 && k <= 122) return Mouse(k, false); return Enum.IsDefined(typeof(Key), k) && InputKeys.Holding((Key)k); }
        /// <summary>The game's right mouse button (OptionsData _swapLMBRMB ? Mouse0 : Mouse1) went down this frame: opens the focus. Standing with
        /// nothing held it does nothing in vanilla flight (it rotates a held package, erases an assigned whiteboard, looks around when seated).</summary>
        static bool FocusOpenPressed() => Mouse(swapLR ? 118 : 119, true);
        /// <summary>Closes the focused phone: the physical right button (the left one clicks the phone's buttons; the game's input is off meanwhile).</summary>
        public static bool FocusClickPressed() => Mouse(119, true);

        // ------------------------------------------------------------------ state
        static int Count { get { int n = 0; foreach (var s in slots) if (s != null) n++; return n; } }
        static int FreeSlot() { for (int i = 0; i < Slots; i++) if (slots[i] == null) return i; return -1; }
        static bool SelectedIsPhone => selected >= 0 && selected < Slots && slots[selected] != null && slots[selected].kind == Kind.Phone;
        /// <summary>A long E on a dock may take its phone now (flight, host, standing, a free slot).</summary>
        public static bool CanTake => wasFlight && host && !blocked && FreeSlot() >= 0;
        static bool Same(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;
        /// <summary>This item is the phone of `dock`: the same entity, or the same block (SCGuid) whose old entity is gone (rebuilt by a rewind).</summary>
        static bool Owns(EntityManager em, Item it, Entity dock, string guid)
        {
            if (Same(it.dock, dock)) return true;
            if (guid.Length == 0 || it.guid != guid) return false;
            try { return !Ecs.Alive(em, it.dock); } catch (Exception) { return false; }
        }

        /// <summary>Main thread, every frame the mod cycle runs (after Phone.Frame).</summary>
        public static void Frame(EntityManager em, float dt)
        {
            if (!Prepare(em)) return;
            lastFrame = Time.frameCount;
            tKeys += dt; if (tKeys >= 2f) { tKeys = 0f; ReadBindings(em); }
            bool flight = InFlight();
            if (flight != wasFlight)
            {
                wasFlight = flight;
                if (Count > 0) ReturnAll(em, flight ? "a new flight started" : "back in the garage");
                selected = -1; DestroyHand(em, flight ? "a new flight started" : "back in the garage");
            }
            if (!flight)
            {
                Idle();
                // 0.31.36: the look of the game's own bar is read here, in the garage (five errors = it is left alone for the session)
                if (sampleWarn < 5) { try { SampleGameBar(em); } catch (Exception ex) { if (sampleWarn++ < 2) ModLog.Info("hotbar look: the game's bar could not be sampled (" + ex.Message + ") — the copy uses the buttons' colours"); } }
                return;
            }
            host = Multi.Authoritative(em);
            if (!host)
            {
                // multiplayer is not tested: on a client the hotbar and the hand stay off (nothing of the host changes)
                if (Count > 0) ReturnAll(em, "this machine is a client"); selected = -1; DestroyHand(em, "this machine is a client"); Idle(); return;
            }
            CheckItems(em);
            bool seated = Seated(em), dead = Phone.Dead(em), menu = (Overlay(em) & MenuMask) != 0;
            busy = !Ecs.IsNull(Assigned(em));
            blocked = seated || dead || menu;
            string why = seated ? "seated" : dead ? "the player died" : menu ? "a game menu opened" : busy ? "the hands are busy (holding something)" : !SelectedIsPhone ? "the hand was emptied" : null;
            if (why != null && Phone.HandMode) Phone.CloseHand(em, why);
            // review (Andre: "'" must empty the hand like vanilla): the empty-hand key also works while the phone in the hand is focused — the
            // focus closes and the hand is empty (the phone stays in its slot); the phone app itself never uses that key
            bool emptied = false;
            // (0.31.35: not while a name is being typed — that key types a character then)
            if (Phone.HandMode && !Phone.Typing && KeyDown(keyEmpty)) { Phone.CloseHand(em, "empty-hand key " + KeyName(keyEmpty)); Select(-1, true); emptied = true; }
            // with nothing carried the keys stay exactly as vanilla flight has them: they do nothing (no sound, no selection)
            if (!emptied && !blocked && !busy && !Phone.IsOpen && Count > 0)
            {
                Keys(em);
                if (SelectedIsPhone && Phone.ClosedFrame != Time.frameCount && FocusOpenPressed()) Phone.OpenHand(em);
            }
            focus = Phone.HandMode;
            handWanted = SelectedIsPhone;
            handVisible = handWanted && !blocked && !busy && !(Phone.IsOpen && !Phone.HandMode);
            barVisible = Count > 0 && !blocked;
            if (handWanted && Hand3D && !handFailed) EnsureHand(em);
            if (!Hand3D && !Ecs.IsNull(hand)) DestroyHand(em, "[Remote Computer] hand_3d is off");
            CheckHandDrawn(em);
            draw2D = handVisible && !focus && (!Hand3D || handFailed);
            // (an error here is most likely the copy destroyed together with the game's UI: it is made again; three errors = the drawn bar)
            try { UpdateGameBar(); }
            catch (Exception ex)
            {
                if (++gbErrors >= 3) GameBarFailed("update: " + ex.Message);
                else
                {
                    // review (0.31.36): the copy is destroyed, not only forgotten — if it is still there it would stay on screen, and it may
                    // hold one use of the game's UI blur counter (CRPUIBlurActivator), given back only when the object is disabled or destroyed
                    try { if (gbRoot != null) UnityEngine.Object.Destroy(gbRoot); } catch (Exception) { }
                    gbRoot = null; gbOn = false; tGb = 0f;
                }
            }
        }
        static void Idle() { barVisible = false; handWanted = false; handVisible = false; focus = false; draw2D = false; blocked = true; HideGameBar(); }

        /// <summary>Mirrors UIHandComponentsList.Update (research section 1): empty hand first (the vanilla `else`), then Hotbar(k % 10) for k = 1..10;
        /// with the Ctrl key held a digit does nothing here (in the garage it changes the row).</summary>
        static void Keys(EntityManager em)
        {
            if (KeyDown(keyEmpty)) { Select(-1, true); return; }
            if (KeyHeld(keyCtrl)) return;
            for (int k = 1; k <= Slots; k++) if (KeyDown(keyHot[k % 10])) { Select(k - 1, true); return; }
        }
        static void Select(int i, bool byKey)
        {
            showUntil = Time.unscaledTime + 2.5f;
            if (byKey) Scroll();
            if (i == selected) return;
            selected = i;
            if (i < 0) ModLog.Info("hotbar: empty hand (" + KeyName(keyEmpty) + ") — the phone stays in its slot");
            else ModLog.Info("hotbar: slot " + ((i + 1) % 10) + " selected (" + (slots[i] == null ? "empty: empty hand" : "phone in the hand: right click to use it") + ")");
        }
        static void Scroll()
        {
            // the vanilla hotbar's own sound (UIHandComponentsList._soundInventoryScroll, volume 0.1); its GameObject is inactive in flight, the sound is loaded
            try { var ui = UIManager._singleton; var hl = ui != null ? ui._handComponentsList : null; if (hl != null) { UIManager.PlaySound(hl._soundInventoryScroll, 0.1f, 1f); return; } } catch (Exception) { }
            try { UIManager.PlaySoundUIClick(); } catch (Exception) { }
        }
        static void Sound(int which)
        {
            try { if (which == 1) UIManager.PlaySoundUIClickAction(); else if (which == 2) UIManager.PlaySoundUIClickInvalid(); else UIManager.PlaySoundUIClick(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ take / put back / auto-return
        /// <summary>Long E on a dock that shows its phone: the phone goes to the first free slot (and into the hand if the hand was empty).</summary>
        public static bool TakeFrom(EntityManager em, Entity dock)
        {
            int free = FreeSlot();
            if (!CanTake || free < 0) { Sound(2); if (!fullNoted) { fullNoted = true; ModLog.Info("hotbar: the phone cannot be taken now (" + (!host ? "client" : free < 0 ? "the hotbar is full" : "seated / menu open") + ")"); } return false; }
            string guid = ""; try { guid = Ecs.Guid(em, dock); } catch (Exception) { }
            // review: one phone per dock, never two — a slot that already carries this dock's phone (same entity, or same block rebuilt as a new
            // entity by a rewind and not re-bound yet: CheckItems re-binds after 1 s) refuses the take and the dock is hidden again
            for (int i = 0; i < Slots; i++)
            {
                var it = slots[i]; if (it == null || it.kind != Kind.Phone || !Owns(em, it, dock, guid)) continue;
                RemoteComputer.TakeFromDock(em, dock); it.dock = dock; it.tRebind = 0f;
                ModLog.Info("hotbar: dock " + dock.Index + "'s phone is already in slot " + ((i + 1) % 10) + " — not taken twice (the dock shows it hidden again)");
                return false;
            }
            if (!RemoteComputer.TakeFromDock(em, dock)) return false;
            slots[free] = new Item { kind = Kind.Phone, dock = dock, guid = guid, takenTick = JointSystem.CurrentTick };
            bool inHand = selected < 0 || slots[selected] == null;
            if (inHand) selected = free;
            showUntil = Time.unscaledTime + 2.5f;
            Sound(1);
            ModLog.Info("hotbar: phone TAKEN from dock " + dock.Index + " -> slot " + ((free + 1) % 10) + (inHand ? " (in the hand)" : " (the hand keeps slot " + ((selected + 1) % 10) + ")") + (JointSystem.CurrentTick >= 0 ? ", tick " + JointSystem.CurrentTick : ""));
            return true;
        }
        static bool fullNoted;
        /// <summary>Short E on an empty dock: its own phone goes back (an empty dock always has its phone in the hotbar: one phone per dock,
        /// never two). If the hotbar has no phone of this dock (should not happen) the dock just shows its phone again.</summary>
        public static void PutBack(EntityManager em, Entity dock)
        {
            string guid = ""; try { guid = Ecs.Guid(em, dock); } catch (Exception) { }
            // review: the exact dock entity first; the SCGuid only for an item whose dock entity is gone (a block pasted twice may share a guid:
            // matching it on a live other dock would return the wrong phone and leave that dock empty with nothing carried)
            int pick = -1;
            for (int i = 0; i < Slots && pick < 0; i++) { var it = slots[i]; if (it != null && it.kind == Kind.Phone && Same(it.dock, dock)) pick = i; }
            for (int i = 0; i < Slots && pick < 0; i++) { var it = slots[i]; if (it != null && it.kind == Kind.Phone && Owns(em, it, dock, guid)) pick = i; }
            if (pick >= 0)
            {
                RemoteComputer.ReturnToDock(em, dock); slots[pick] = null; showUntil = Time.unscaledTime + 2.5f;
                Sound(0);
                ModLog.Info("hotbar: phone PUT BACK on dock " + dock.Index + " (from slot " + ((pick + 1) % 10) + ")");
                return;
            }
            RemoteComputer.ReturnToDock(em, dock);
            ModLog.Warn("hotbar: dock " + dock.Index + " was empty but its phone was not in the hotbar — the dock shows it again");
        }
        static void ReturnAll(EntityManager em, string why)
        {
            for (int i = 0; i < Slots; i++)
            {
                var it = slots[i]; if (it == null) continue;
                try { if (Ecs.Alive(em, it.dock)) RemoteComputer.ReturnToDock(em, it.dock); } catch (Exception) { }
                ModLog.Info("hotbar: phone AUTO-RETURNED to dock " + it.dock.Index + " (" + why + ")");
                slots[i] = null;
            }
            try { RemoteComputer.ShowAllDocks(em); } catch (Exception) { }
            if (Phone.HandMode) Phone.CloseHand(em, why);
        }
        /// <summary>Every frame: a time rewind to before an item's take puts that phone back on its dock (the tick went below the take's tick); a dock
        /// rebuilt as a new entity (same SCGuid) gets its phone hidden again while the phone is still in the hotbar (never two phones).</summary>
        static void CheckItems(EntityManager em)
        {
            int tick = JointSystem.CurrentTick;
            for (int i = 0; i < Slots; i++)
            {
                var it = slots[i]; if (it == null) continue;
                if (tick >= 0 && it.takenTick >= 0 && tick < it.takenTick - 2)
                {
                    try { if (Ecs.Alive(em, it.dock)) RemoteComputer.ReturnToDock(em, it.dock); } catch (Exception) { }
                    ModLog.Info("hotbar: phone AUTO-RETURNED to dock " + it.dock.Index + " (time rewind to tick " + tick + ", before the take at tick " + it.takenTick + ")");
                    slots[i] = null; continue;
                }
                if (Ecs.Alive(em, it.dock) && RemoteComputer.DockTracked(it.dock)) { if (RemoteComputer.DockHasPhone(it.dock)) RemoteComputer.TakeFromDock(em, it.dock); continue; }
                it.tRebind += Time.unscaledDeltaTime; if (it.tRebind < 1f) continue; it.tRebind = 0f;
                var nd = RemoteComputer.FlightDockByGuid(em, it.guid);
                if (Ecs.IsNull(nd) || Same(nd, it.dock)) continue;
                bool owned = false; for (int j = 0; j < Slots; j++) if (j != i && slots[j] != null && Same(slots[j].dock, nd)) owned = true;
                if (owned) continue;   // review: that dock's phone is another slot's (a block pasted twice shares the guid): never two items for one dock
                RemoteComputer.TakeFromDock(em, nd);
                ModLog.Info("hotbar: dock " + it.dock.Index + " is now entity " + nd.Index + " (same block) — its phone stays in the hotbar, hidden on the dock");
                it.dock = nd;
            }
        }
        /// <summary>World unloaded (JointSystem.ForgetAll): nothing carried survives; every dock that is still there shows its phone.</summary>
        public static void Forget()
        {
            int n = Count;
            try
            {
                var w = World.DefaultGameObjectInjectionWorld;
                if (w != null && w.IsCreated && !Unloading.Window)
                {
                    var em = w.EntityManager; for (int i = 0; i < Slots; i++) if (slots[i] != null && Ecs.Alive(em, slots[i].dock)) RemoteComputer.ReturnToDock(em, slots[i].dock); RemoteComputer.ShowAllDocks(em);
                    DestroyHand(em, "mod state cleared");   // review: never leave the mod's render entity behind (it would stay drawn where it was)
                }
            }
            catch (Exception) { }
            for (int i = 0; i < Slots; i++) slots[i] = null;
            if (n > 0) ModLog.Info("hotbar: " + n + " phone(s) AUTO-RETURNED to their dock (world unloaded / back in the garage)");
            selected = -1; wasFlight = false; hand = Entity.Null; handWorld = null; Idle();
        }

        // ------------------------------------------------------------------ 0.31.35: the bar with the game's own look (a copy of UIHandComponentsList)
        static EPC_SpaceshipComponent phoneEpc;
        /// <summary>Plugin load: the Phone Dock's registered component (its inventory icon is the phone's icon in the bar).</summary>
        public static void Connect(IReadOnlyList<EPC_SpaceshipComponent> regs)
        {
            try { if (regs != null) foreach (var e in regs) if (e != null && e.name == "SC_PhoneDock") phoneEpc = e; } catch (Exception) { }
        }
        static GameObject gbRoot; static IntPtr gbSrc; static bool gbFailed, gbOn; static float tGb = 9f, tGbCheck, gbNameAt = -99f, gbNameAlpha = -1f; static int gbState = int.MinValue;
        static readonly RawImage[] gbIcon = new RawImage[Slots]; static readonly Image[] gbSel = new Image[Slots]; static readonly TMP_Text[] gbCount = new TMP_Text[Slots];
        static TMP_Text gbName, gbRowId; static Texture gbTex; static int gbErrors;

        // ------------------------------------------------------------------ 0.31.36: the look of the game's own bar (sample + buttons)
        sealed class GLook { public Color cr, gc; public bool en, act; }
        /// <summary>One graphic of the copy: its slot (-1 = not in a slot), its place under the slot (or under the bar) as child numbers
        /// ("/1/0"), whether it is the slot's background (the button's target) and whether the mod itself drives it.</summary>
        sealed class CloneG { public Graphic g; public CanvasRenderer cr; public GameObject go; public int slot; public string rel; public bool bg, driven; }
        static readonly List<CloneG> gbGraphics = new List<CloneG>();
        static readonly Color[] btnNormal = new Color[Slots], btnDisabled = new Color[Slots]; static readonly bool[] btnTint = new bool[Slots];
        static RectTransform gbRect;
        static Dictionary<string, GLook> lookFilled, lookEmpty, lookRoot; static bool lookOk, lookStable, lookHasFilled, lookHasEmpty, lookRect, crBroken, gbDumped;
        static Color lookBgFilled, lookBgEmpty; static Vector2 lookAMin, lookAMax, lookPivot, lookPos, lookSize; static Vector3 lookScale;
        static int lookVersion, lookApplied = -1, lookCount, lookLogs, appliedLogs, sampleWarn; static float tSample = 9f; static string lookSaid = "";
        static readonly Dictionary<IntPtr, int> slotOfTmp = new Dictionary<IntPtr, int>();
        static bool Close(Color a, Color b) => Math.Abs(a.r - b.r) < 0.004f && Math.Abs(a.g - b.g) < 0.004f && Math.Abs(a.b - b.b) < 0.004f && Math.Abs(a.a - b.a) < 0.004f;
        static Color Mul(Color c, float m) => new Color(c.r * m, c.g * m, c.b * m, c.a * m);   // Selectable: tint * colorMultiplier (alpha too)
        /// <summary>Where a graphic sits: under slot i ("/child/child" from the slot's root, "" = the root itself) or under the bar.</summary>
        static bool Locate(Transform t, IntPtr root, Dictionary<IntPtr, int> slotOf, out int slot, out string rel)
        {
            slot = -1; rel = null; var sb = new System.Text.StringBuilder();
            for (int guard = 0; guard < 12 && t != null; guard++)
            {
                var p = t.Pointer;
                if (slotOf.TryGetValue(p, out int i)) { slot = i; rel = sb.ToString(); return true; }
                if (p == root) { rel = sb.ToString(); return true; }
                sb.Insert(0, t.GetSiblingIndex()).Insert(0, '/');
                t = t.parent;
            }
            return false;
        }
        /// <summary>The slot whose background colour is the most common one among the slots with (or without) an item: a slot under the
        /// mouse or being pressed has another tint and loses the vote.</summary>
        static int Majority(Color[] bg, bool[] ok, bool[] filled, bool want)
        {
            int best = -1, bestN = 0;
            for (int i = 0; i < Slots; i++)
            {
                if (!ok[i] || filled[i] != want) continue;
                int n = 0; for (int j = 0; j < Slots; j++) if (ok[j] && filled[j] == want && Close(bg[i], bg[j])) n++;
                if (n > bestN) { bestN = n; best = i; }
            }
            return best;
        }
        /// <summary>Garage, main thread, at most once a second until two samples in a row agree and then every 30 s: the game's own bar, while it is on screen
        /// with no menu over it, is read (nothing of it is changed).</summary>
        static void SampleGameBar(EntityManager em)
        {
            if (!GameLook || gbFailed) return;
            // review: every 30 s only once two samples in a row gave the same colours (a button fades from one tint to the other in
            // about 0.1 s — e.g. right after the row changed — and a sample taken in that moment would be a colour in between)
            tSample += Time.unscaledDeltaTime; if (tSample < (lookOk && lookStable ? 30f : 1f)) return; tSample = 0f;
            if ((Overlay(em) & MenuMask) != 0) return;
            var ui = UIManager._singleton; if (ui == null) return;
            var src = ui._handComponentsList; if (src == null) return;
            var go = src.gameObject; if (go == null || !go.activeInHierarchy) return;
            var items = src._items; if (items == null || items.Length < Slots) return;
            var bg = new Color[Slots]; var ok = new bool[Slots]; var filled = new bool[Slots]; string btnSaid = "";
            slotOfTmp.Clear();
            for (int i = 0; i < Slots; i++)
            {
                var it = items[i]; if (it == null) return;
                slotOfTmp[it.transform.Pointer] = i;
                filled[i] = it._prefab != null;
                var b = it._button; Graphic tg = b != null ? b.targetGraphic : null;
                if (tg == null) tg = it.GetComponent<Graphic>();
                if (tg != null) { var r = tg.canvasRenderer; if (r != null) { bg[i] = r.GetColor(); ok[i] = true; } }
                // (for the log only: what the slot's button says its colours are — the check of where the dark look comes from)
                if (i == 0) { try { if (b == null) btnSaid = "; slot 1 has no button"; else if (b.transition != Selectable.Transition.ColorTint) btnSaid = "; slot 1's button does not tint (" + b.transition.ToString() + ")"; else { var cb = b.colors; btnSaid = "; slot 1's button: normal " + Col(Mul(cb.normalColor, cb.colorMultiplier)) + ", disabled " + Col(Mul(cb.disabledColor, cb.colorMultiplier)) + (b.interactable ? "" : " (now disabled)"); } } catch (Exception) { } }
            }
            int repF = Majority(bg, ok, filled, true), repE = Majority(bg, ok, filled, false);
            if (repF < 0 && repE < 0) return;
            // a bar that is fading or hidden some other way is not a look to copy
            if ((repF >= 0 && bg[repF].a < 0.05f) || (repF < 0 && bg[repE].a < 0.05f)) return;
            var f = new Dictionary<string, GLook>(); var e = new Dictionary<string, GLook>(); var rt0 = new Dictionary<string, GLook>();
            var gs = go.GetComponentsInChildren<Graphic>(true); IntPtr root = go.transform.Pointer; int n = 0;
            for (int i = 0; i < gs.Length; i++)
            {
                var g = gs[i]; if (g == null) continue;
                if (!Locate(g.transform, root, slotOfTmp, out int slot, out string rel)) continue;
                var d = slot < 0 ? rt0 : slot == repF ? f : slot == repE ? e : null; if (d == null) continue;
                var r = g.canvasRenderer; if (r == null) continue;
                d[rel] = new GLook { cr = r.GetColor(), gc = g.color, en = g.enabled, act = g.gameObject.activeSelf }; n++;
            }
            slotOfTmp.Clear();
            var rect = go.GetComponent<RectTransform>();
            lookStable = lookOk && lookHasFilled == (repF >= 0) && lookHasEmpty == (repE >= 0) && (repF < 0 || Close(lookBgFilled, bg[repF])) && (repE < 0 || Close(lookBgEmpty, bg[repE]));
            lookFilled = repF >= 0 ? f : null; lookEmpty = repE >= 0 ? e : null; lookRoot = rt0;
            lookHasFilled = repF >= 0; lookHasEmpty = repE >= 0;
            if (repF >= 0) lookBgFilled = bg[repF]; if (repE >= 0) lookBgEmpty = bg[repE];
            lookRect = rect != null;
            if (lookRect) { lookAMin = rect.anchorMin; lookAMax = rect.anchorMax; lookPivot = rect.pivot; lookPos = rect.anchoredPosition; lookSize = rect.sizeDelta; lookScale = rect.localScale; }
            lookCount = n; lookOk = true; lookVersion++;
            string said = (lookHasFilled ? "slot with an item " + Col(lookBgFilled) : "no slot with an item") + ", " + (lookHasEmpty ? "empty slot " + Col(lookBgEmpty) : "no empty slot on the bar (the button's disabled colour is used for those)")
                        + (lookRect ? "; bar at " + V2(lookPos) + " size " + V2(lookSize) : "") + btnSaid;
            if (said != lookSaid && lookLogs++ < 3) { lookSaid = said; ModLog.Info("hotbar look: the game's own bar sampled in the garage — CanvasRenderer colour of the " + said + "; " + n + " graphic(s) read"); }
        }
        /// <summary>The colour the slot's background gets through its CanvasRenderer: the garage sample, else the button's own colours.</summary>
        static bool SlotTint(int i, bool filled, out Color c)
        {
            c = Color.white;
            if (lookOk)
            {
                if (filled && lookHasFilled) { c = lookBgFilled; return true; }
                if (!filled && lookHasEmpty) { c = lookBgEmpty; return true; }
            }
            if (btnTint[i]) { c = filled ? btnNormal[i] : btnDisabled[i]; return true; }
            if (lookOk) { c = lookHasFilled ? lookBgFilled : lookBgEmpty; return true; }
            return false;
        }
        static void SetCr(CloneG cg, Color c)
        {
            try { if (!crBroken && cg.cr != null) { cg.cr.SetColor(c); return; } } catch (Exception) { crBroken = true; }
            try { cg.g.CrossFadeColor(c, 0f, true, true); } catch (Exception) { }   // the game's own way (what its buttons call)
        }
        /// <summary>The copy takes the look: every time it is shown, a slot changes or a new sample came. Never the mod's own parts.</summary>
        static void ApplyLook()
        {
            lookApplied = lookVersion;
            try
            {
                if (lookOk && lookRect && gbRect != null)
                {
                    if (gbRect.anchorMin != lookAMin) gbRect.anchorMin = lookAMin; if (gbRect.anchorMax != lookAMax) gbRect.anchorMax = lookAMax;
                    if (gbRect.pivot != lookPivot) gbRect.pivot = lookPivot; if (gbRect.sizeDelta != lookSize) gbRect.sizeDelta = lookSize;
                    if (gbRect.anchoredPosition != lookPos) gbRect.anchoredPosition = lookPos; if (gbRect.localScale != lookScale) gbRect.localScale = lookScale;
                }
            }
            catch (Exception) { }
            int tinted = 0, others = 0;
            for (int k = 0; k < gbGraphics.Count; k++)
            {
                var cg = gbGraphics[k];
                try
                {
                    if (cg.g == null) continue;
                    bool filled = cg.slot >= 0 && slots[cg.slot] != null;
                    var d = !lookOk ? null : cg.slot < 0 ? lookRoot : (filled ? (lookFilled ?? lookEmpty) : (lookEmpty ?? lookFilled));
                    GLook L = null; if (d != null) d.TryGetValue(cg.rel, out L);
                    if (cg.bg)
                    {
                        if (SlotTint(cg.slot, filled, out var c)) { SetCr(cg, c); tinted++; }
                        if (L != null && cg.g.color != L.gc) cg.g.color = L.gc;
                        continue;
                    }
                    if (cg.driven || L == null) continue;
                    SetCr(cg, L.cr); if (cg.g.color != L.gc) cg.g.color = L.gc; others++;
                    if (cg.slot >= 0)
                    {
                        // decoration inside a slot (number badge...): shown or not as on the game's bar
                        if (cg.g.enabled != L.en) cg.g.enabled = L.en;
                        if (cg.go != null && cg.go.activeSelf != L.act) cg.go.SetActive(L.act);
                    }
                }
                catch (Exception) { }
            }
            if (appliedLogs++ < 2)
            {
                SlotTint(0, true, out var cf); SlotTint(0, false, out var ce);
                ModLog.Info("hotbar look: applied to the copy — slot background " + Col(cf) + " with an item, " + Col(ce) + " empty (" + (lookOk ? "sampled from the game's bar in the garage" : btnTint[0] ? "from the buttons' colours: no garage sample in this session yet" : "NOT known: no sample and the buttons use no colour tint")
                            + "; buttons: normal " + (btnTint[0] ? Col(btnNormal[0]) : "-") + ", disabled " + (btnTint[0] ? Col(btnDisabled[0]) : "-") + "); " + tinted + " slot(s) tinted, " + others + " other graphic(s) set" + (crBroken ? "; CanvasRenderer.SetColor failed, CrossFadeColor used" : ""));
            }
        }
        /// <summary>The game-look bar is on screen now (then the mod's own drawn bar is not).</summary>
        static bool GameBarOn => gbOn;
        static void GameBarFailed(string why)
        {
            if (gbFailed) return; gbFailed = true;
            ModLog.Warn("hotbar: the copy of the game's hotbar cannot be used (" + why + ") — the mod draws its own bar instead");
            try { if (gbRoot != null) UnityEngine.Object.Destroy(gbRoot); } catch (Exception) { }
            gbRoot = null; gbOn = false;
        }
        static void HideGameBar()
        {
            if (!gbOn) return; gbOn = false;
            try { if (gbRoot != null) gbRoot.SetActive(false); } catch (Exception) { }
        }
        static Texture PhoneIcon()
        {
            if (gbTex != null) return gbTex;
            try { if (phoneEpc != null) gbTex = phoneEpc._iconTexture2D; } catch (Exception) { }
            if (gbTex != null) return gbTex;
            try
            {
                // not registered through this plugin's list (or no icon there yet): the loaded prefab by its name
                var objs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<EPC_SpaceshipComponent>());
                for (int i = 0; i < objs.Length; i++) { var o = objs[i]; if (o == null || o.name != "SC_PhoneDock") continue; var e = o.TryCast<EPC_SpaceshipComponent>(); if (e != null && e._iconTexture2D != null) { phoneEpc = e; gbTex = e._iconTexture2D; break; } }
            }
            catch (Exception) { }
            return gbTex;
        }
        /// <summary>Makes the copy (once; again if the game rebuilt its UI). Nothing of the game's own hotbar is changed.</summary>
        static void EnsureGameBar()
        {
            if (gbRoot != null || gbFailed) return;
            tGb += Time.unscaledDeltaTime; if (tGb < 1f) return; tGb = 0f;
            GameObject holder = null, clone = null;
            try
            {
                var ui = UIManager._singleton; if (ui == null) return;
                var src = ui._handComponentsList; if (src == null) return;
                var tex = PhoneIcon(); if (tex == null) { GameBarFailed("the Phone Dock's inventory icon was not found"); return; }
                var srcGo = src.gameObject; var parent = srcGo.transform.parent;
                if (parent == null) { GameBarFailed("the game's hotbar has no parent object"); return; }
                // an inactive holder: the copy is born inactive in the hierarchy, so none of its scripts wakes up
                holder = new GameObject("CM_HotbarHolder"); holder.SetActive(false); holder.transform.SetParent(parent, false);
                clone = UnityEngine.Object.Instantiate(srcGo, holder.transform, false);
                clone.name = "CM_Hotbar";
                var list = clone.GetComponent<UIHandComponentsList>();
                if (list == null) throw new InvalidOperationException("the copy has no UIHandComponentsList");
                var items = list._items;
                if (items == null || items.Length < Slots) throw new InvalidOperationException("the copy has " + (items == null ? 0 : items.Length) + " slot(s), expected " + Slots);
                for (int i = 0; i < Slots; i++)
                {
                    var it = items[i]; if (it == null) throw new InvalidOperationException("slot " + i + " is missing");
                    gbIcon[i] = it._iconImage; gbSel[i] = it._selectedFrame; gbCount[i] = it._remainingText;
                }
                gbName = list._handComponentNameText; gbRowId = list._rowIdText;
                // 0.31.36: the buttons' colours before the buttons go (the game tints each slot through its button), and the slots' roots
                var slotOf = new Dictionary<IntPtr, int>(); var bgPtr = new IntPtr[Slots];
                for (int i = 0; i < Slots; i++)
                {
                    btnTint[i] = false; bgPtr[i] = IntPtr.Zero;
                    try
                    {
                        var it = items[i]; slotOf[it.transform.Pointer] = i;
                        var b = it._button; Graphic tg = b != null ? b.targetGraphic : null;
                        if (tg == null) tg = it.GetComponent<Graphic>();
                        if (tg != null) bgPtr[i] = tg.Pointer;
                        if (b != null && b.transition == Selectable.Transition.ColorTint) { var cb = b.colors; float m = cb.colorMultiplier; btnNormal[i] = Mul(cb.normalColor, m); btnDisabled[i] = Mul(cb.disabledColor, m); btnTint[i] = true; }
                    }
                    catch (Exception) { }
                }
                // the game's UI blur switch stays when an image of the bar uses a material of its own (then the look needs the blurred screen)
                bool keepBlur = false; var mats = new List<string>();
                try
                {
                    var gs0 = clone.GetComponentsInChildren<Graphic>(true);
                    for (int i = 0; i < gs0.Length; i++)
                    {
                        var g = gs0[i]; if (g == null || g.TryCast<TMP_Text>() != null) continue;
                        var m = g.material; if (m == null) continue; string mn = m.name ?? "";
                        if (mn == "Default UI Material") continue;
                        keepBlur = true; string sn = ""; try { var sh = m.shader; if (sh != null) sn = sh.name; } catch (Exception) { }
                        string both = mn + " [" + sn + "]"; if (!mats.Contains(both)) mats.Add(both);
                    }
                }
                catch (Exception) { keepBlur = true; }
                GameObject stats = null;
                try { var sa = list._statsAutopaint; if (sa != null) stats = sa.transform.parent.parent.gameObject; } catch (Exception) { }
                // the scripts go first (they never woke up, so nothing of theirs runs), then the buttons. Kept: everything that is a UI
                // element (UIBehaviour: images, texts, layout groups, masks...) — that is the look; removed: every other script (the game's
                // hotbar logic, its slots' drag and drop, anything that reads keys or the mouse)
                int scripts = 0, buttons = 0, blurKept = 0; var removed = new List<string>();
                var mbs = clone.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = 0; i < mbs.Length; i++)
                {
                    var mb = mbs[i]; if (mb == null) continue;
                    if (mb.TryCast<UnityEngine.EventSystems.UIBehaviour>() != null) continue;
                    string tn = ""; try { tn = mb.GetIl2CppType().Name; } catch (Exception) { }
                    if (keepBlur && tn == "CRPUIBlurActivator") { blurKept++; continue; }   // only a use counter (OnEnable +1, OnDisable -1)
                    if (tn.Length > 0 && !removed.Contains(tn)) removed.Add(tn);
                    UnityEngine.Object.DestroyImmediate(mb); scripts++;
                }
                var bts = clone.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < bts.Length; i++) if (bts[i] != null) { UnityEngine.Object.DestroyImmediate(bts[i]); buttons++; }
                if (clone.GetComponent<UIHandComponentsList>() != null) throw new InvalidOperationException("the game's script is still on the copy");
                clone.SetActive(false);
                clone.transform.SetParent(parent, false);
                try { clone.transform.SetSiblingIndex(srcGo.transform.GetSiblingIndex() + 1); } catch (Exception) { }
                var cg = clone.AddComponent<CanvasGroup>(); cg.blocksRaycasts = false;   // the mouse never reaches the copy (interactable stays on: off would grey any selectable out)
                if (stats != null) stats.SetActive(false);
                if (gbName != null) { gbName.gameObject.SetActive(true); gbName.text = ""; }
                // 0.31.36: every graphic of the copy with its place in the tree (to give it the look of the game's bar)
                gbGraphics.Clear(); gbRect = clone.GetComponent<RectTransform>(); lookApplied = -1; crBroken = false;   // (a new copy tries CanvasRenderer.SetColor again: the error may have been the old copy, gone with the game's UI)
                try
                {
                    var driven = new HashSet<IntPtr>();
                    for (int i = 0; i < Slots; i++) { if (gbIcon[i] != null) driven.Add(gbIcon[i].Pointer); if (gbSel[i] != null) driven.Add(gbSel[i].Pointer); if (gbCount[i] != null) driven.Add(gbCount[i].Pointer); }
                    if (gbName != null) driven.Add(gbName.Pointer); if (gbRowId != null) driven.Add(gbRowId.Pointer);
                    var gs = clone.GetComponentsInChildren<Graphic>(true); IntPtr rootPtr = clone.transform.Pointer;
                    for (int i = 0; i < gs.Length; i++)
                    {
                        var g = gs[i]; if (g == null) continue;
                        if (!Locate(g.transform, rootPtr, slotOf, out int slot, out string rel)) continue;
                        gbGraphics.Add(new CloneG { g = g, cr = g.canvasRenderer, go = g.gameObject, slot = slot, rel = rel, bg = slot >= 0 && g.Pointer == bgPtr[slot], driven = driven.Contains(g.Pointer) });
                    }
                }
                catch (Exception ex) { ModLog.Info("hotbar look: the copy's graphics could not be listed (" + ex.Message + ") — it keeps the look it was born with"); }
                gbRoot = clone; clone = null; gbSrc = src.Pointer; gbState = int.MinValue; gbOn = false; gbNameAlpha = -1f;
                ModLog.Info("hotbar: using a copy of the game's own hotbar (" + scripts + " script(s) [" + string.Join(", ", removed) + "] and " + buttons + " button(s) removed from the copy before it was ever active; icon " + tex.name + " " + tex.width + "x" + tex.height
                            + "; " + (blurKept > 0 ? "the game's UI blur switch kept (materials: " + string.Join(", ", mats) + ")" : keepBlur ? "materials of its own: " + string.Join(", ", mats) : "plain UI material, no blur switch needed") + ")");
                if (!gbDumped) { gbDumped = true; try { int lines = 0; DumpTree(gbRoot.transform, gbRoot.name, ref lines, 0); } catch (Exception ex) { ModLog.Info("hotbar ui: (tree not logged: " + ex.Message + ")"); } }
            }
            catch (Exception ex)
            {
                try { if (clone != null) UnityEngine.Object.Destroy(clone); } catch (Exception) { }
                GameBarFailed(ex.Message);
            }
            finally { try { if (holder != null) UnityEngine.Object.Destroy(holder); } catch (Exception) { } }
        }
        static string V2(Vector2 v) => "(" + v.x.ToString("0.##") + ", " + v.y.ToString("0.##") + ")";
        static string Col(Color c) => "(" + c.r.ToString("0.##") + ", " + c.g.ToString("0.##") + ", " + c.b.ToString("0.##") + ", " + c.a.ToString("0.##") + ")";
        /// <summary>Once per copy: the UI tree of the game's hotbar in the log (sizes, sprites, fonts, colours) — to check the look and for the drawn fallback.</summary>
        static void DumpTree(Transform t, string path, ref int lines, int depth)
        {
            if (lines >= 150 || depth > 9 || t == null) return;
            lines++;
            var go = t.gameObject; var sb = new System.Text.StringBuilder("hotbar ui: ").Append(path).Append(go.activeSelf ? "" : " [off]");
            try
            {
                var rt = t.TryCast<RectTransform>();
                if (rt != null) sb.Append(" | rect anchors ").Append(V2(rt.anchorMin)).Append("..").Append(V2(rt.anchorMax)).Append(" pivot ").Append(V2(rt.pivot)).Append(" pos ").Append(V2(rt.anchoredPosition)).Append(" size ").Append(V2(rt.sizeDelta)).Append(" scale ").Append(t.localScale.x.ToString("0.##"));
                var comps = go.GetComponents<Component>(); var names = new List<string>();
                for (int i = 0; i < comps.Length; i++) { var c = comps[i]; if (c == null) continue; string n = c.GetIl2CppType().Name; if (n != "RectTransform" && n != "CanvasRenderer") names.Add(n); }
                if (names.Count > 0) sb.Append(" | ").Append(string.Join(", ", names));
                var img = go.GetComponent<Image>();
                if (img != null) { var sp = img.sprite; sb.Append(" | Image sprite ").Append(sp == null ? "none" : sp.name + " " + sp.rect.width.ToString("0") + "x" + sp.rect.height.ToString("0")).Append(" type ").Append(img.type.ToString()).Append(" color ").Append(Col(img.color)).Append(img.enabled ? "" : " (disabled)"); try { var m = img.material; if (m != null && m.name != "Default UI Material") sb.Append(" material ").Append(m.name); } catch (Exception) { } }
                var sh = go.GetComponent<Shadow>();
                if (sh != null) sb.Append(" | outline color ").Append(Col(sh.effectColor));
                var crd = go.GetComponent<CanvasRenderer>();
                if (crd != null) { var cc = crd.GetColor(); if (!Close(cc, Color.white)) sb.Append(" | renderer color ").Append(Col(cc)); }
                var raw = go.GetComponent<RawImage>();
                if (raw != null) { var tx = raw.texture; sb.Append(" | RawImage texture ").Append(tx == null ? "none" : tx.name + " " + tx.width + "x" + tx.height).Append(" color ").Append(Col(raw.color)).Append(raw.enabled ? "" : " (disabled)"); }
                var tmp = go.GetComponent<TMP_Text>();
                if (tmp != null) { var f = tmp.font; sb.Append(" | Text font ").Append(f == null ? "none" : f.name).Append(" size ").Append(tmp.fontSize.ToString("0.#")).Append(" color ").Append(Col(tmp.color)).Append(" text \"").Append(tmp.text ?? "").Append("\""); }
            }
            catch (Exception ex) { sb.Append(" | (").Append(ex.Message).Append(")"); }
            ModLog.Info(sb.ToString());
            int n2 = t.childCount;
            for (int i = 0; i < n2; i++) { var ch = t.GetChild(i); if (ch != null) DumpTree(ch, path + "/" + ch.name, ref lines, depth + 1); }
        }
        static float EaseInOutQuad(float t) => t < 0.5f ? 2f * t * t : 1f - (-2f * t + 2f) * (-2f * t + 2f) * 0.5f;
        /// <summary>Every frame of the mod cycle in flight: the copy is shown while something is carried; its icons, selection frames and
        /// name text follow the mod's slots (written only when something changed).</summary>
        static void UpdateGameBar()
        {
            bool want = barVisible && GameLook && !gbFailed;
            if (!want) { HideGameBar(); return; }
            EnsureGameBar();
            if (gbRoot == null) { gbOn = false; return; }
            tGbCheck += Time.unscaledDeltaTime;
            if (tGbCheck >= 2f)
            {
                // the game rebuilt its UI (another hotbar object): the copy is made again from the new one
                tGbCheck = 0f;
                var ui = UIManager._singleton; var src = ui != null ? ui._handComponentsList : null;
                if (src == null || src.Pointer != gbSrc) { try { UnityEngine.Object.Destroy(gbRoot); } catch (Exception) { } gbRoot = null; gbOn = false; tGb = 9f; return; }
                // 0.31.36: the look is still on the copy? (one read; a slot's tint that went back to white is put again)
                try
                {
                    for (int k = 0; k < gbGraphics.Count; k++)
                    {
                        var cg = gbGraphics[k]; if (!cg.bg || cg.cr == null) continue;
                        if (SlotTint(cg.slot, slots[cg.slot] != null, out var tintNow) && !crBroken && !Close(cg.cr.GetColor(), tintNow)) lookApplied = -1;
                        break;
                    }
                }
                catch (Exception) { }
            }
            if (!gbOn)
            {
                gbRoot.SetActive(true); gbOn = true; gbState = int.MinValue;
                if (!gbRoot.activeInHierarchy) { GameBarFailed("its parent is not shown in flight"); return; }
            }
            int state = selected + 1; for (int i = 0; i < Slots; i++) if (slots[i] != null) state |= 1 << (i + 5);
            if (state != gbState)
            {
                gbState = state; var tex = PhoneIcon();
                for (int i = 0; i < Slots; i++)
                {
                    bool has = slots[i] != null && tex != null;
                    var ic = gbIcon[i];
                    if (ic != null)
                    {
                        if (has) { if (!ic.gameObject.activeSelf) ic.gameObject.SetActive(true); ic.texture = tex; ic.color = Color.white; ic.enabled = true; }
                        else ic.enabled = false;
                    }
                    var sf = gbSel[i]; if (sf != null) sf.gameObject.SetActive(i == selected);
                    var ct = gbCount[i]; if (ct != null) ct.text = "";
                }
                if (gbRowId != null) gbRowId.text = "1";   // the mod has one row (the game shows the row number here)
                if (gbName != null)
                {
                    bool named = selected >= 0 && slots[selected] != null;
                    if (named) { gbName.text = RemoteComputer.Text("SC_PhoneDock_ItemName", "Phone"); gbNameAt = Time.unscaledTime; } else gbNameAt = -99f;
                }
                lookApplied = -1;   // 0.31.36: a slot got or lost its item: its background follows (with an item / empty)
            }
            if (lookApplied != lookVersion) ApplyLook();
            if (gbName != null)
            {
                // the game's own fade of the item name: full for 1.1 s, then 0.6 per second, eased (UIHandComponentsList)
                float t = (Time.unscaledTime - gbNameAt - 1.1f) * 0.6f;
                float a = gbNameAt < 0f ? 0f : t <= 0f ? 1f : t >= 1f ? 0f : 1f - EaseInOutQuad(t);
                if (Math.Abs(a - gbNameAlpha) > 0.004f) { gbName.alpha = a; gbNameAlpha = a; }
            }
        }

        // ------------------------------------------------------------------ the phone in the hand (3D)
        static Entity hand; static World handWorld; static int handLayer; static bool handFailed, handHidden = true, eyeNoted; static float tHandTry = 9f, focusT;
        static float3 eye; static int eyeFrame = -1;
        /// <summary>Makes the render entity: the dock phone's component types filtered by an allow-list (LocalToWorld + every CRP* data type
        /// except outlines / 3D text / types holding entity references) + ActiveWormholeWorld, and the bytes of each data component copied;
        /// buffers start empty (the game's RenderersMemoryBuilder clears and refills them every frame). WormholeWorldIndex is NOT copied: the game's
        /// CRPWormholeUpdate would remove ActiveWormholeWorld from it in another wormhole world; this entity follows the camera, so it is always active.
        /// No LocalTransform / Parent: the game's transform systems never touch it; the mod writes its LocalToWorld.</summary>
        static unsafe void EnsureHand(EntityManager em)
        {
            if (Ecs.Alive(em, hand)) return;
            hand = Entity.Null;
            tHandTry += Time.unscaledDeltaTime; if (tHandTry < 1f) return; tHandTry = 0f;
            Entity dock = SelectedIsPhone ? slots[selected].dock : Entity.Null;
            var src = RemoteComputer.DockToggle(dock);
            if (!Ecs.Alive(em, src)) src = RemoteComputer.AnyFlightToggle(em);
            if (!Ecs.Alive(em, src)) return;   // no dock phone to copy right now: the next try in 1 s
            try
            {
                var types = em.GetComponentTypes(src, Unity.Collections.Allocator.Temp);
                var keep = new List<TypeIndex>(); var names = new List<string>(); bool aww = false, data = false, layer = false;
                for (int i = 0; i < types.Length; i++)
                {
                    var ti = types[i].TypeIndex;
                    if (ti.Value == Ecs.tiLTW.Value) { keep.Add(ti); continue; }
                    if (ti.Value == tiAWW.Value) { keep.Add(ti); aww = true; continue; }
                    if (ti.IsSharedComponentType || ti.IsManagedComponent || ti.IsChunkComponent || ti.IsCleanupComponent || ti.HasEntityReferences) continue;
                    string n = null; try { var t = TypeManager.GetType(ti); if (t != null) n = t.Name; } catch (Exception) { }
                    if (n == null || !n.StartsWith("CRP") || n.Contains("Outline") || n.Contains("Text3D")) continue;
                    keep.Add(ti); names.Add(n);
                    if (n == "CRPRendererData") data = true; if (ti.Value == tiLayer.Value) layer = true;
                }
                if (!data || !layer) throw new InvalidOperationException("the dock's phone entity has no CRPRendererData / CRPLayerID (types: " + string.Join(", ", names) + ")");
                if (!aww) keep.Add(tiAWW);
                var arr = new Il2CppStructArray<ComponentType>(keep.Count);
                for (int i = 0; i < keep.Count; i++) arr[i] = ComponentType.ReadWrite(keep[i]);
                var e = Ecs.Create(em, arr); int copied = 0, copiedBytes = 0;
                try
                {
                    foreach (var ti in keep)
                    {
                        if (ti.IsBuffer || ti.IsZeroSized || !Ecs.HasC(em, src, ti)) continue;
                        // 0.31.36: the component's real size (the game's own sizeof of its struct). It was TypeManager.GetTypeInfo(ti).SizeInChunk,
                        // which the interop reads wrong in this game (a struct returned by reference: see Ecs.ComponentSize) — a wrong byte
                        // count here writes past the component. No size, or one no render component has = no copy at all: the UI phone instead
                        int size = Ecs.ComponentSize(ti);
                        if (size <= 0 || size > 1024) { string tn = "#" + ti.Value; try { var t = TypeManager.GetType(ti); if (t != null) tn = t.Name; } catch (Exception) { } throw new InvalidOperationException("the size of " + tn + " is not known (" + size + ")"); }
                        var s = (byte*)Ecs.RO(em, src, ti); var d = (byte*)em.GetComponentDataRawRW(e, ti);
                        if (s != null && d != null) { Buffer.MemoryCopy(s, d, size, size); copied++; copiedBytes += size; }
                    }
                    // review: the render memory header starts at 0 — copied from the dock it would already hold the dock's bits, and the
                    // self-check (CheckHandDrawn) would pass even if the game's renderer never touched this entity
                    if (headerOk && Ecs.HasC(em, e, tiHeader)) { var hp0 = (uint*)em.GetComponentDataRawRW(e, tiHeader); if (hp0 != null) *hp0 = 0u; }
                    var lp = (int*)em.GetComponentDataRawRW(e, tiLayer);
                    int srcLayer = *lp; handLayer = srcLayer == 31 ? RemoteComputer.DockLayer(dock) : srcLayer; if (handLayer == 31) handLayer = 0;
                    *lp = 31; handHidden = true;   // hidden until the first placement
                }
                catch (Exception) { try { Ecs.Destroy(em, e); } catch (Exception) { } throw; }
                hand = e; handWorld = World.DefaultGameObjectInjectionWorld; focusT = 0f; shownFrames = 0; handChecked = false;
                ModLog.Info("hand: phone drawn in 3D in front of the camera (entity " + e.Index + ", copy of dock phone entity " + src.Index + ": " + string.Join(", ", names) + (aww ? "" : ", + ActiveWormholeWorld") + "; " + copied + " component(s) copied, " + copiedBytes + " bytes in all, each with the game's own size of its struct; layer " + handLayer + ")");
            }
            catch (Exception ex)
            {
                handFailed = true;
                ModLog.Warn("hand: the 3D phone could not be made (" + ex.Message + ") — the phone in the hand is drawn by the mod's UI in the lower right instead");
            }
        }
        /// <summary>Self-check: once the 3D phone was shown for 30 frames, the game's RenderersMemoryBuilder must have written its memory header
        /// (a transform bit: CopyLTW 0x8 or UniformTransform 0x100). Still 0 = the game's renderer does not pick this entity up: switch to the UI phone.</summary>
        static unsafe void CheckHandDrawn(EntityManager em)
        {
            if (handChecked || !headerOk || Ecs.IsNull(hand) || shownFrames < 30 || handHidden) return;   // (review: checked while it is shown)
            handChecked = true;
            try
            {
                if (!Ecs.HasC(em, hand, tiHeader)) { ModLog.Info("hand: (the phone entity has no render memory header to check)"); return; }
                uint mask = *(uint*)Ecs.RO(em, hand, tiHeader);
                if (mask != 0) { ModLog.Info("hand: the game's renderer picked up the 3D phone (render memory mask 0x" + mask.ToString("X") + ")"); return; }
                handFailed = true; DestroyHand(em, "the game's renderer did not pick it up");
                ModLog.Warn("hand: the game's renderer did not pick up the 3D phone (render memory mask 0 after 30 frames) — the phone in the hand is drawn by the mod's UI in the lower right instead");
            }
            catch (Exception ex) { ModLog.Warn("hand: render check: " + ex.Message); }
        }
        static bool handChecked; static int shownFrames;
        static void DestroyHand(EntityManager em, string why)
        {
            if (Ecs.IsNull(hand)) return;
            try { if (handWorld != null && handWorld == World.DefaultGameObjectInjectionWorld && handWorld.IsCreated && Ecs.Alive(em, hand)) { Ecs.Destroy(em, hand); ModLog.Info("hand: 3D phone entity removed (" + why + ")"); } } catch (Exception) { }
            hand = Entity.Null; handWorld = null; handHidden = true;
        }
        /// <summary>Unloading (prefix of Core.ClearGame / CleanupWorldToDefaultState, before the game clears the world): the mod's entity goes first.</summary>
        public static void BeforeUnload()
        {
            if (Ecs.IsNull(hand)) return;
            try { var w = World.DefaultGameObjectInjectionWorld; if (w != null && w.IsCreated && w == handWorld) DestroyHand(w.EntityManager, "world unloading"); } catch (Exception) { }
            hand = Entity.Null; handWorld = null;
        }
        static Entity camEnt; static float tCam = 9f;
        static unsafe Entity Camera(EntityManager em)
        {
            tCam += Time.unscaledDeltaTime;
            if (!Ecs.Alive(em, camEnt) || tCam > 2f)
            {
                tCam = 0f; camEnt = Entity.Null;
                try { var p = First(qMine); if (Ecs.HasC(em, p, tiPCD)) camEnt = *(Entity*)((byte*)Ecs.RO(em, p, tiPCD) + 0xE8); } catch (Exception) { }   // PlayerControllerData._cameraEntity (TurretSeat.PlayerCamera)
            }
            return camEnt;
        }
        /// <summary>TransformSystemGroup postfix: where the game put the camera this frame (before anything moves it later in the frame).</summary>
        public static unsafe void CaptureEye()
        {
            if (Ecs.IsNull(hand) || !JointSystem.EmOk || !ok) return;
            try
            {
                var em = JointSystem.CurrentEm; var cam = Camera(em); if (!Ecs.HasC(em, cam, Ecs.tiLTW)) return;
                var p = (LocalToWorld*)Ecs.RO(em, cam, Ecs.tiLTW); if (p == null) return;
                eye = new float3 { x = p->Value.c3.x, y = p->Value.c3.y, z = p->Value.c3.z }; eyeFrame = Time.frameCount;
            }
            catch (Exception) { }
        }
        // pose: phone frame = the dock phone's mesh (x across, y up, screen towards -Z) un-tilted by the dock's 22 degrees (models_v2.py DOCK_TILT),
        // centred on its middle; held low right ~30 cm in front of the eye, turned a little towards the eye; focus = centre, 22 cm, facing the eye
        // 0.31.35 (Andre: "lower, almost cut by the bottom edge, so the missing hand is not seen"): the resting height is HandLow (cfg hand_low,
        // default -0.88: about a third of the phone below the bottom edge; it was -0.62, fully visible). The focus pose does not use it.
        const float Tilt = -22f, CenterY = 0.081f, CenterZ = 0.004f, Scale = 0.8f, RestNdcX = 0.62f, RestDist = 0.30f, FocusDist = 0.22f, RestPitch = 15f, RestYaw = 12f;
        /// <summary>PresentationSystemGroup prefix (after the turret seat's camera write, before the game's render pipeline): the hand phone's
        /// LocalToWorld from the camera's, and its layer (31 = hidden, like the game hides parts).</summary>
        public static unsafe void PlaceHand()
        {
            if (Ecs.IsNull(hand) || !JointSystem.EmOk || !ok) return;
            var em = JointSystem.CurrentEm;
            try
            {
                if (!Ecs.Alive(em, hand)) { hand = Entity.Null; return; }
                bool show = handVisible && Hand3D && wasFlight && Time.frameCount - lastFrame <= 3 && !Unloading.Active;
                var cam = Camera(em);
                LocalToWorld* cp = show && Ecs.HasC(em, cam, Ecs.tiLTW) ? (LocalToWorld*)Ecs.RO(em, cam, Ecs.tiLTW) : null;
                if (cp == null) show = false;
                if (show && eyeFrame == Time.frameCount)
                {
                    float dx = cp->Value.c3.x - eye.x, dy = cp->Value.c3.y - eye.y, dz = cp->Value.c3.z - eye.z;
                    if (dx * dx + dy * dy + dz * dz > 0.25f) { show = false; if (!eyeNoted) { eyeNoted = true; ModLog.Info("hand: the camera is away from the eyes (another mod's 3rd-person / free camera?) — the phone in the hand is hidden meanwhile"); } }
                }
                // review: hidden = only a read every frame (a RW access would bump the chunk and sync the jobs on CRPLayerID every frame for nothing)
                var lr = (int*)Ecs.RO(em, hand, tiLayer); if (lr == null) return;
                if (!show) { if (*lr != 31) { var lw = (int*)em.GetComponentDataRawRW(hand, tiLayer); if (lw != null) *lw = 31; } handHidden = true; return; }
                var lp = (int*)em.GetComponentDataRawRW(hand, tiLayer); if (lp == null) return;
                float dt = Math.Min(0.1f, Time.unscaledDeltaTime);
                focusT = handHidden ? (focus ? 1f : 0f) : Mathf.MoveTowards(focusT, focus ? 1f : 0f, dt / 0.15f);
                float f = focusT * focusT * (3f - 2f * focusT);
                float fov = 60f;
                if (camOk && Ecs.HasC(em, cam, tiCam)) { float v = *(float*)((byte*)Ecs.RO(em, cam, tiCam) + 56); if (v > 10f && v < 170f) fov = v; }   // CRPCameraData._cameraFOV (vertical, degrees)
                float tanH = (float)Math.Tan(fov * 0.5f * Math.PI / 180.0), aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 1.7778f;
                // position in the camera frame (x right, y up, z forward)
                float rx = RestNdcX * tanH * aspect * RestDist, ry = HandLow * tanH * RestDist;
                float px = rx * (1f - f), py = ry * (1f - f) + (-0.01f) * f, pz = RestDist + (FocusDist - RestDist) * f;
                float pitch = RestPitch * (1f - f), yaw = RestYaw * (1f - f);
                // local linear map L = S * Ry(yaw) * Rx(pitch) * Rx(Tilt); local translation = p - S * Ry * Rx(pitch) * c
                var R = Mul(RotY(yaw), RotX(pitch)); var L = Mul(R, RotX(Tilt));
                for (int i = 0; i < 9; i++) L[i] *= Scale;
                float cx = R[1] * CenterY + R[2] * CenterZ, cy = R[4] * CenterY + R[5] * CenterZ, cz = R[7] * CenterY + R[8] * CenterZ;
                float tx = px - Scale * cx, ty = py - Scale * cy, tz = pz - Scale * cz;
                // world = camera (orthonormal columns c0 right, c1 up, c2 forward, c3 position) * local
                var m = cp->Value;
                float[] C = { m.c0.x, m.c1.x, m.c2.x, m.c0.y, m.c1.y, m.c2.y, m.c0.z, m.c1.z, m.c2.z };
                var W = Mul(C, L);
                float wx = m.c3.x + C[0] * tx + C[1] * ty + C[2] * tz, wy = m.c3.y + C[3] * tx + C[4] * ty + C[5] * tz, wz = m.c3.z + C[6] * tx + C[7] * ty + C[8] * tz;
                var hp = (LocalToWorld*)em.GetComponentDataRawRW(hand, Ecs.tiLTW); if (hp == null) return;
                var o = hp->Value;
                o.c0 = new float4 { x = W[0], y = W[3], z = W[6], w = 0f }; o.c1 = new float4 { x = W[1], y = W[4], z = W[7], w = 0f };
                o.c2 = new float4 { x = W[2], y = W[5], z = W[8], w = 0f }; o.c3 = new float4 { x = wx, y = wy, z = wz, w = 1f };
                hp->Value = o;
                if (*lp != handLayer) *lp = handLayer;
                handHidden = false; if (shownFrames < 1000) shownFrames++;
            }
            catch (Exception ex) { if (placeWarn++ < 3) ModLog.Warn("hand: " + ex.Message); }
        }
        static int placeWarn;
        // 3x3 row-major helpers (plain floats: no interop calls every frame)
        static float[] RotX(float deg) { float a = deg * (float)Math.PI / 180f, c = (float)Math.Cos(a), s = (float)Math.Sin(a); return new[] { 1f, 0f, 0f, 0f, c, -s, 0f, s, c }; }
        static float[] RotY(float deg) { float a = deg * (float)Math.PI / 180f, c = (float)Math.Cos(a), s = (float)Math.Sin(a); return new[] { c, 0f, s, 0f, 1f, 0f, -s, 0f, c }; }
        static float[] Mul(float[] A, float[] B)
        {
            var r = new float[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 3 + j] = A[i * 3] * B[j] + A[i * 3 + 1] * B[3 + j] + A[i * 3 + 2] * B[6 + j];
            return r;
        }

        // ------------------------------------------------------------------ drawing (IMGUI, Cycle.OnGUI: reads only what Frame computed)
        static GUIStyle sNum, sClock; static float stylesH = -1f;
        public static void Draw()
        {
            try
            {
                if (Time.frameCount - lastFrame > 3) { HideGameBar(); return; }   // the mod cycle is paused (menu, replay, mod off): nothing of the hotbar on screen
                float tp = Phone.TakeProgress;
                if (tp >= 0f && tp <= 1f) TakeBar(tp);
                if (draw2D) Phone2D();
                if (barVisible && !GameBarOn) Bar();   // 0.31.35: the mod's own drawn bar only when the copy of the game's hotbar is not in use
            }
            catch (Exception ex) { if (drawWarn++ < 3) ModLog.Warn("hotbar: drawing: " + ex.Message); }
        }
        static int drawWarn;
        static void Styles(float h)
        {
            if (Math.Abs(h - stylesH) < 0.5f && sNum != null) return; stylesH = h;
            sNum = new GUIStyle(); sNum.fontSize = Math.Max(9, (int)(h * 0.26f)); sNum.fontStyle = FontStyle.Bold; var n = sNum.normal; n.textColor = new Color(0.93f, 0.95f, 0.97f, 0.9f); sNum.normal = n; sNum.alignment = TextAnchor.UpperLeft;
            sClock = new GUIStyle(); sClock.fontSize = Math.Max(8, (int)(h * 0.05f)); var c = sClock.normal; c.textColor = new Color(0.8f, 0.83f, 0.88f); sClock.normal = c; sClock.alignment = TextAnchor.MiddleCenter;
        }
        /// <summary>10 slots at the bottom centre in the game's dark style; the selected one framed. Full opacity for 2.5 s after a change, then dimmer.</summary>
        static void Bar()
        {
            float s = Mathf.Clamp(Screen.height * 0.05f, 34f, 64f), gap = s * 0.12f, total = Slots * s + (Slots - 1) * gap;
            float x0 = (Screen.width - total) * 0.5f, y = Screen.height - s - s * 0.35f;
            Styles(s);
            var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, Time.unscaledTime < showUntil ? 1f : 0.6f);
            for (int i = 0; i < Slots; i++)
            {
                var r = new Rect(x0 + i * (s + gap), y, s, s);
                if (i == selected) Phone.Panel(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), "hbsel", new Color32(225, 230, 240, 235), 7);
                Phone.Panel(r, "hbslot", new Color32(18, 20, 26, 190), 6);
                var it = slots[i];
                if (it != null && it.kind == Kind.Phone)
                {
                    float w = s * 0.32f, h = s * 0.62f; var b = new Rect(r.x + (s - w) * 0.5f, r.y + (s - h) * 0.5f + s * 0.04f, w, h);
                    Phone.Panel(b, "hbph", new Color32(44, 46, 52, 255), 3);
                    Phone.Panel(new Rect(b.x + w * 0.12f, b.y + h * 0.08f, w * 0.76f, h * 0.84f), "hbscr", new Color32(60, 110, 190, 255), 2);
                }
                GUI.Label(new Rect(r.x + s * 0.08f, r.y + s * 0.02f, s * 0.5f, s * 0.4f), ((i + 1) % 10).ToString(), sNum);
            }
            GUI.color = old;
        }
        /// <summary>Holding E on a dock's phone in flight: a small bar under the crosshair fills up until the phone is taken.</summary>
        static void TakeBar(float t)
        {
            float w = Mathf.Clamp(Screen.height * 0.07f, 50f, 110f), h = Math.Max(4f, Screen.height * 0.006f);
            var r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.5f + Screen.height * 0.03f, w, h);
            Phone.Panel(r, "hbtk0", new Color32(0, 0, 0, 150), 2);
            if (t > 0.02f) Phone.Panel(new Rect(r.x, r.y, w * t, h), "hbtk1", new Color32(225, 230, 240, 235), 2);
        }
        /// <summary>Fallback ([Remote Computer] hand_3d = false, or the 3D entity could not be made): the phone held in the lower right, drawn by the mod's UI.</summary>
        static void Phone2D()
        {
            float h = Mathf.Clamp(Screen.height * 0.30f, 140f, 420f), w = h * 0.5f;
            var body = new Rect(Screen.width - w - Screen.width * 0.07f, Screen.height - h * 0.62f, w, h);   // 0.31.35: lower (about 62 % of it shows), like the 3D one
            Styles(h);
            Phone.Panel(body, "hb2body", new Color32(14, 15, 18, 255), 14);
            float b = w * 0.05f; var scr = new Rect(body.x + b, body.y + b * 1.4f, w - 2f * b, h - b * 2.8f);
            Phone.Panel(scr, "hb2scr", new Color32(22, 26, 32, 255), 9);
            GUI.Label(new Rect(scr.x, scr.y + h * 0.02f, scr.width, h * 0.06f), DateTime.Now.ToString("HH:mm"), sClock);
            Color32[] cols = { new Color32(70, 120, 200, 255), new Color32(90, 180, 120, 255), new Color32(210, 160, 60, 255), new Color32(200, 80, 80, 255) };
            float ic = scr.width * 0.16f, gx = (scr.width - 4f * ic) / 5f;
            for (int i = 0; i < 4; i++) for (int j = 0; j < 3; j++)
                    Phone.Panel(new Rect(scr.x + gx + i * (ic + gx), scr.y + h * 0.12f + j * (ic + gx), ic, ic), "hb2ic" + ((i + j) % 4), cols[(i + j) % 4], 4);
        }
    }
}
