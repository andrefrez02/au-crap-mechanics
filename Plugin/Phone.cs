using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrapMechanics
{
    /// <summary>Remote control, delivery 1: the phone on the Phone Dock. A SHORT press of the game's action key (E by default) while the
    /// game's pointer is on the dock's phone opens the phone screen in front of the camera, with the game's cursor; Esc or E closes it.
    /// (A long press is kept for delivery 2: phone in the hand.) How (research remoto_e1_pesquisa.md, section 4):
    ///   pointer = ActionablesController._targetEntity (+8); E on a toggle does nothing in the game, so the mod measures the press itself
    ///   (the game's seat does the same with Esc: hold time, 0.2 s);
    ///   game input off = CRPInputOverride._enabledInput (+0) = false (the tutorial's switch: look, move, jetpack, E, clicks, Esc and hotkeys
    ///   all read as released); the mod keeps reading the keyboard and mouse itself. Turned back on only after the keys are released
    ///   (otherwise the game sees a fresh Esc press and opens its menu), and only if the mod turned it off;
    ///   cursor = bit 20 of OverlaySingleton._opened (the game's ids are 0..14): UIManager then frees and shows the game's cursor, and
    ///   locks it again when the bit is cleared.
    /// App "Remote": the active channel 0..99 (arrows), 10 buttons (keys 1..0 too). Each button shows the state of the nearest powered
    /// Remote Computer on that channel within range, and its row's type/value mode. x..y rows show x and y with a pencil each: a keypad
    /// sets them; they are kept per channel + button in BepInEx/config/CrapMechanics/phone.txt.
    /// Delivery 2: a LONG press (held ShortPress s) on a dock that shows its phone takes it to the hotbar (Hotbar.cs); a short press on an empty dock
    /// puts it back. The same app opens "in the hand" (right click with the phone selected in the hotbar: Hotbar.cs), and in the GARAGE, where a
    /// switch-type row press sets the row's START state (saved per computer in knobs.txt, applied at launch; RemoteComputer.ApplyStart).
    /// Round 0.31.35:
    ///   INPUT rows (the row's DIRECTION switch): the button shows the incoming number, always lit in a colour picked on the phone, with the
    ///   decimals picked on the phone (both per channel + button, phone.txt); keys 1..0 and clicks on it only show a short note.
    ///   NAMES: the channel and every button can be renamed. On the phone (the pencil next to the name; kept per channel + button in
    ///   phone.txt) and at the computer (aim at its front display = channel name, or at a row's display / switches = row name, and press E;
    ///   kept per block in names.txt). The phone's own name wins; an empty one falls back to the nearest computer's; none = the old labels.
    ///   The rename box takes typed text from TextInput.cs (the game's own typed-text buffer), Enter = OK, Esc = cancel, Del = clear,
    ///   Ctrl+V = paste, 16 characters at most. While it is open nothing else of the phone or the hotbar reacts to keys. At the computer the
    ///   box uses the same lock as the phone (the game's input off, cursor shown) with no phone screen behind it.</summary>
    internal static class Phone
    {
        public static float ShortPress = 0.35f;
        const uint MyBit = 1u << 20, MenuMask = 0x3FCF;
        static bool open, inputOffByMod, bitByMod, restorePending, deferredBit;
        static Entity dock; static int channel;
        static float eDownAt = -1f; static Entity eDock, eTarget; static bool eUsed, eFlight; static Key actionKey = Key.E; static float tKey = 9f;
        static bool handMode, openInFlight; static int closedFrame = -1; static float takeProgress = -1f;
        /// <summary>button = a button-type row held down; garage = pressed in the garage (only blinks: nothing is sent or stored for a button row).</summary>
        sealed class Held { public int n; public bool button, byMouse, releasing, garage; public Key key; public float pressAt, releaseAt; public List<RemoteComputer.Comp> comps; }
        static readonly Dictionary<int, Held> held = new Dictionary<int, Held>();
        static int kpRow = -1; static bool kpY, kpFresh; static string kpText = "";
        static List<RemoteComputer.Comp> matching = new List<RemoteComputer.Comp>(); static float nearestM = -1f, noneFlashUntil;
        static TypeIndex tiOverlay, tiInput, tiCtrl, tiOptions, tiPlayer; static EntityQuery qOverlay, qInput, qCtrl, qOptions, qPlayer; static bool ok, failed, playerOk; static int offFreeze, offHealth;
        static int lastFrame = -1; static bool deadAtOpen;   // review 22/11: watchdog (the mod cycle stopped calling Frame) and player death
        // 0.31.35: the rename / edit box. boxOnly = opened at a Remote Computer (no phone screen); renInput = the button is an input row (colour + decimals too)
        enum Ren { None, PhoneChannel, PhoneButton, CompChannel, CompRow }
        static Ren ren = Ren.None; static int renRow; static string renText = ""; static RemoteComputer.Comp renComp; static bool boxOnly, renInput, bsChar; static float bsAt = -1f;
        static float infoUntil; static string infoText = ""; static bool inputNoted; static int renOkFrame = -1;
        /// <summary>0.31.35: a rename box is open (typed keys belong to it: the hotbar must not react to them).</summary>
        public static bool Typing => open && ren != Ren.None;
        static readonly Key[] DigitKeys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0 };
        static readonly Key[] PadKeys = { Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9, Key.Numpad0 };
        public static bool IsOpen => open;
        /// <summary>Delivery 2: the app is open on the phone in the hand (right click), not on a dock.</summary>
        public static bool HandMode => open && handMode && !boxOnly;
        /// <summary>Delivery 2: the frame the app was last closed (the right click that closed it must not open it again in the same frame).</summary>
        public static int ClosedFrame => closedFrame;
        /// <summary>Delivery 2: 0..1 while E is held on a dock's phone in flight (the take progress shown near the crosshair), -1 otherwise.</summary>
        public static float TakeProgress => takeProgress;

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiOverlay = TypeManager.GetTypeIndex(Il2CppType.Of<OverlaySingleton>());
                tiInput = TypeManager.GetTypeIndex(Il2CppType.Of<CRPInputOverride>());
                tiCtrl = TypeManager.GetTypeIndex(Il2CppType.Of<ActionablesController>());
                tiOptions = TypeManager.GetTypeIndex(Il2CppType.Of<OptionsData>());
                qOverlay = Q(em, tiOverlay); qInput = Q(em, tiInput); qCtrl = Q(em, tiCtrl); qOptions = Q(em, tiOptions);
                try { tiPlayer = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); qPlayer = Q(em, tiPlayer); offFreeze = FieldOffset(typeof(PlayerControllerData), "NativeFieldInfoPtr__freeze"); offHealth = FieldOffset(typeof(PlayerControllerData), "NativeFieldInfoPtr__health01"); playerOk = true; } catch (Exception ex) { ModLog.Warn("phone: player data not found (the phone will not close by itself on death): " + ex.Message); }
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Error("phone: game types not found, the phone cannot open: " + ex.Message); return false; }
        }
        /// <summary>Offset of a struct field from the interop's NativeFieldInfoPtr_* (il2cpp counts the 16-byte object header).</summary>
        static int FieldOffset(Type t, string field)
        {
            var f = t.GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (f == null) throw new InvalidOperationException(t.Name + "." + field + " not found in the interop");
            int off = (int)IL2CPP.il2cpp_field_get_offset((IntPtr)f.GetValue(null)) - 16;
            if (off < 0) throw new InvalidOperationException(t.Name + "." + field + ": bad offset");
            return off;
        }
        static EntityQuery Q(EntityManager em, TypeIndex ti) { var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(ti); return em.CreateEntityQuery(a); }
        static Entity First(EntityQuery q) { foreach (var e in Ecs.QueryList(q)) return e; return Entity.Null; }
        static unsafe byte* Ptr(EntityManager em, EntityQuery q, TypeIndex ti, bool rw)
        {
            var e = First(q); if (!Ecs.HasC(em, e, ti)) return null;
            return rw ? (byte*)em.GetComponentDataRawRW(e, ti) : (byte*)Ecs.RO(em, e, ti);
        }
        static bool InFlight() { try { return Ecs.Ok && Ecs.QSing.CalculateEntityCountWithoutFiltering() > 0; } catch (Exception) { return false; } }
        static unsafe Key ReadActionKey(EntityManager em)
        {
            try
            {
                var p = Ptr(em, qOptions, tiOptions, false); if (p == null) return Key.E;
                int k = *(int*)(p + 156);   // OptionsData._controls (+148) ._keyAction (+8); CRPKey = the Input System's Key numbers
                if (k > 0 && Enum.IsDefined(typeof(Key), k)) return (Key)k;
            }
            catch (Exception) { }
            return Key.E;
        }
        static unsafe Entity Target(EntityManager em)
        {
            var p = Ptr(em, qCtrl, tiCtrl, false); return p == null ? Entity.Null : *(Entity*)(p + 8);
        }
        static unsafe uint Overlay(EntityManager em) { var p = Ptr(em, qOverlay, tiOverlay, false); return p == null ? 0u : *(uint*)p; }
        /// <summary>ActionablesController._assignedEntity (+0): something the player holds (a lever being dragged, a package...).</summary>
        static unsafe Entity Assigned(EntityManager em) { var p = Ptr(em, qCtrl, tiCtrl, false); return p == null ? Entity.Null : *(Entity*)p; }
        /// <summary>Review 22/11: the player is dead — PlayerControllerData._freeze has the Death flag (1 &lt;&lt; 4, after GarageGrabberRotatingSC 1,
        /// Actionables 2, BlueprintPlacing 4, Tutorial 8) or _health01 is 0. Offsets from the interop's field info (layout 21/11: +508, +512).</summary>
        internal static unsafe bool Dead(EntityManager em)
        {
            if (!playerOk) return false;
            try { var e = First(qPlayer); if (!Ecs.HasC(em, e, tiPlayer)) return false; var p = (byte*)Ecs.RO(em, e, tiPlayer); return p != null && ((*(uint*)(p + offFreeze) & 16u) != 0 || *(float*)(p + offHealth) <= 0f); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ open / close
        /// <summary>Main thread, every frame the mod cycle runs (flight or garage).</summary>
        public static void Frame(EntityManager em, float dt)
        {
            if (!Prepare(em)) return;
            tKey += dt; if (tKey >= 2f) { tKey = 0f; actionKey = ReadActionKey(em); }
            lastFrame = Time.frameCount;
            if (!open) { Detect(em); return; }
            takeProgress = -1f;
            // delivery 2: the app also opens in the garage; it closes when the mode changes under it (launch / back to the garage)
            if (InFlight() != openInFlight) { Close(em, openInFlight ? "the flight ended" : "the flight started"); return; }
            // review 22/11: the player died with the phone open (only if they were alive when it opened: a bad read never locks the phone shut)
            if (!deadAtOpen && Dead(em)) { Close(em, "the player died"); return; }
            if (boxOnly)
            {
                // 0.31.35: the rename box opened at a computer: only the box (no phone, no computers list)
                if (ren == Ren.None || renComp == null || !Ecs.Alive(em, renComp.block)) { Close(em, "the computer is gone"); return; }
                if ((Overlay(em) & MenuMask) != 0) { Close(em, "a game menu opened"); return; }
                Keep(em);
                RenameInput(em, MouseGui(), Click());
                return;
            }
            if (!handMode && !Ecs.Alive(em, dock)) { Close(em, "the dock is gone"); return; }
            Keep(em);
            try
            {
                var from = From(em);
                matching = openInFlight ? RemoteComputer.Matching(em, channel, from) : RemoteComputer.GarageMatching(em, channel, from);
                nearestM = matching.Count > 0 ? (float)Math.Sqrt(Dist2(matching[0].pos, from)) : -1f;
                if (openInFlight && matching.Count > 0) RemoteComputer.RefreshInputs(em, matching[0]);   // 0.31.35: input rows show a live value
            }
            catch (Exception) { matching = new List<RemoteComputer.Comp>(); }
            HandleInput(em);
            Releases(em);
        }
        static void Detect(EntityManager em)
        {
            float now = Time.unscaledTime; takeProgress = -1f;
            bool flight = InFlight();
            if (InputKeys.Pressed(actionKey))
            {
                eDownAt = -1f; eUsed = false;
                var t = Target(em);
                if (RemoteComputer.IsDockPhone(em, t, out var d)) { eDownAt = now; eDock = d; eTarget = t; eFlight = flight; }
                // 0.31.35: E on a Remote Computer's front display (channel name) or on a row's display / switches (row name): the rename box.
                // E does nothing on a lever or a toggle in the game; only standing, with nothing held, no menu, host / solo.
                else if (RemoteComputer.Host && RemoteComputer.RenameTarget(em, t, out var rc, out int rrow) && (Overlay(em) & MenuMask) == 0 && Ecs.IsNull(Assigned(em)) && !Hotbar.SeatedNow(em) && !Dead(em))
                { OpenBox(em, rc, rrow); return; }
            }
            if (eDownAt < 0f) return;
            if (InputKeys.Holding(actionKey))
            {
                // delivery 2: holding E on a dock that shows its phone takes it as soon as ShortPress is reached (the release then does nothing)
                if (eUsed || !flight || !eFlight) return;
                var t = Target(em);
                if (t.Index != eTarget.Index || t.Version != eTarget.Version) { eDownAt = -1f; return; }   // looked away while holding: cancelled
                if (!RemoteComputer.DockHasPhone(eDock) || !Hotbar.CanTake) return;
                float h = now - eDownAt;
                if (h < ShortPress) { takeProgress = h / ShortPress; return; }
                eUsed = true; eDownAt = -1f;
                if ((Overlay(em) & MenuMask) == 0) Hotbar.TakeFrom(em, eDock);
                return;
            }
            float tHeld = now - eDownAt; eDownAt = -1f;
            if (eUsed) return;
            if (tHeld >= ShortPress)
            {
                if (!longNoted) { longNoted = true; ModLog.Info("phone: long press (" + tHeld.ToString("F2") + " s) on the dock did nothing — " + (!flight ? "the phone is taken only in flight (in the garage a short tap opens the app)" : !RemoteComputer.DockHasPhone(eDock) ? "the dock is empty (a short tap puts the phone back)" : "the phone cannot be taken now (client, full hotbar or seated)")); }
                return;
            }
            if ((Overlay(em) & MenuMask) != 0 || !Ecs.Alive(em, eDock)) return;
            // delivery 2: a short tap on an EMPTY dock in flight puts its phone back
            if (flight && RemoteComputer.DockTracked(eDock) && !RemoteComputer.DockHasPhone(eDock)) { Hotbar.PutBack(em, eDock); return; }
            Open(em, eDock);
        }
        static bool longNoted;
        static unsafe void Open(EntityManager em, Entity d)
        {
            dock = d; handMode = false; boxOnly = false; ren = Ren.None; openInFlight = InFlight(); channel = Memory.Channel; open = true; held.Clear(); kpRow = -1; lastFrame = Time.frameCount; deadAtOpen = Dead(em);
            restorePending = false; deferredBit = false;   // reopened before the input came back: the mod still owns what it turned off
            Keep(em);
            Sound(0);
            ModLog.Info("phone: opened (dock " + d.Index + ", channel " + channel + (openInFlight ? "" : ", GARAGE: switch rows set the start state") + ")" + (RemoteComputer.Host ? "" : " — this machine is a client: the Remote app runs on the host only for now") + (inputOffByMod ? "" : " — the game's input was already off (or not found): it is left as it is"));
        }
        /// <summary>Delivery 2: right click with the phone in the hand (Hotbar.cs): the same app, focused in the centre. Flight only.</summary>
        public static void OpenHand(EntityManager em)
        {
            if (open || !Prepare(em) || !InFlight()) return;
            dock = Entity.Null; handMode = true; boxOnly = false; ren = Ren.None; openInFlight = true; channel = Memory.Channel; open = true; held.Clear(); kpRow = -1; lastFrame = Time.frameCount; deadAtOpen = Dead(em);
            restorePending = false; deferredBit = false;
            Keep(em);
            Sound(0);
            ModLog.Info("phone: focus ON (phone in the hand, channel " + channel + ")" + (inputOffByMod ? "" : " — the game's input was already off (or not found): it is left as it is"));
        }
        /// <summary>0.31.35: the rename box at a Remote Computer (row 0 = its channel name, 1..10 = a row's name): the phone's lock (the game's
        /// input off, the cursor shown) with only the box on screen.</summary>
        static void OpenBox(EntityManager em, RemoteComputer.Comp c, int row)
        {
            dock = Entity.Null; handMode = false; boxOnly = true; openInFlight = InFlight(); open = true; held.Clear(); kpRow = -1; lastFrame = Time.frameCount; deadAtOpen = Dead(em);
            restorePending = false; deferredBit = false;
            Keep(em);
            renComp = c; StartRename(row <= 0 ? Ren.CompChannel : Ren.CompRow, row, RemoteComputer.NameOf(c, row), false, actionKey);
            ModLog.Info("rename: box opened at SC_RemoteComputer " + c.block.Index + " (" + (row <= 0 ? "channel name" : "row " + row + " name") + ", now \"" + renText + "\")" + (inputOffByMod ? "" : " — the game's input was already off (or not found): it is left as it is"));
        }
        /// <summary>Delivery 2: the hotbar closes the focused phone (seated, died, menu, hand emptied, holding something, garage...).</summary>
        public static void CloseHand(EntityManager em, string why) { if (open && handMode) Close(em, why); }
        /// <summary>The cursor bit and the game's input stay as the phone needs them while it is open.</summary>
        static unsafe void Keep(EntityManager em)
        {
            var o = Ptr(em, qOverlay, tiOverlay, false);
            if (o != null && (*(uint*)o & MyBit) == 0) { var w = Ptr(em, qOverlay, tiOverlay, true); if (w != null) { *(uint*)w |= MyBit; bitByMod = true; } }
            var i = Ptr(em, qInput, tiInput, false);
            if (i != null && *i != 0) { var w = Ptr(em, qInput, tiInput, true); if (w != null) { *w = 0; inputOffByMod = true; } }
        }
        static unsafe void ClearBit(EntityManager em)
        {
            if (!bitByMod) return; bitByMod = false;
            var w = Ptr(em, qOverlay, tiOverlay, true); if (w != null) *(uint*)w &= ~MyBit;
        }
        static void Close(EntityManager em, string why)
        {
            if (!open) return;
            foreach (var h in held.Values) if (h.button && !h.garage) { try { var (x, y) = Memory.XY(channel, h.n); RemoteComputer.Release(em, h.comps, h.n, x, y); } catch (Exception) { } }
            held.Clear(); kpRow = -1; open = false; closedFrame = Time.frameCount;
            if (ren != Ren.None) { ren = Ren.None; TextInput.End(); }
            try { ClearBit(em); } catch (Exception) { deferredBit = true; }
            restorePending = inputOffByMod;
            ModLog.Info(boxOnly ? "rename: box closed (" + why + ")" : handMode ? "phone: focus OFF (" + why + ")" : "phone: closed (" + why + ")");
            handMode = false; boxOnly = false; renComp = null;
        }
        /// <summary>Start of every Cycle.Update, before its early returns: closes the phone when the mod cycle stops (blueprint menu, replay,
        /// world unload, mod off) and gives the game its input back once Esc / E / clicks / 1..0 are released.</summary>
        public static void Guard()
        {
            if (!open && !restorePending && !deferredBit) return;
            if (Unloading.Window)
            {
                if (open) { open = false; handMode = false; boxOnly = false; renComp = null; if (ren != Ren.None) { ren = Ren.None; TextInput.End(); } closedFrame = Time.frameCount; held.Clear(); kpRow = -1; deferredBit = bitByMod; restorePending = inputOffByMod; ModLog.Info("phone: closed (world unloading)"); }
                return;
            }
            var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated || !ok) return;
            var em = w.EntityManager;
            try
            {
                if (open && (CrapMechanicsPlugin.IsActive == null || !CrapMechanicsPlugin.IsActive.Value || Unloading.Active || JointSystem.MenuBP || ReplayLink.InPlayback())) Close(em, "the mod cycle paused");
                // review 22/11: Frame (input, Esc/E) is no longer being called — something before it in the cycle returns early or throws.
                // Without this the player would be stuck with the game's input off and no way to close the phone.
                if (open && lastFrame >= 0 && Time.frameCount - lastFrame > 30) Close(em, "the mod cycle stopped updating the phone");
                if (deferredBit && !open) { deferredBit = false; ClearBit(em); }
                if (restorePending && !open && KeysUp()) RestoreInput(em);
            }
            catch (Exception ex) { if (guardWarn++ < 3) ModLog.Warn("phone: " + ex.Message); }
        }
        static int guardWarn;
        static bool KeysUp()
        {
            if (InputKeys.Holding(Key.Escape) || InputKeys.Holding(actionKey) || InputKeys.Holding(Key.Enter) || InputKeys.Holding(Key.NumpadEnter)) return false;
            foreach (var k in DigitKeys) if (InputKeys.Holding(k)) return false;
            foreach (var k in PadKeys) if (InputKeys.Holding(k)) return false;   // review 22/11: the numpad shortcuts too
            try { var m = Mouse.current; if (m != null && (m.leftButton.isPressed || m.rightButton.isPressed)) return false; } catch (Exception) { }
            return true;
        }
        static unsafe void RestoreInput(EntityManager em)
        {
            restorePending = false;
            if (!inputOffByMod) return; inputOffByMod = false;
            var w = Ptr(em, qInput, tiInput, true); if (w != null) *w = 1;
            ModLog.Info("phone: game input back on");
        }
        public static void Forget() { held.Clear(); kpRow = -1; matching = new List<RemoteComputer.Comp>(); renComp = null; TextInput.Forget(); }

        // ------------------------------------------------------------------ input while open
        static Vector2 MouseGui()
        {
            try { var m = Mouse.current; if (m == null) return new Vector2(-1f, -1f); var p = m.position.ReadValue(); return new Vector2(p.x, Screen.height - p.y); } catch (Exception) { return new Vector2(-1f, -1f); }
        }
        static bool Click() { try { var m = Mouse.current; return m != null && m.leftButton.wasPressedThisFrame; } catch (Exception) { return false; } }
        static bool MouseDown() { try { var m = Mouse.current; return m != null && m.leftButton.isPressed; } catch (Exception) { return false; } }
        static void HandleInput(EntityManager em)
        {
            var L = Layout(); var mp = MouseGui(); bool click = Click();
            if (ren != Ren.None) { RenameInput(em, mp, click); return; }   // 0.31.35: every key belongs to the rename box (E types a letter, Esc cancels only the box)
            if (kpRow >= 0) { KeypadInput(em, L, mp, click); return; }
            if (handMode) { bool esc = InputKeys.Pressed(Key.Escape); if (esc || Hotbar.FocusClickPressed()) { Close(em, esc ? "Esc" : "right click"); return; } }   // delivery 2: E does not close the phone in the hand
            else if (InputKeys.Pressed(Key.Escape) || InputKeys.Pressed(actionKey)) { Close(em, InputKeys.Pressed(Key.Escape) ? "Esc" : actionKey.ToString()); return; }
            if (InputKeys.Pressed(Key.LeftArrow) || (click && L.chLeft.Contains(mp))) { SetChannel(em, channel - 1); return; }
            if (InputKeys.Pressed(Key.RightArrow) || (click && L.chRight.Contains(mp))) { SetChannel(em, channel + 1); return; }
            for (int i = 0; i < 10; i++)
            {
                if (InputKeys.Pressed(DigitKeys[i])) Press(em, i + 1, false, DigitKeys[i]);
                else if (InputKeys.Pressed(PadKeys[i])) Press(em, i + 1, false, PadKeys[i]);
            }
            if (!click) return;
            var near = matching.Count > 0 ? matching[0] : null;
            if (L.chPen.Contains(mp)) { renComp = near; StartRename(Ren.PhoneChannel, 0, Memory.Name(channel, 0), false, Key.None); return; }
            for (int i = 0; i < 10; i++)
            {
                bool isIn = near != null && near.rows[i].input;
                if (L.pn[i].Contains(mp)) { renComp = near; StartRename(Ren.PhoneButton, i + 1, Memory.Name(channel, i + 1), isIn, Key.None); return; }
                if (isIn) { if (L.btn[i].Contains(mp)) { InputInfo(i + 1); return; } continue; }
                bool xy = near != null && near.rows[i].valXY;
                if (xy && L.fx[i].Contains(mp)) { OpenKeypad(i + 1, false); return; }
                if (xy && L.fy[i].Contains(mp)) { OpenKeypad(i + 1, true); return; }
                if (L.btn[i].Contains(mp)) { Press(em, i + 1, true, Key.None); return; }
            }
        }
        static void SetChannel(EntityManager em, int c)
        {
            ReleaseAll(em);
            channel = ((c % 100) + 100) % 100; Memory.Channel = channel; Sound(0);
        }
        static float3 From(EntityManager em)
        {
            try { var p = Ecs.MyPlayerOrNull(em); if (Ecs.HasC(em, p, Ecs.tiLTW)) return Ecs.Position(em, p); } catch (Exception) { }
            try { return Ecs.Position(em, dock); } catch (Exception) { return default; }
        }
        static float Dist2(float3 a, float3 b) { var d = Ecs.Sub(a, b); return Ecs.Dot(d, d); }
        static void Press(EntityManager em, int n, bool byMouse, Key key)
        {
            if (held.ContainsKey(n)) return;
            if (!openInFlight) { GaragePress(em, n, byMouse, key); return; }
            if (!RemoteComputer.Host || matching.Count == 0) { Sound(2); noneFlashUntil = Time.unscaledTime + 0.6f; if (!noneLogged) { noneLogged = true; ModLog.Info("remote: phone channel " + channel + ", button " + n + ": " + (RemoteComputer.Host ? "no powered computer on this channel within " + RemoteComputer.RangeM.ToString("F0") + " m" : "this machine is a client (host only for now)")); } return; }
            noneLogged = false;
            var list = new List<RemoteComputer.Comp>(matching); var row = list[0].rows[n - 1];
            if (row.input) { InputInfo(n); return; }   // 0.31.35: an input row takes no command
            var (x, y) = Memory.XY(channel, n);
            Sound(1);
            var h = new Held { n = n, byMouse = byMouse, key = key, pressAt = Time.unscaledTime, comps = list };
            if (row.typSwitch)
            {
                bool on = !row.on;   // explicit state from the nearest computer, so every computer of the channel ends up the same
                RemoteComputer.Apply(em, list, n, on, x, y, false);
                ModLog.Info("remote: phone channel " + channel + ", button " + n + " -> " + (on ? "ON" : "OFF") + " (switch, value " + Num(Value(row, on, x, y)) + ") — " + list.Count + " computer(s) obeyed (nearest " + nearestM.ToString("F0") + " m)");
            }
            else
            {
                h.button = true;
                RemoteComputer.Apply(em, list, n, true, x, y, true);
                ModLog.Info("remote: phone channel " + channel + ", button " + n + " -> ON while held (button, value " + Num(Value(row, true, x, y)) + ") — " + list.Count + " computer(s) obeyed (nearest " + nearestM.ToString("F0") + " m)");
            }
            held[n] = h;
        }
        static bool noneLogged, garageButtonNoted;
        /// <summary>0.31.35: a key or a click on an INPUT row's button: nothing is sent, the status line says why for a moment.</summary>
        static void InputInfo(int n)
        {
            infoText = RemoteComputer.Text("SC_PhoneDock_AppInInfo", "Button {0} is an input: it only shows a value").Replace("{0}", n.ToString()); infoUntil = Time.unscaledTime + 1.6f;
            if (!inputNoted) { inputNoted = true; ModLog.Info("remote: phone channel " + channel + ", button " + n + " is an INPUT row on the nearest computer — the press does nothing (the button shows the incoming value)"); }
        }

        // ------------------------------------------------------------------ 0.31.35: rename / edit box
        static void StartRename(Ren kind, int row, string current, bool inputRow, Key openedBy)
        {
            ren = kind; renRow = row; renText = Names.Clean(current ?? ""); renInput = inputRow && kind == Ren.PhoneButton; bsAt = -1f; kpRow = -1; renOkFrame = -1;
            TextInput.Begin(openedBy); Sound(0);
        }
        static void Backspace() { if (renText.Length > 0) renText = renText.Substring(0, renText.Length - 1); }
        static void AppendText(string t) { if (t == null) return; foreach (char c in t) if (Names.Allowed(c) && renText.Length < Names.MaxLen) renText += c; }
        static void RenameInput(EntityManager em, Vector2 mp, bool click)
        {
            var R = RenLayout();
            string typed = TextInput.Take(); float now = Time.unscaledTime;
            if (InputKeys.Pressed(Key.Escape) || (click && R.cancel.Contains(mp))) { EndRename(em, false); return; }
            if (InputKeys.Pressed(Key.Delete)) { renText = ""; Sound(0); }
            // paste: the game's own Ctrl+V needs the game's input, which is off here
            if ((InputKeys.Holding(Key.LeftCtrl) || InputKeys.Holding(Key.RightCtrl)) && InputKeys.Pressed(Key.V)) { try { AppendText(GUIUtility.systemCopyBuffer); } catch (Exception) { } }
            bool sawBs = false;
            foreach (char c in typed)
            {
                if (c == (char)8) { sawBs = true; Backspace(); }
                else AppendText(c.ToString());
            }
            // review 0.31.35: Enter / OK confirm one frame later, after that frame's characters went in. The keys are seen at once but the
            // typed characters come from the game's buffer, read after this Update — at 10 FPS a quick "c, Enter" lands in one frame and
            // the "c" was lost. Enter and Esc themselves arrive as control characters, which AppendText drops
            if (renOkFrame >= 0) { if (Time.frameCount > renOkFrame) { renOkFrame = -1; EndRename(em, true); } return; }
            if (InputKeys.Pressed(Key.Enter) || InputKeys.Pressed(Key.NumpadEnter) || (click && R.ok.Contains(mp))) { renOkFrame = Time.frameCount; return; }
            // Backspace normally arrives as character 8 (with the system's key repeat). If it never does, the key itself deletes one character.
            if (sawBs) { bsChar = true; bsAt = -1f; }
            else if (!bsChar)
            {
                if (InputKeys.Pressed(Key.Backspace)) bsAt = now;
                else if (bsAt >= 0f && now - bsAt > 0.15f) { bsAt = -1f; Backspace(); }
            }
            if (!renInput || !click) return;
            for (int i = 0; i < Palette.Length; i++) if (R.pal[i].Contains(mp)) { Memory.SetColor(channel, renRow, i); Sound(0); return; }
            for (int i = 0; i <= 6; i++) if (R.dec[i].Contains(mp)) { Memory.SetDec(channel, renRow, i); Sound(0); return; }
        }
        static void EndRename(EntityManager em, bool ok)
        {
            var kind = ren; int row = renRow; string name = Names.Clean(renText);
            ren = Ren.None; TextInput.End(); Sound(0);
            if (ok)
            {
                try
                {
                    switch (kind)
                    {
                        case Ren.PhoneChannel: Memory.SetName(channel, 0, name); ModLog.Info("rename: phone, channel " + channel + (name.Length > 0 ? " = \"" + name + "\"" : ": name removed (the computer's name shows, if any)")); break;
                        case Ren.PhoneButton: Memory.SetName(channel, row, name); ModLog.Info("rename: phone, channel " + channel + " button " + row + (name.Length > 0 ? " = \"" + name + "\"" : ": name removed (the computer's name shows, if any)")); break;
                        case Ren.CompChannel:
                        case Ren.CompRow:
                            bool saved = RemoteComputer.SetName(em, renComp, kind == Ren.CompChannel ? 0 : row, name);
                            ModLog.Info("rename: SC_RemoteComputer " + (renComp == null ? "?" : renComp.block.Index.ToString()) + (kind == Ren.CompChannel ? " channel name" : " row " + row + " name") + (saved ? (name.Length > 0 ? " = \"" + name + "\" (names.txt)" : " removed") : " NOT saved"));
                            break;
                    }
                }
                catch (Exception ex) { ModLog.Warn("rename: " + ex.Message); }
            }
            if (boxOnly) Close(em, ok ? "Enter" : "Esc");
        }
        /// <summary>The name shown for the channel (n = 0) or a button (1..10): the phone's own, else the nearest computer's, else "".</summary>
        static string ShownName(int n, RemoteComputer.Comp near)
        {
            string s = Memory.Name(channel, n);
            return s.Length > 0 ? s : RemoteComputer.NameOf(near, n);
        }
        /// <summary>A number for an input button: `dec` decimals (0..6), but never more digits than a float really has (8 minus the digits
        /// before the point); thousands in groups; from 1e12 up in scientific notation; NaN = "—".</summary>
        internal static string FormatValue(float v, int dec)
        {
            if (float.IsNaN(v)) return "—";
            if (float.IsPositiveInfinity(v)) return "∞"; if (float.IsNegativeInfinity(v)) return "-∞";
            dec = Math.Max(0, Math.Min(6, dec));
            // the shortest decimal that is this float (1e11f is "100000000000", not the 99999997952 its bits spell out)
            double dv; if (!double.TryParse(v.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) dv = v;
            double a = Math.Abs(dv);
            if (a >= 1e12) { int m = Math.Min(dec, 4); return dv.ToString(m == 0 ? "0E+0" : "0." + new string('0', m) + "E+0", CultureInfo.InvariantCulture); }
            int intDigits = a < 1.0 ? 1 : (int)Math.Floor(Math.Log10(a)) + 1;
            int d = Math.Min(dec, Math.Max(0, 8 - intDigits));
            string s = dv.ToString("F" + d, CultureInfo.InvariantCulture);
            bool neg = s.StartsWith("-"); if (neg) s = s.Substring(1);
            bool zero = true; foreach (char c in s) if (c >= '1' && c <= '9') { zero = false; break; }
            int dot = s.IndexOf('.'); string ip = dot < 0 ? s : s.Substring(0, dot), fp = dot < 0 ? "" : s.Substring(dot);
            if (ip.Length > 4) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < ip.Length; i++) { if (i > 0 && (ip.Length - i) % 3 == 0) sb.Append(' '); sb.Append(ip[i]); } ip = sb.ToString(); }
            return (neg && !zero ? "-" : "") + ip + fp;
        }
        /// <summary>Delivery 2, garage: a switch-type row press flips the row's START state of every garage computer on the channel (knobs.txt);
        /// a button-type row only blinks while held (buttons are momentary, nothing is stored). Nothing is sent: the garage has no circuits running.</summary>
        static void GaragePress(EntityManager em, int n, bool byMouse, Key key)
        {
            if (!RemoteComputer.Host || matching.Count == 0)
            {
                Sound(2); noneFlashUntil = Time.unscaledTime + 0.6f;
                if (!noneLogged) { noneLogged = true; ModLog.Info("remote (garage): phone channel " + channel + ", button " + n + ": " + (RemoteComputer.Host ? "no Remote Computer on this channel in the garage" : "this machine is a client (host only for now)")); }
                return;
            }
            noneLogged = false;
            var list = new List<RemoteComputer.Comp>(matching); var row = list[0].rows[n - 1];
            if (row.input) { InputInfo(n); return; }
            Sound(1);
            var h = new Held { n = n, byMouse = byMouse, key = key, pressAt = Time.unscaledTime, comps = list, garage = true };
            if (row.typSwitch)
            {
                bool on = !row.on;
                int saved = RemoteComputer.SetGarageStart(em, list, n, on);
                ModLog.Info("remote (garage): phone channel " + channel + ", row " + n + " start state -> " + (on ? "ON" : "OFF") + " (saved in knobs.txt for " + saved + " of " + list.Count + " computer(s); applied at launch)");
            }
            else
            {
                h.button = true;
                if (!garageButtonNoted) { garageButtonNoted = true; ModLog.Info("remote (garage): row " + n + " is a button (momentary): it only blinks in the garage, nothing is stored"); }
            }
            held[n] = h;
        }
        static float Value(RemoteComputer.Row r, bool on, float x, float y) => on ? (r.valXY ? y : 1f) : (r.valXY ? x : 0f);
        /// <summary>Released buttons: a button row goes off once the "on" was seen for a couple of game ticks (a quick tap still works).</summary>
        static void Releases(EntityManager em)
        {
            if (held.Count == 0) return;
            bool mouseDown = MouseDown(); float now = Time.unscaledTime; List<int> done = null;
            foreach (var h in held.Values)
            {
                if (!h.releasing) { bool up = h.byMouse ? !mouseDown : !InputKeys.Holding(h.key); if (up) { h.releasing = true; h.releaseAt = now; } }
                if (!h.releasing) continue;
                if (h.button && !h.garage)
                {
                    if (!RemoteComputer.SeenOn(em, h.comps, h.n) && now - h.releaseAt < 0.5f) continue;
                    var (x, y) = Memory.XY(channel, h.n);
                    RemoteComputer.Release(em, h.comps, h.n, x, y);
                    var r = h.comps.Count > 0 ? h.comps[0].rows[h.n - 1] : null;
                    ModLog.Info("remote: phone channel " + channel + ", button " + h.n + " released -> OFF (value " + (r == null ? "?" : Num(Value(r, false, x, y))) + ") after " + (now - h.pressAt).ToString("F2") + " s — " + h.comps.Count + " computer(s)");
                }
                (done ?? (done = new List<int>())).Add(h.n);
            }
            if (done != null) foreach (var n in done) held.Remove(n);
        }
        static void ReleaseAll(EntityManager em)
        {
            foreach (var h in held.Values) if (h.button && !h.garage) { var (x, y) = Memory.XY(channel, h.n); RemoteComputer.Release(em, h.comps, h.n, x, y); }
            held.Clear();
        }

        // ------------------------------------------------------------------ keypad
        static void OpenKeypad(int n, bool isY)
        {
            kpRow = n; kpY = isY; var (x, y) = Memory.XY(channel, n); kpText = Num(isY ? y : x); kpFresh = true; Sound(0);
        }
        static readonly string[] KpLabels = { "7", "8", "9", "DEL", "4", "5", "6", "-", "1", "2", "3", ",", "0", "ESC", "OK", "OK" };
        static void KeypadInput(EntityManager em, Lay L, Vector2 mp, bool click)
        {
            if (handMode) { if (Hotbar.FocusClickPressed()) { kpRow = -1; Close(em, "right click"); return; } }
            else if (InputKeys.Pressed(actionKey)) { kpRow = -1; Close(em, actionKey.ToString()); return; }
            string k = null;
            if (InputKeys.Pressed(Key.Escape)) k = "ESC";
            else if (InputKeys.Pressed(Key.Enter) || InputKeys.Pressed(Key.NumpadEnter)) k = "OK";
            else if (InputKeys.Pressed(Key.Backspace) || InputKeys.Pressed(Key.Delete)) k = "DEL";
            else if (InputKeys.Pressed(Key.Minus) || InputKeys.Pressed(Key.NumpadMinus)) k = "-";
            else if (InputKeys.Pressed(Key.Comma) || InputKeys.Pressed(Key.Period) || InputKeys.Pressed(Key.NumpadPeriod)) k = ",";
            else for (int i = 0; i < 10; i++) if (InputKeys.Pressed(DigitKeys[i]) || InputKeys.Pressed(PadKeys[i])) { k = ((i + 1) % 10).ToString(); break; }
            if (k == null && click) for (int i = 0; i < 16; i++) if (L.kp[i].Contains(mp)) { k = KpLabels[i]; break; }
            if (k == null) return;
            Sound(0);
            switch (k)
            {
                case "ESC": kpRow = -1; return;
                case "OK": KeypadOk(em); return;
                case "DEL": if (kpFresh) kpText = ""; else if (kpText.Length > 0) kpText = kpText.Substring(0, kpText.Length - 1); kpFresh = false; return;
                case "-": kpText = kpText.StartsWith("-") ? kpText.Substring(1) : "-" + kpText; kpFresh = false; return;
                case ",": if (kpFresh) kpText = "0"; if (kpText.IndexOf(',') < 0 && kpText.IndexOf('.') < 0 && kpText.Length < 14) kpText += ","; kpFresh = false; return;
                default: if (kpFresh) kpText = ""; if (kpText.Length < 14) kpText += k; kpFresh = false; return;
            }
        }
        static void KeypadOk(EntityManager em)
        {
            string s = kpText.Replace(',', '.'); float v;
            if (s == "" || s == "-" || s == "." || s == "-.") v = 0f;
            else if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || float.IsNaN(v) || float.IsInfinity(v)) { Sound(2); return; }
            int n = kpRow; kpRow = -1;
            Memory.Set(channel, n, kpY, v);
            var (x, y) = Memory.XY(channel, n);
            var list = new List<RemoteComputer.Comp>(matching);
            RemoteComputer.ApplyXY(em, list, n, x, y);
            ModLog.Info("remote" + (openInFlight ? "" : " (garage)") + ": phone channel " + channel + ", button " + n + ": " + (kpY ? "y" : "x") + " = " + Num(v) + " (off " + Num(x) + ", on " + Num(y) + ") — sent to " + list.Count + " computer(s)");
        }

        // ------------------------------------------------------------------ sounds (the game's UI clicks)
        static void Sound(int which)
        {
            try { if (which == 1) UIManager.PlaySoundUIClickAction(); else if (which == 2) UIManager.PlaySoundUIClickInvalid(); else UIManager.PlaySoundUIClick(); }
            catch (Exception ex) { if (!soundWarned) { soundWarned = true; ModLog.Warn("phone: click sound: " + ex.Message); } }
        }
        static bool soundWarned;
        internal static string Num(float v) => v.ToString("G7", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ memory: active channel + x / y per channel and button
        internal static class Memory
        {
            static readonly Dictionary<int, (float x, float y)> xy = new Dictionary<int, (float, float)>();
            // 0.31.35, per channel + button (key = channel * 100 + n; n = 0 is the channel itself): the phone's names, and an input button's colour and decimals
            static readonly Dictionary<int, string> names = new Dictionary<int, string>();
            static readonly Dictionary<int, int> colors = new Dictionary<int, int>(), decs = new Dictionary<int, int>();
            public const int DefaultDec = 4;
            static int ch; static bool loaded; static string path;
            static string FilePath()
            {
                if (path == null) { var folder = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.PluginName); try { System.IO.Directory.CreateDirectory(folder); } catch (Exception) { } path = System.IO.Path.Combine(folder, "phone.txt"); }
                return path;
            }
            static void Load()
            {
                if (loaded) return; loaded = true;
                try
                {
                    if (!System.IO.File.Exists(FilePath())) return;
                    foreach (var line in System.IO.File.ReadAllLines(FilePath(), System.Text.Encoding.UTF8))
                    {
                        var p = line.Trim().Split(' '); if (p.Length == 0 || p[0].StartsWith("#")) continue;
                        if (p[0] == "name")
                        {
                            // "name <channel> <n> <text...>" (the text is the rest of the line)
                            var q = line.TrimStart().Split(new[] { ' ' }, 4);
                            if (q.Length == 4 && int.TryParse(q[1], out var nc) && int.TryParse(q[2], out var nn) && nc >= 0 && nc <= 99 && nn >= 0 && nn <= 10) { string t = Names.Clean(q[3]); if (t.Length > 0) names[nc * 100 + nn] = t; }
                            continue;
                        }
                        if ((p[0] == "color" || p[0] == "dec") && p.Length >= 4 && int.TryParse(p[1], out var kc) && int.TryParse(p[2], out var kn) && int.TryParse(p[3], out var kv) && kc >= 0 && kc <= 99 && kn >= 1 && kn <= 10)
                        {
                            if (p[0] == "color") colors[kc * 100 + kn] = Math.Max(0, Math.Min(Palette.Length - 1, kv)); else decs[kc * 100 + kn] = Math.Max(0, Math.Min(6, kv));
                            continue;
                        }
                        if (p[0] == "channel" && p.Length >= 2 && int.TryParse(p[1], out var c)) ch = Math.Max(0, Math.Min(99, c));
                        else if (p[0] == "xy" && p.Length >= 5 && int.TryParse(p[1], out var cc) && int.TryParse(p[2], out var n)
                                 && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                            xy[cc * 100 + n] = (x, y);
                    }
                    ModLog.Info("phone: memory loaded from " + FilePath() + " (channel " + ch + ", " + xy.Count + " x..y button(s), " + names.Count + " name(s), " + colors.Count + " colour(s), " + decs.Count + " decimals setting(s))");
                }
                catch (Exception ex) { ModLog.Warn("phone: could not read " + FilePath() + ": " + ex.Message); }
            }
            static void Save()
            {
                try
                {
                    var sb = new System.Text.StringBuilder("# Crap Mechanics phone (Remote app): the active channel; per channel and button: xy = x (off) / y (on); name = the phone's own name (button 0 = the channel); color / dec = an input button's colour (0..7) and decimals (0..6)\n");
                    sb.Append("channel ").Append(ch).Append('\n');
                    var keys = new List<int>(xy.Keys); keys.Sort();
                    foreach (var k in keys) sb.Append("xy ").Append(k / 100).Append(' ').Append(k % 100).Append(' ').Append(xy[k].x.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(xy[k].y.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                    keys = new List<int>(names.Keys); keys.Sort();
                    foreach (var k in keys) sb.Append("name ").Append(k / 100).Append(' ').Append(k % 100).Append(' ').Append(names[k]).Append('\n');
                    keys = new List<int>(colors.Keys); keys.Sort();
                    foreach (var k in keys) sb.Append("color ").Append(k / 100).Append(' ').Append(k % 100).Append(' ').Append(colors[k]).Append('\n');
                    keys = new List<int>(decs.Keys); keys.Sort();
                    foreach (var k in keys) sb.Append("dec ").Append(k / 100).Append(' ').Append(k % 100).Append(' ').Append(decs[k]).Append('\n');
                    System.IO.File.WriteAllText(FilePath(), sb.ToString(), new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex) { ModLog.Warn("phone: could not write " + FilePath() + ": " + ex.Message); }
            }
            /// <summary>The phone's own name of a channel (n = 0) or a button (1..10); "" = none (the computer's name shows).</summary>
            public static string Name(int c, int n) { Load(); return names.TryGetValue(c * 100 + n, out var v) ? v : ""; }
            public static void SetName(int c, int n, string name)
            {
                Load(); name = Names.Clean(name); int k = c * 100 + n;
                names.TryGetValue(k, out var cur); if ((cur ?? "") == name) return;
                if (name.Length == 0) names.Remove(k); else names[k] = name;
                Save();
            }
            public static int Color(int c, int n) { Load(); return colors.TryGetValue(c * 100 + n, out var v) ? v : 0; }
            public static void SetColor(int c, int n, int i) { Load(); if (Color(c, n) == i) return; colors[c * 100 + n] = i; Save(); }
            public static int Dec(int c, int n) { Load(); return decs.TryGetValue(c * 100 + n, out var v) ? v : DefaultDec; }
            public static void SetDec(int c, int n, int d) { Load(); if (Dec(c, n) == d) return; decs[c * 100 + n] = d; Save(); }
            public static int Channel { get { Load(); return ch; } set { Load(); if (ch != value) { ch = value; Save(); } } }
            public static (float x, float y) XY(int c, int n) { Load(); return xy.TryGetValue(c * 100 + n, out var v) ? v : (0f, 1f); }
            public static void Set(int c, int n, bool isY, float v)
            {
                Load(); var cur = XY(c, n);
                xy[c * 100 + n] = isY ? (cur.x, v) : (v, cur.y); Save();
            }
        }

        // ------------------------------------------------------------------ drawing (IMGUI, Cycle.OnGUI)
        internal sealed class Lay
        {
            public Rect body, screen, clock, title, chLeft, chText, chRight, chPen, status, footer, kpPanel, kpTitle, kpDisplay; public float fs;
            public Rect[] btn = new Rect[10], fx = new Rect[10], fy = new Rect[10], kp = new Rect[16], pn = new Rect[10];   // pn = the pencil of each button's name (0.31.35)
        }
        static readonly Lay lay = new Lay();
        static Lay Layout()
        {
            var L = lay;
            float H = Mathf.Clamp(Screen.height * 0.82f, 380f, 1000f), W = H * 0.5f;
            L.body = new Rect((Screen.width - W) * 0.5f, (Screen.height - H) * 0.5f, W, H);
            float b = W * 0.04f;
            L.screen = new Rect(L.body.x + b, L.body.y + b * 1.4f, W - 2f * b, H - b * 2.8f);
            float fs = H / 44f; L.fs = fs;
            var s = L.screen; float pad = s.width * 0.05f, x0 = s.x + pad, w0 = s.width - 2f * pad, y = s.y + pad * 0.4f;
            L.clock = new Rect(x0, y, w0, fs * 1.3f); y += fs * 1.4f;
            L.title = new Rect(x0, y, w0, fs * 2.0f); y += fs * 2.2f;
            float ch = fs * 2.6f;
            L.chLeft = new Rect(x0, y, ch * 1.3f, ch); L.chRight = new Rect(x0 + w0 - ch * 1.3f, y, ch * 1.3f, ch); L.chText = new Rect(x0 + ch * 1.4f, y, w0 - ch * 2.8f, ch);
            L.chPen = new Rect(L.chText.xMax - ch * 0.62f, y + ch * 0.19f, ch * 0.62f, ch * 0.62f); y += ch + pad * 0.4f;
            L.status = new Rect(x0, y, w0, fs * 1.6f); y += fs * 1.8f;
            L.footer = new Rect(x0, s.yMax - fs * 1.8f, w0, fs * 1.4f);
            float gap = pad * 0.45f, areaH = L.footer.y - gap - y, bh = (areaH - 4f * gap) / 5f, bw = (w0 - gap) / 2f;
            for (int i = 0; i < 10; i++)
            {
                int col = i % 2, row = i / 2;
                var r = new Rect(x0 + col * (bw + gap), y + row * (bh + gap), bw, bh); L.btn[i] = r;
                float fh = bh * 0.34f, fw = (bw - bh * 0.1f * 3f) / 2f, fy = r.yMax - fh - bh * 0.08f;
                L.fx[i] = new Rect(r.x + bh * 0.1f, fy, fw, fh); L.fy[i] = new Rect(r.x + bh * 0.2f + fw, fy, fw, fh);
                float ps = bh * 0.26f; L.pn[i] = new Rect(r.xMax - bh * 0.08f - ps, r.y + bh * 0.07f, ps, ps);
            }
            // keypad: over the lower part of the screen
            float ky = L.status.y; L.kpPanel = new Rect(s.x + pad * 0.3f, ky, s.width - pad * 0.6f, L.footer.y - ky - gap);
            L.kpTitle = new Rect(x0, ky + pad * 0.3f, w0, fs * 1.8f);
            L.kpDisplay = new Rect(x0, L.kpTitle.yMax + gap, w0, fs * 3.0f);
            float ktop = L.kpDisplay.yMax + gap * 1.5f, kh = (L.kpPanel.yMax - pad * 0.3f - ktop - 3f * gap) / 4f, kw = (w0 - 3f * gap) / 4f;
            for (int i = 0; i < 16; i++) { int c = i % 4, r = i / 4; L.kp[i] = new Rect(x0 + c * (kw + gap), ktop + r * (kh + gap), kw, kh); }
            L.kp[14] = new Rect(L.kp[14].x, L.kp[14].y, kw * 2f + gap, kh); L.kp[15] = new Rect(0f, 0f, 0f, 0f);   // OK is two keys wide
            return L;
        }

        // 0.31.35: the rename / edit box (over the lower part of the phone screen, or alone in the middle of the screen at a computer)
        internal sealed class RenLay { public Rect panel, title, field, sub, palLabel, decLabel, ok, cancel, help; public Rect[] pal = new Rect[8], dec = new Rect[7]; public float fs; }
        static readonly RenLay renLay = new RenLay();
        static RenLay RenLayout()
        {
            var R = renLay; Rect p; float fs;
            if (boxOnly) { fs = Mathf.Clamp(Screen.height / 54f, 12f, 26f); float w = fs * 25f, h = fs * 14f; p = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h); }
            else { var L = Layout(); fs = L.fs; p = L.kpPanel; }
            R.fs = fs; R.panel = p;
            float pad = fs * 0.9f, gap = fs * 0.35f, x0 = p.x + pad, w0 = p.width - 2f * pad, y = p.y + pad * 0.7f;
            R.title = new Rect(x0, y, w0, fs * 1.8f); y += fs * 2.2f;
            R.field = new Rect(x0, y, w0, fs * 3.0f); y += fs * 3.3f;
            R.sub = new Rect(x0, y, w0, fs * 1.4f); y += fs * 2.0f;
            if (renInput)
            {
                R.palLabel = new Rect(x0, y, w0, fs * 1.3f); y += fs * 1.5f;
                float sw = (w0 - 7f * gap) / 8f; for (int i = 0; i < 8; i++) R.pal[i] = new Rect(x0 + i * (sw + gap), y, sw, fs * 2.2f); y += fs * 2.8f;
                R.decLabel = new Rect(x0, y, w0, fs * 1.3f); y += fs * 1.5f;
                float dw = (w0 - 6f * gap) / 7f; for (int i = 0; i < 7; i++) R.dec[i] = new Rect(x0 + i * (dw + gap), y, dw, fs * 2.2f); y += fs * 2.8f;
            }
            else { for (int i = 0; i < 8; i++) R.pal[i] = default; for (int i = 0; i < 7; i++) R.dec[i] = default; }
            float bh = fs * 2.6f;
            R.help = new Rect(x0, p.yMax - pad * 0.6f - fs * 1.4f, w0, fs * 1.4f);
            float by = R.help.y - fs * 0.5f - bh;
            R.cancel = new Rect(x0, by, (w0 - gap) * 0.5f, bh); R.ok = new Rect(x0 + (w0 + gap) * 0.5f, by, (w0 - gap) * 0.5f, bh);
            return R;
        }

        static bool fancy = true; static readonly Dictionary<string, (Texture2D t, GUIStyle s)> bgs = new Dictionary<string, (Texture2D, GUIStyle)>();
        static GUIStyle sTitle, sText, sSmall, sNum, sState, sCenter, sKey, sName, sVal, sField, sLit, sChan, sMode; static float stylesFs = -1f;
        static GUIContent fitContent;
        /// <summary>0.31.35: an input button's colours (always lit; index kept per channel + button in phone.txt; 0 = teal, the default).</summary>
        internal static readonly Color32[] Palette = { new Color32(0, 132, 132, 255), new Color32(38, 104, 196, 255), new Color32(118, 78, 188, 255), new Color32(184, 60, 130, 255),
            new Color32(186, 58, 50, 255), new Color32(200, 116, 28, 255), new Color32(44, 136, 70, 255), new Color32(92, 100, 114, 255) };
        const string Dim = "#8a93a0";
        /// <summary>Shrinks the style's font until `plain` fits in `width` (never below `min`).</summary>
        static void Fit(GUIStyle st, string plain, float width, float max, float min)
        {
            int size = Math.Max(8, (int)max), lo = Math.Max(6, (int)min);
            try
            {
                if (fitContent == null) fitContent = new GUIContent();
                fitContent.text = plain; st.fontSize = size;
                float w = st.CalcSize(fitContent).x;
                if (w > width && w > 1f) size = Math.Max(lo, (int)(size * width / w));
            }
            catch (Exception) { size = Math.Max(lo, Math.Min(size, (int)(width / (Math.Max(1, plain.Length) * 0.58f)))); }
            st.fontSize = size;
        }
        static readonly List<IntPtr> pins = new List<IntPtr>();
        static Texture2D Round(Color32 c, int size, int rad)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            t.hideFlags = HideFlags.HideAndDontSave; t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
            var px = new Il2CppStructArray<Color32>(size * size);
            for (int yy = 0; yy < size; yy++)
                for (int xx = 0; xx < size; xx++)
                {
                    float cx = Math.Max(rad - 0.5f - xx, xx - (size - rad - 0.5f)), cy = Math.Max(rad - 0.5f - yy, yy - (size - rad - 0.5f));
                    float a = 1f;
                    if (cx > 0f && cy > 0f) { float d = (float)Math.Sqrt(cx * cx + cy * cy); a = Mathf.Clamp01(rad - d + 0.5f); }
                    px[yy * size + xx] = new Color32(c.r, c.g, c.b, (byte)(c.a * a));
                }
            t.SetPixels32(px); t.Apply(false, false);
            pins.Add(IL2CPP.il2cpp_gchandle_new(t.Pointer, false));
            return t;
        }
        static Texture2D Pencil(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            t.hideFlags = HideFlags.HideAndDontSave; t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
            var px = new Il2CppStructArray<Color32>(size * size);
            // a pencil from the bottom-left tip to the top-right end (image rows: 0 = bottom)
            float ax = size * 0.2f, ay = size * 0.2f, bx = size * 0.82f, by = size * 0.82f, half = size * 0.11f;
            for (int yy = 0; yy < size; yy++)
                for (int xx = 0; xx < size; xx++)
                {
                    float vx = bx - ax, vy = by - ay, wx = xx + 0.5f - ax, wy = yy + 0.5f - ay;
                    float tt = Mathf.Clamp01((wx * vx + wy * vy) / (vx * vx + vy * vy));
                    float dx = wx - tt * vx, dy = wy - tt * vy, dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    float width = tt < 0.22f ? half * (tt / 0.22f) : half;   // the sharpened tip
                    float a = Mathf.Clamp01(width - dist + 0.5f);
                    Color32 col = tt < 0.12f ? new Color32(40, 40, 46, 255) : tt < 0.22f ? new Color32(230, 200, 150, 255) : tt > 0.86f ? new Color32(230, 120, 120, 255) : new Color32(245, 190, 60, 255);
                    px[yy * size + xx] = new Color32(col.r, col.g, col.b, (byte)(255 * a));
                }
            t.SetPixels32(px); t.Apply(false, false);
            pins.Add(IL2CPP.il2cpp_gchandle_new(t.Pointer, false));
            return t;
        }
        static GUIStyle Bg(string key, Color32 c, int size, int rad)
        {
            if (bgs.TryGetValue(key, out var v) && v.t != null) return v.s;
            var t = key == "pencil" ? Pencil(size) : Round(c, size, rad);
            var s = new GUIStyle(); var n = s.normal; n.background = t; s.normal = n; s.border = new RectOffset(rad, rad, rad, rad);
            bgs[key] = (t, s); return s;
        }
        static GUIStyle TextStyle(float size, Color c, TextAnchor a, bool bold)
        {
            var s = new GUIStyle(); s.fontSize = Math.Max(8, (int)size); var n = s.normal; n.textColor = c; s.normal = n; s.alignment = a; s.wordWrap = false; s.clipping = TextClipping.Clip;
            if (bold) s.fontStyle = FontStyle.Bold; return s;
        }
        static void Styles(float fs)
        {
            if (Math.Abs(fs - stylesFs) < 0.5f && sText != null) return; stylesFs = fs;
            var white = new Color(0.93f, 0.95f, 0.97f); var grey = new Color(0.62f, 0.66f, 0.72f);
            sTitle = TextStyle(fs * 1.45f, white, TextAnchor.MiddleCenter, true);
            sText = TextStyle(fs * 1.15f, white, TextAnchor.MiddleCenter, false);
            sSmall = TextStyle(fs * 0.85f, grey, TextAnchor.MiddleCenter, false);
            sNum = TextStyle(fs * 1.6f, white, TextAnchor.UpperLeft, true);
            sState = TextStyle(fs * 0.9f, white, TextAnchor.UpperRight, true);
            sCenter = TextStyle(fs * 0.95f, white, TextAnchor.MiddleLeft, false);
            sKey = TextStyle(fs * 1.3f, white, TextAnchor.MiddleCenter, true);
            // 0.31.35: names (rich text: the dim "(1)" / "(ch 1)"), an input button's value, the rename field, small text on a lit button
            sName = TextStyle(fs * 1.15f, white, TextAnchor.UpperLeft, true); sName.richText = true;
            sChan = TextStyle(fs * 1.45f, white, TextAnchor.MiddleCenter, true); sChan.richText = true;
            sVal = TextStyle(fs * 2.0f, white, TextAnchor.MiddleCenter, true); sVal.richText = false;
            sField = TextStyle(fs * 1.45f, white, TextAnchor.MiddleLeft, true); sField.richText = false;
            sLit = TextStyle(fs * 0.85f, new Color(0.95f, 0.97f, 0.98f, 0.9f), TextAnchor.MiddleCenter, false);
            sMode = TextStyle(fs * 0.85f, grey, TextAnchor.MiddleCenter, false);
        }
        internal static void Panel(Rect r, string key, Color32 c, int rad)
        {
            if (fancy) GUI.Box(r, "", Bg(key + "_" + rad, c, rad * 2 + 4, rad));
            else { GUI.Box(r, ""); GUI.Box(r, ""); }
        }
        static void Pen(Rect r)
        {
            float s = Math.Min(r.width, r.height) * 0.7f; var q = new Rect(r.xMax - s - r.height * 0.15f, r.y + (r.height - s) * 0.5f, s, s);
            if (fancy) GUI.Box(q, "", Bg("pencil", default, 32, 0)); else GUI.Label(q, "/", sSmall);
        }
        static readonly Color32 CBody = new Color32(14, 15, 18, 255), CScreen = new Color32(22, 26, 32, 255), COff = new Color32(40, 46, 56, 255), COn = new Color32(36, 128, 70, 255),
            CHold = new Color32(70, 160, 100, 255), CHover = new Color32(52, 60, 72, 255), CNone = new Color32(30, 34, 40, 255), CField = new Color32(22, 26, 32, 235),
            CKey = new Color32(46, 53, 64, 255), COk = new Color32(40, 110, 200, 255), CPanel = new Color32(16, 19, 24, 250);

        public static void Draw()
        {
            if (!open) return;
            try { if (boxOnly) { if (ren != Ren.None) DrawRename(MouseGui()); } else DrawPhone(); }
            catch (Exception ex) { if (fancy) { fancy = false; ModLog.Warn("phone: drawing with textures failed (" + ex.Message + ") — plain boxes from now on"); } else if (drawWarn++ < 3) ModLog.Warn("phone: drawing: " + ex.Message); }
        }
        static int drawWarn;
        static void DrawPhone()
        {
            var L = Layout(); Styles(L.fs); var mp = MouseGui(); float now = Time.unscaledTime;
            Panel(L.body, "body", CBody, 28); Panel(L.screen, "screen", CScreen, 18);
            GUI.Label(L.clock, DateTime.Now.ToString("HH:mm"), sSmall);
            GUI.Label(L.title, RemoteComputer.Text("SC_PhoneDock_AppTitle", "Remote"), sTitle);
            Panel(L.chLeft, L.chLeft.Contains(mp) ? "hover" : "off", L.chLeft.Contains(mp) ? CHover : COff, 10); GUI.Label(L.chLeft, "<", sKey);
            Panel(L.chRight, L.chRight.Contains(mp) ? "hover" : "off", L.chRight.Contains(mp) ? CHover : COff, 10); GUI.Label(L.chRight, ">", sKey);
            var near = matching.Count > 0 ? matching[0] : null; bool modal = kpRow >= 0 || ren != Ren.None;
            // 0.31.35: "Name (ch 1)" with the "(ch 1)" smaller and dimmer; no name = "Channel 1" as before. The pencil renames it on this phone.
            string chName = ShownName(0, near);
            var chArea = new Rect(L.chText.x, L.chText.y, L.chPen.x - L.chText.x - L.fs * 0.2f, L.chText.height);
            if (chName.Length == 0) { Fit(sChan, "Channel 00", chArea.width, L.fs * 1.45f, L.fs * 0.8f); GUI.Label(chArea, RemoteComputer.Text("SC_PhoneDock_AppChannel", "Channel {0}").Replace("{0}", channel.ToString()), sChan); }
            else
            {
                string tag = "(ch " + channel + ")";
                Fit(sChan, chName + " " + tag, chArea.width * 1.12f, L.fs * 1.45f, L.fs * 0.7f);
                GUI.Label(chArea, chName + " <size=" + Math.Max(7, (int)(sChan.fontSize * 0.72f)) + "><color=" + Dim + ">" + tag + "</color></size>", sChan);
            }
            PenButton(L.chPen, !modal && L.chPen.Contains(mp));
            string status;
            if (!RemoteComputer.Host) status = RemoteComputer.Text("SC_PhoneDock_AppHostOnly", "Remote runs on the host only for now");
            else if (!openInFlight) status = matching.Count == 0 ? RemoteComputer.Text("SC_PhoneDock_AppGarageNone", "No Remote Computer on channel {0} in the garage").Replace("{0}", channel.ToString())
                                                               : RemoteComputer.Text("SC_PhoneDock_AppGarage", "Garage: start state of {0} computer(s)").Replace("{0}", matching.Count.ToString());
            else if (matching.Count == 0) status = RemoteComputer.Text("SC_PhoneDock_AppNoComputer", "No powered computer on channel {0} in range").Replace("{0}", channel.ToString());
            else status = RemoteComputer.Text("SC_PhoneDock_AppComputers", "{0} computer(s), nearest {1} m").Replace("{0}", matching.Count.ToString()).Replace("{1}", nearestM.ToString("F0"));
            var st = sSmall; if (matching.Count == 0 && now < noneFlashUntil && ((int)(now * 8f) & 1) == 0) st = sText;
            if (now < infoUntil) { status = infoText; st = sText; }   // 0.31.35: "button N is an input"
            GUI.Label(L.status, status, st);
            string sOn = RemoteComputer.Text("SC_PhoneDock_AppOn", "ON"), sOff = RemoteComputer.Text("SC_PhoneDock_AppOff", "OFF"),
                   sBtn = RemoteComputer.Text("SC_PhoneDock_AppButton", "button"), sSw = RemoteComputer.Text("SC_PhoneDock_AppSwitch", "switch");
            for (int i = 0; i < 10; i++)
            {
                var r = L.btn[i]; var row = near?.rows[i]; bool isHeld = held.ContainsKey(i + 1) && held[i + 1].button && !held[i + 1].releasing;
                bool isIn = row != null && row.input, overPen = L.pn[i].Contains(mp);
                bool hover = !modal && !isIn && r.Contains(mp) && !overPen && !(row != null && row.valXY && (L.fx[i].Contains(mp) || L.fy[i].Contains(mp)));
                int ci = isIn ? Memory.Color(channel, i + 1) : 0;
                string key = row == null ? "none" : isIn ? "in" + ci : isHeld ? "hold" : row.on ? "on" : hover ? "hover" : "off";
                Color32 c = row == null ? CNone : isIn ? Palette[ci] : isHeld ? CHold : row.on ? COn : hover ? CHover : COff;
                Panel(r, key, c, 12);
                float m = r.height * 0.1f, stateW = L.fs * 2.7f;
                // 0.31.35: the name on top with a dim "(n)"; no name = the big number as before. The pencil renames it on this phone.
                // A named button gives the whole top line to the name: its ON / OFF then goes in front of the small mode line below.
                string nm = ShownName(i + 1, near); bool named = nm.Length > 0;
                var nameArea = new Rect(r.x + m, r.y + m * 0.6f, Math.Max(10f, L.pn[i].x - (named ? 0f : stateW) - m * 0.3f - (r.x + m)), r.height * 0.4f);
                if (!named) GUI.Label(nameArea, (i + 1).ToString(), sNum);
                else
                {
                    string tag = "(" + (i + 1) + ")";
                    Fit(sName, nm + " " + tag, nameArea.width * 1.1f, L.fs * 1.15f, L.fs * 0.62f);
                    GUI.Label(new Rect(nameArea.x, nameArea.y + m * 0.4f, nameArea.width, nameArea.height), nm + " <size=" + Math.Max(7, (int)(sName.fontSize * 0.8f)) + "><color=" + (isIn ? "#e6ebf0" : Dim) + ">" + tag + "</color></size>", sName);
                }
                PenButton(L.pn[i], !modal && overPen);
                if (row == null) continue;
                var stateArea = new Rect(L.pn[i].x - stateW - m * 0.2f, r.y + m * 0.8f, stateW, r.height * 0.3f);
                if (isIn)
                {
                    // an input row: the incoming number, always lit in the colour picked on the phone; not a command button
                    if (!named) GUI.Label(stateArea, RemoteComputer.Text("SC_PhoneDock_AppIn", "IN"), sState);
                    string val = !openInFlight || row.inFrom == 0 ? "—" : FormatValue(row.inValue, Memory.Dec(channel, i + 1));
                    var vr = new Rect(r.x + m, r.y + r.height * 0.36f, r.width - 2f * m, r.height * 0.36f);
                    Fit(sVal, val, vr.width, L.fs * 2.0f, L.fs * 0.7f); GUI.Label(vr, val, sVal);
                    string src = !openInFlight ? "" : row.inFrom == 1 ? RemoteComputer.Text("SC_PhoneDock_AppInCable", "cable") : row.inFrom == 2 ? "ch " + row.ch
                               : row.inFrom == 3 ? RemoteComputer.Text("SC_PhoneDock_AppInHeld", "ch {0} (last)").Replace("{0}", row.ch.ToString()) : RemoteComputer.Text("SC_PhoneDock_AppInNone", "no signal");
                    string inMode = RemoteComputer.Text("SC_PhoneDock_AppInput", "input") + (src.Length > 0 ? " · " + src : "");
                    Fit(sLit, inMode, r.width - 2f * m, L.fs * 0.85f, L.fs * 0.55f);
                    GUI.Label(new Rect(r.x + m, r.yMax - r.height * 0.27f, r.width - 2f * m, r.height * 0.2f), inMode, sLit);
                    continue;
                }
                if (!named) GUI.Label(stateArea, row.on ? sOn : sOff, sState);
                string mode = (named ? (row.on ? sOn : sOff) + " · " : "") + (row.typSwitch ? sSw : sBtn) + " · " + (row.valXY ? "x..y" : "0..1") + (row.dstOn ? " · ch " + row.ch : "");
                Fit(sMode, mode, r.width - 2f * m, L.fs * 0.85f, L.fs * 0.55f);   // (the line shrinks to fit instead of being cut at both ends)
                if (!row.valXY) { GUI.Label(new Rect(r.x + m, r.yMax - r.height * 0.42f, r.width - 2f * m, r.height * 0.34f), mode, sMode); continue; }
                GUI.Label(new Rect(r.x + m, r.y + r.height * 0.36f, r.width - 2f * m, r.height * 0.22f), mode, sMode);
                var (x, y) = Memory.XY(channel, i + 1);
                Field(L.fx[i], "x", x, mp); Field(L.fy[i], "y", y, mp);
            }
            GUI.Label(L.footer, handMode ? RemoteComputer.Text("SC_PhoneDock_AppCloseHand", "Esc or right click: put away") : RemoteComputer.Text("SC_PhoneDock_AppClose", "Esc or E: close"), sSmall);
            if (kpRow >= 0) DrawKeypad(L, mp);
            if (ren != Ren.None) DrawRename(mp);
        }
        /// <summary>A small pencil button (rename).</summary>
        static void PenButton(Rect r, bool hover)
        {
            Panel(r, hover ? "penh" : "pen", hover ? new Color32(70, 80, 96, 235) : new Color32(0, 0, 0, 70), 5);
            float s = Math.Min(r.width, r.height) * 0.8f; var q = new Rect(r.x + (r.width - s) * 0.5f, r.y + (r.height - s) * 0.5f, s, s);
            if (fancy) GUI.Box(q, "", Bg("pencil", default, 32, 0)); else GUI.Label(q, "/", sSmall);
        }
        static void DrawRename(Vector2 mp)
        {
            var R = RenLayout(); Styles(R.fs);
            if (boxOnly) Panel(new Rect(R.panel.x - R.fs * 0.5f, R.panel.y - R.fs * 0.5f, R.panel.width + R.fs, R.panel.height + R.fs), "body", CBody, 18);
            Panel(R.panel, "panel", CPanel, 14);
            string title = ren == Ren.PhoneChannel ? RemoteComputer.Text("SC_PhoneDock_AppRenameCh", "Channel {0}: name on this phone").Replace("{0}", channel.ToString())
                         : ren == Ren.PhoneButton ? RemoteComputer.Text("SC_PhoneDock_AppRenameBtn", "Button {0}: name on this phone").Replace("{0}", renRow.ToString())
                         : ren == Ren.CompChannel ? RemoteComputer.Text("SC_RemoteComputer_RenameCh", "Computer: channel name")
                         : RemoteComputer.Text("SC_RemoteComputer_RenameRow", "Computer: row {0} name").Replace("{0}", renRow.ToString());
            GUI.Label(R.title, title, sText);
            Panel(R.field, "field", CField, 6);
            bool caret = ((int)(Time.unscaledTime * 2f) & 1) == 0;
            var fr = new Rect(R.field.x + R.fs * 0.6f, R.field.y, R.field.width - R.fs * 1.2f, R.field.height);
            Fit(sField, renText + "|", fr.width, R.fs * 1.45f, R.fs * 0.8f);
            GUI.Label(fr, renText + (caret ? "|" : ""), sField);
            string sub;
            if (ren == Ren.PhoneChannel || ren == Ren.PhoneButton)
            {
                string cn = RemoteComputer.NameOf(renComp, ren == Ren.PhoneChannel ? 0 : renRow);
                sub = cn.Length > 0 ? RemoteComputer.Text("SC_PhoneDock_AppRenameFallback", "Empty = the computer's name: {0}").Replace("{0}", cn) : RemoteComputer.Text("SC_PhoneDock_AppRenameNone", "Empty = no name");
            }
            else sub = RemoteComputer.Text("SC_RemoteComputer_RenameNote", "Phones show it unless they have their own name");
            GUI.Label(R.sub, sub + "   " + renText.Length + "/" + Names.MaxLen, sSmall);
            if (renInput)
            {
                int cur = Memory.Color(channel, renRow), dcur = Memory.Dec(channel, renRow);
                GUI.Label(R.palLabel, RemoteComputer.Text("SC_PhoneDock_AppColour", "Colour"), sSmall);
                for (int i = 0; i < Palette.Length; i++)
                {
                    var q = R.pal[i];
                    if (i == cur) Panel(new Rect(q.x - 3f, q.y - 3f, q.width + 6f, q.height + 6f), "palsel", new Color32(235, 240, 246, 255), 9);
                    Panel(q, "pal" + i, Palette[i], 7);
                }
                GUI.Label(R.decLabel, RemoteComputer.Text("SC_PhoneDock_AppDecimals", "Decimals"), sSmall);
                for (int i = 0; i <= 6; i++)
                {
                    var q = R.dec[i]; bool hov = q.Contains(mp);
                    Panel(q, i == dcur ? "ok" : hov ? "hover" : "key", i == dcur ? COk : hov ? CHover : CKey, 8);
                    GUI.Label(q, i.ToString(), sKey);
                }
            }
            bool hc = R.cancel.Contains(mp), ho = R.ok.Contains(mp);
            Panel(R.cancel, hc ? "hover" : "key", hc ? CHover : CKey, 10); GUI.Label(R.cancel, RemoteComputer.Text("SC_PhoneDock_AppCancel", "Cancel"), sKey);
            Panel(R.ok, ho ? "okh" : "ok", ho ? new Color32(60, 130, 220, 255) : COk, 10); GUI.Label(R.ok, RemoteComputer.Text("SC_PhoneDock_AppOk", "OK"), sKey);
            GUI.Label(R.help, RemoteComputer.Text("SC_PhoneDock_AppRenameHelp", "Enter: OK · Esc: cancel · Del: clear"), sSmall);
        }
        static void Field(Rect f, string name, float v, Vector2 mp)
        {
            bool hover = kpRow < 0 && ren == Ren.None && f.Contains(mp);
            Panel(f, hover ? "fieldh" : "field", hover ? CHover : CField, 6);
            GUI.Label(new Rect(f.x + f.height * 0.25f, f.y, f.width - f.height * 1.1f, f.height), name + " " + Num(v), sCenter);
            Pen(f);
        }
        static void DrawKeypad(Lay L, Vector2 mp)
        {
            Panel(L.kpPanel, "panel", CPanel, 14);
            string t = (kpY ? RemoteComputer.Text("SC_PhoneDock_AppEditY", "Button {0}: on value (y)") : RemoteComputer.Text("SC_PhoneDock_AppEditX", "Button {0}: off value (x)")).Replace("{0}", kpRow.ToString());
            GUI.Label(L.kpTitle, t, sText);
            Panel(L.kpDisplay, "field", CField, 6);
            GUI.Label(L.kpDisplay, kpText.Length == 0 ? " " : kpText, sTitle);
            for (int i = 0; i < 16; i++)
            {
                if (i == 15) continue;
                var r = L.kp[i]; bool hover = r.Contains(mp); string lab = KpLabels[i];
                if (lab == "OK") lab = RemoteComputer.Text("SC_PhoneDock_AppOk", "OK");
                Panel(r, i == 14 ? (hover ? "okh" : "ok") : hover ? "hover" : "key", i == 14 ? (hover ? new Color32(60, 130, 220, 255) : COk) : hover ? CHover : CKey, 10);
                GUI.Label(r, lab, sKey);
            }
        }
    }
}
