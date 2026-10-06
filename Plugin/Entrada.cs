using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Mecanica
{
    /// <summary>Teclado e mouse. O jogo usa o Input System novo (o Input antigo do Unity nao responde aqui), entao: Keyboard.current /
    /// Mouse.current do Input System; se isso falhar, GetAsyncKeyState do Windows pras teclas (sem delta de mouse).</summary>
    internal static class Entrada
    {
        static bool novoOk, avisou; static float tTenta = -99f; static Keyboard teclado; static Mouse mouse;
        static readonly Dictionary<int, bool> anterior = new Dictionary<int, bool>();
        public static string Modo = "?";

        /// <summary>Tenta pegar o teclado do Input System; se ainda nao existe (cedo demais), usa Win32 e tenta de novo daqui a 1 s.</summary>
        static void Preparar()
        {
            if (novoOk) return;
            float agora = Time.unscaledTime; if (agora - tTenta < 1f) return; tTenta = agora;
            try
            {
                teclado = Keyboard.current; mouse = Mouse.current;
                novoOk = teclado != null;
                Modo = novoOk ? "Input System" + (mouse != null ? " + mouse" : " (no mouse!)") : "Win32 (Input System has no keyboard)";
            }
            catch (Exception ex) { novoOk = false; Modo = "Win32 (" + ex.Message + ")"; }
            if (novoOk) Registro.Info("input: " + Modo);
            else if (!avisou) { avisou = true; Registro.Aviso("input: " + Modo + " — retrying every 1 s (without the Input System there is no mouse in the free camera)"); }
        }

        /// <summary>So quando a janela do jogo esta na frente (pra tecla em outro programa nao contar).</summary>
        static bool JanelaNaFrente()
        {
            try { GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); return pid == (uint)Environment.ProcessId; } catch (Exception) { return true; }
        }

        public static bool Segurando(Key k)
        {
            Preparar();
            if (novoOk) { try { if (teclado == null) teclado = Keyboard.current; var c = teclado[k]; return c != null && c.isPressed; } catch (Exception) { } }
            return JanelaNaFrente() && (GetAsyncKeyState(VK(k)) & 0x8000) != 0;
        }
        public static bool Apertou(Key k)
        {
            Preparar();
            if (novoOk) { try { if (teclado == null) teclado = Keyboard.current; var c = teclado[k]; return c != null && c.wasPressedThisFrame; } catch (Exception) { } }
            bool agora = Segurando(k); anterior.TryGetValue((int)k, out bool antes); anterior[(int)k] = agora;
            return agora && !antes;
        }
        public static Vector2 MouseDelta()
        {
            Preparar();
            if (novoOk)
            {
                try { if (mouse == null) mouse = Mouse.current; if (mouse != null) return mouse.delta.ReadValue(); } catch (Exception ex) { if (!avisouMouse) { avisouMouse = true; Registro.Aviso("mouse delta: " + ex.Message); } }
            }
            return Vector2.zero;
        }
        static bool avisouMouse;
        public static float Roda()
        {
            Preparar();
            if (novoOk) { try { if (mouse == null) mouse = Mouse.current; if (mouse != null) return mouse.scroll.ReadValue().y; } catch (Exception) { } }
            return 0f;
        }
        public static bool BotaoDireito()
        {
            Preparar();
            if (novoOk) { try { if (mouse == null) mouse = Mouse.current; if (mouse != null) return mouse.rightButton.isPressed; } catch (Exception) { } }
            return JanelaNaFrente() && (GetAsyncKeyState(0x02) & 0x8000) != 0;
        }

        // Key (Input System) -> codigo VK do Windows (so as teclas que o mod usa)
        internal static int VKde(Key k) => VK(k);
        static int VK(Key k)
        {
            switch (k)
            {
                case Key.Space: return 0x20; case Key.LeftShift: return 0xA0; case Key.LeftAlt: return 0xA4; case Key.LeftCtrl: return 0xA2;
                case Key.LeftArrow: return 0x25; case Key.UpArrow: return 0x26; case Key.RightArrow: return 0x27; case Key.DownArrow: return 0x28;
                case Key.Home: return 0x24; case Key.End: return 0x23; case Key.Escape: return 0x1B; case Key.Insert: return 0x2D; case Key.PageUp: return 0x21; case Key.PageDown: return 0x22; case Key.RightShift: return 0xA1;
                case Key.F1: return 0x70; case Key.F2: return 0x71; case Key.F3: return 0x72; case Key.F4: return 0x73; case Key.F5: return 0x74; case Key.F6: return 0x75;
                case Key.F7: return 0x76; case Key.F8: return 0x77; case Key.F9: return 0x78; case Key.F10: return 0x79; case Key.F11: return 0x7A; case Key.F12: return 0x7B;
                case Key.RightCtrl: return 0xA3; case Key.NumpadPlus: return 0x6B; case Key.NumpadMinus: return 0x6D; case Key.NumpadPeriod: return 0x6E; case Key.NumpadMultiply: return 0x6A; case Key.NumpadDivide: return 0x6F;
                case Key.Comma: return 0xBC; case Key.Period: return 0xBE; case Key.LeftBracket: return 0xDB; case Key.RightBracket: return 0xDD;
                default:
                    if (k >= Key.A && k <= Key.Z) return 0x41 + (k - Key.A);
                    if (k >= Key.Digit1 && k <= Key.Digit9) return 0x31 + (k - Key.Digit1);
                    if (k == Key.Digit0) return 0x30;
                    if (k >= Key.Numpad0 && k <= Key.Numpad9) return 0x60 + (k - Key.Numpad0);
                    return 0;
            }
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    }
}
