using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrapMechanics
{
    /// <summary>Remote control, round 0.31.35: typed text for the rename box (names of channels and buttons), while the game's input is off.
    /// The game itself listens to the Input System's Keyboard.onTextInput and, once per frame, refills the singleton buffer
    /// CRPInputTypedString (one char per element) with everything typed since the last frame — also while CRPInputOverride has the game's
    /// input off (research r03135_remoto_pesquisa.md, section 5). The characters come from the operating system, already composed
    /// (ç, ã, é...); Backspace arrives as char 8. The mod only READS that buffer, in the postfix of the group that refills it
    /// (CustomRenderPipelineFrameSetupGroup), so every character is taken exactly once. Nothing is read unless a rename box is open.
    /// Fallback (the group or the buffer is not found, or the postfix is never called): the mod subscribes to Keyboard.onTextInput itself
    /// while a box is open.</summary>
    internal static class TextInput
    {
        static readonly System.Text.StringBuilder pending = new System.Text.StringBuilder();
        static bool active, bufOk, bufFailed, useFallback, subscribed, noted, bufferWorked; static int openFrame = -1, lastCapture = -1, frames, deafPresses;
        static TypeIndex tiTyped; static EntityQuery qTyped; static World qWorld;
        static Key swallow = Key.None; static char swallowChar;
        static Il2CppSystem.Action<char> handler; static Action<char> managed; static Keyboard subscribedTo;
        public static bool Active => active;

        /// <summary>A rename box opened. `openedBy` = the key that opened it (its own character, and its auto-repeat while it stays down,
        /// must not land in the box); Key.None when it was opened with the mouse.</summary>
        public static void Begin(Key openedBy)
        {
            pending.Clear(); active = true; openFrame = Time.frameCount; frames = 0; lastCapture = -1; deafPresses = 0;
            swallow = openedBy; swallowChar = openedBy >= Key.A && openedBy <= Key.Z ? (char)('a' + (openedBy - Key.A)) : '\0';
            if (useFallback) Subscribe();
        }
        public static void End() { active = false; pending.Clear(); swallow = Key.None; Unsubscribe(); }

        static bool Prepare(EntityManager em)
        {
            var w = World.DefaultGameObjectInjectionWorld;
            if (bufOk && w == qWorld) return true;
            if (bufFailed) return false;
            try
            {
                tiTyped = TypeManager.GetTypeIndex(Il2CppType.Of<CRPInputTypedString>());
                var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiTyped);
                qTyped = em.CreateEntityQuery(a); qWorld = w; bufOk = true; return true;
            }
            catch (Exception ex) { bufFailed = true; ModLog.Warn("text input: the game's typed-text buffer (CRPInputTypedString) was not found: " + ex.Message); return false; }
        }
        /// <summary>Postfix of CustomRenderPipelineFrameSetupGroup (once per frame, right after the game refilled its buffer).</summary>
        public static unsafe void Capture()
        {
            if (!active || useFallback || !JointSystem.EmOk) return;
            try
            {
                var em = JointSystem.CurrentEm;
                if (!Prepare(em)) return;
                lastCapture = Time.frameCount;
                if (Time.frameCount <= openFrame) return;   // the frame that opened the box: its characters (the E that opened it) are dropped
                Entity e = Entity.Null; foreach (var x in Ecs.QueryList(qTyped)) { e = x; break; }
                if (!Ecs.HasC(em, e, tiTyped)) return;
                int len = em.GetBufferLength(e, tiTyped); if (len <= 0) return;
                var p = (char*)em.GetBufferRawRO(e, tiTyped); if (p == null) return;
                if (len > 64) len = 64;
                for (int i = 0; i < len; i++) Add(p[i]);
                bufferWorked = true;
                if (!noted) { noted = true; ModLog.Info("text input: typed characters are read from the game's own buffer (CRPInputTypedString, " + len + " char(s) in the first frame)"); }
            }
            catch (Exception ex) { if (warn++ < 3) ModLog.Warn("text input: " + ex.Message); }
        }
        static int warn;
        static void Add(char c)
        {
            if (swallow != Key.None && (swallowChar == '\0' || char.ToLowerInvariant(c) == swallowChar)) return;   // the key that opened the box is still down
            if (pending.Length < 256) pending.Append(c);
        }
        /// <summary>Main thread, once per frame while a box is open: the characters typed since the last call (char 8 = Backspace; other
        /// control characters are left for the caller to drop).</summary>
        public static string Take()
        {
            if (!active) return "";
            frames++;
            if (swallow != Key.None && !InputKeys.Holding(swallow)) swallow = Key.None;
            // self-check: the box has been open for a few frames and the group postfix was never called (the group or the buffer is missing)
            if (!useFallback && frames > 4 && (lastCapture < 0 || Time.frameCount - lastCapture > 4))
            {
                useFallback = true;
                ModLog.Info("text input: the game's typed-text buffer is not being read (" + (Ecs.PtrFrameSetup == IntPtr.Zero ? "CustomRenderPipelineFrameSetupGroup not found" : bufFailed ? "CRPInputTypedString not found" : "the group's update was not seen") + ") — listening to Keyboard.onTextInput instead");
                Subscribe();
            }
            // second self-check: the buffer is read every frame but never had a character, and keys were pressed (the game does not fill it
            // while its input is off, on this build?) — same fallback. Once the buffer delivered a character this is never tried.
            if (!useFallback && !bufferWorked && frames > 2)
            {
                bool key = false; try { var kb = Keyboard.current; key = kb != null && kb.anyKey.wasPressedThisFrame && !InputKeys.Pressed(Key.Escape) && !InputKeys.Pressed(Key.Enter) && !InputKeys.Pressed(Key.NumpadEnter); } catch (Exception) { }
                if (key && ++deafPresses >= 3)
                {
                    useFallback = true;
                    ModLog.Info("text input: 3 keys pressed and the game's typed-text buffer stayed empty — listening to Keyboard.onTextInput instead");
                    Subscribe();
                }
            }
            if (pending.Length == 0) return "";
            var s = pending.ToString(); pending.Clear(); return s;
        }
        static void OnChar(char c) { if (active && Time.frameCount > openFrame) Add(c); }
        static void Subscribe()
        {
            if (subscribed) return;
            try
            {
                var kb = Keyboard.current; if (kb == null) { if (warn++ < 3) ModLog.Warn("text input: no keyboard in the Input System — names cannot be typed"); return; }
                if (handler == null) { managed = OnChar; handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(managed); }
                kb.add_onTextInput(handler); subscribedTo = kb; subscribed = true;
            }
            catch (Exception ex) { if (warn++ < 3) ModLog.Warn("text input: could not listen to Keyboard.onTextInput: " + ex.Message); }
        }
        static void Unsubscribe()
        {
            if (!subscribed) return; subscribed = false;
            try { if (subscribedTo != null && handler != null) subscribedTo.remove_onTextInput(handler); } catch (Exception) { }
            subscribedTo = null;
        }
        /// <summary>World unloaded: the query belongs to the old world.</summary>
        public static void Forget() { bufOk = false; qWorld = null; if (active) End(); }
    }
}
