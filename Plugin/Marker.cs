using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Entities;

namespace CrapMechanics
{
    /// <summary>0.7.12: F8 = "mark an error". At the moment of F8: a very visible line in the session log with the exact time + snapshot of the state
    /// of all joints + screenshot (logs/marks). Then it opens a small Windows window on top of the game for Andre to
    /// write what he saw, with the "send with screenshot" box. The text goes into the log with the mark's number. An unchecked screenshot goes
    /// to logs/_old (nothing is deleted).
    /// (The game's IMGUI is almost entirely stripped from the build — GUI.Label/Button(string) don't even exist — so the window is Win32.)</summary>
    internal static class Marker
    {
        public static string LogsFolder;
        public const string ModName = "Crap Mechanics";
        static int vkMark = 0x77; static string markName = "", cursorName = "";
        /// <summary>0.12.12: F8 and the turret's cursor key come from the options menu (Mod Menu library) if installed.</summary>
        internal static void ForgetKeys() { markName = ""; cursorName = ""; }
        static void RefreshKeys()
        {
            string m = KeybindsLink.Key(ModName, "mark_error", CrapMechanicsPlugin.MarkKey != null ? CrapMechanicsPlugin.MarkKey.Value : "");
            if (m != markName)
            {
                markName = m;
                if (string.IsNullOrWhiteSpace(m)) vkMark = 0;
                else if (System.Enum.TryParse<UnityEngine.InputSystem.Key>(m.Trim(), true, out var k) && InputKeys.VKOf(k) > 0) vkMark = InputKeys.VKOf(k);
                else { ModLog.Warn("mark: key '" + m + "' can't be used to mark errors; keeping the previous value"); }
            }
            string c = KeybindsLink.Key(ModName, "turret_cursor", null);
            if (c != null && c != cursorName)
            {
                cursorName = c;
                if (System.Enum.TryParse<UnityEngine.InputSystem.Key>(c.Trim(), true, out var kc)) JointSystem.TurretCursorKey = kc;
                else if (string.IsNullOrWhiteSpace(c)) JointSystem.TurretCursorKey = UnityEngine.InputSystem.Key.None;
            }
        }
        static int number; static bool keyBefore; static volatile bool windowOpen;
        static readonly ConcurrentQueue<Action> main = new ConcurrentQueue<Action>();

        public static void Step(EntityManager em)
        {
            while (main.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { ModLog.Warn("mark: " + ex.Message); } }
            RefreshKeys();
            bool keyCode = vkMark > 0 && (GetAsyncKeyState(vkMark) & 0x8000) != 0;
            bool pressed = keyCode && !keyBefore; keyBefore = keyCode;
            if (!pressed || windowOpen) return;
            IntPtr game = GetForegroundWindow();
            GetWindowThreadProcessId(game, out uint pid);
            if (pid != (uint)Environment.ProcessId) return;   // F8 in another program: ignore
            Mark(em, game);
        }

        static void Mark(EntityManager em, IntPtr game)
        {
            int n = ++number; var now = DateTime.Now; string instant = now.ToString("HH:mm:ss.fff");
            string baseName = "mark-" + now.ToString("yyyyMMdd-HHmmss") + "-" + n;
            string folder = Path.Combine(LogsFolder ?? ".", "marks"); try { Directory.CreateDirectory(folder); } catch (Exception) { }
            string state; try { state = JointSystem.State(em); } catch (Exception ex) { state = "  (could not read: " + ex.Message + ")\n"; }
            string coup; try { coup = Couplers.State(em); } catch (Exception ex) { coup = "  (could not read: " + ex.Message + ")\n"; }
            ModLog.Warn("===== ERROR MARK #" + n + " — mark key pressed at " + instant + " =====\njoint state at this moment:\n" + state + "recouplers/beacons:\n" + coup + "===== (the note follows in a line 'MARK #" + n + " note') =====");

            // screenshot: capture now, before the window opens on top
            string print = null;
            try
            {
                if (GetClientRect(game, out var r) && r.right > r.left && r.bottom > r.top)
                {
                    var p = new POINT { x = 0, y = 0 }; ClientToScreen(game, ref p);
                    int w = r.right - r.left, h = r.bottom - r.top;
                    var px = Capture(p.x, p.y, w, h);
                    if (px != null)
                    {
                        print = Path.Combine(folder, baseName + ".png");
                        string dest = print;
                        new Thread(() => { try { File.WriteAllBytes(dest, Png(px, w, h)); } catch (Exception ex) { main.Enqueue(() => ModLog.Warn("mark #" + n + ": saving the screenshot failed: " + ex.Message)); } }) { IsBackground = true }.Start();
                    }
                }
            }
            catch (Exception ex) { ModLog.Warn("mark #" + n + ": screenshot failed: " + ex.Message); print = null; }

            string txt = Path.Combine(folder, baseName + ".txt");
            try { File.WriteAllText(txt, "ERROR MARK #" + n + " — at " + instant + "\n\njoint state:\n" + state + "\nrecouplers/beacons:\n" + coup); } catch (Exception) { }

            windowOpen = true;
            string printFinal = print;
            var t = new Thread(() =>
            {
                string text = null; bool withPrint = true, ok = false;
                try { ok = Window.Show("Mark error #" + n + " (" + instant + ")", printFinal != null, out text, out withPrint); }
                catch (Exception ex) { main.Enqueue(() => ModLog.Warn("mark #" + n + ": window failed: " + ex.Message)); }
                main.Enqueue(() => Finish(n, instant, ok, text, withPrint, printFinal, txt));
                windowOpen = false;
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        static void Finish(int n, string instant, bool ok, string text, bool withPrint, string print, string txt)
        {
            text = (text ?? "").Replace("\r\n", "\n").Trim();
            if (print != null && (!ok || !withPrint))
            {
                // no screenshot: move to _old (project rule: don't delete). Wait for the PNG to finish writing.
                string origin = print;
                new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 50 && !File.Exists(origin); i++) Thread.Sleep(100);
                        Thread.Sleep(300);
                        var trash = Path.Combine(LogsFolder, "_old"); Directory.CreateDirectory(trash);
                        var d = Path.Combine(trash, Path.GetFileName(origin)); if (!File.Exists(d)) File.Move(origin, d);
                    }
                    catch (Exception) { }
                }) { IsBackground = true }.Start();
            }
            string overPrint = print == null ? "no screenshot (capture failed)" : ok && withPrint ? "screenshot: marks/" + Path.GetFileName(print) : "no screenshot (unchecked)";
            string line = ok ? "MARK #" + n + " note (at " + instant + "): " + (text.Length == 0 ? "(empty)" : text) + " — " + overPrint
                              : "MARK #" + n + " (at " + instant + "): window cancelled — the mark and the state stay in the log, " + overPrint;
            ModLog.Warn(line);
            try { File.AppendAllText(txt, "\n" + line + "\n"); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- capture (GDI) and PNG
        static byte[] Capture(int x, int y, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen); bmp = CreateCompatibleBitmap(screen, w, h); old = SelectObject(mem, bmp);
                if (!BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000)) return null;   // SRCCOPY | CAPTUREBLT
                SelectObject(mem, old); old = IntPtr.Zero;
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
                var px = new byte[w * h * 4];
                if (GetDIBits(mem, bmp, 0, (uint)h, px, ref bi, 0) == 0) return null;
                return px;   // BGRA, top to bottom
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        static byte[] Png(byte[] bgra, int w, int h)
        {
            var raw = new byte[(w * 3 + 1) * h];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                raw[o++] = 0;
                int i = y * w * 4;
                for (int x = 0; x < w; x++, i += 4) { raw[o++] = bgra[i + 2]; raw[o++] = bgra[i + 1]; raw[o++] = bgra[i]; }
            }
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var d = new DeflateStream(ms, CompressionLevel.Fastest, true)) d.Write(raw, 0, raw.Length);
                uint a = 1, b = 0; foreach (var c in raw) { a = (a + c) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                compressed = ms.ToArray();
            }
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13]; BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Piece(png, "IHDR", ihdr); Piece(png, "IDAT", compressed); Piece(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }
        static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static uint[] crcTable;
        static void Piece(Stream s, string kind, byte[] data)
        {
            if (crcTable == null) { crcTable = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; crcTable[n] = c; } }
            var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(kind); s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFFu;
            foreach (var x in t) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (var x in data) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            crc ^= 0xFFFFFFFFu; var cb = new byte[4]; BE(cb, 0, crc); s.Write(cb, 0, 4);
        }

        // ---------------------------------------------------------------- Win32
        [StructLayout(LayoutKind.Sequential)] struct RECTANGLE { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECTANGLE r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    }

    /// <summary>Small Win32 window (its own thread): text box, "send with screenshot", Send / Cancel. Enter = new line;
    /// Ctrl+Enter = Send; Esc = Cancel.</summary>
    internal static class Window
    {
        const int ID_TEXT = 101, ID_PRINT = 102, ID_OK = 1, ID_CANCEL = 2;
        static IntPtr hText, hPrint, hWindow; static bool result; static string text; static bool withPrint;
        static WndProc proc;   // live reference (otherwise the GC collects the delegate)
        static bool registered;

        public static bool Show(string title, bool hasPrint, out string textOut, out bool printOut)
        {
            result = false; text = ""; withPrint = hasPrint;
            IntPtr inst = GetModuleHandle(null);
            if (!registered)
            {
                proc = Proc;
                var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, hCursor = LoadCursor(IntPtr.Zero, 32512), hbrBackground = (IntPtr)(15 + 1), lpszClassName = "CrapMechanicsMark" };
                if (RegisterClassEx(ref wc) == 0) throw new Exception("RegisterClassEx " + Marshal.GetLastWin32Error());
                registered = true;
            }
            int W = 520, H = 300;
            int sx = GetSystemMetrics(0), sy = GetSystemMetrics(1);
            hWindow = CreateWindowEx(0x00000008 /*TOPMOST*/ | 0x00000001 /*DLGMODALFRAME*/, "CrapMechanicsMark", title, 0x00C00000 | 0x00080000 | 0x10000000 /*CAPTION|SYSMENU|VISIBLE*/, (sx - W) / 2, (sy - H) / 2, W, H, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hWindow == IntPtr.Zero) throw new Exception("CreateWindowEx " + Marshal.GetLastWin32Error());
            IntPtr source = GetStockObject(17);
            IntPtr rot = CreateWindowEx(0, "STATIC", "What happened? (Ctrl+Enter sends, Esc cancels)", 0x50000000, 12, 10, 480, 20, hWindow, IntPtr.Zero, inst, IntPtr.Zero);
            hText = CreateWindowEx(0x00000200 /*CLIENTEDGE*/, "EDIT", "", 0x50000000 | 0x00010000 /*TABSTOP*/ | 0x0004 /*MULTILINE*/ | 0x0040 /*AUTOVSCROLL*/ | 0x1000 /*WANTRETURN*/ | 0x00200000 /*VSCROLL*/, 12, 34, 480, 150, hWindow, (IntPtr)ID_TEXT, inst, IntPtr.Zero);
            hPrint = CreateWindowEx(0, "BUTTON", hasPrint ? "Send with screenshot" : "Send with screenshot (capture failed)", 0x50000000 | 0x00010000 | 0x0003 /*AUTOCHECKBOX*/, 12, 194, 300, 22, hWindow, (IntPtr)ID_PRINT, inst, IntPtr.Zero);
            IntPtr ok = CreateWindowEx(0, "BUTTON", "Send", 0x50000000 | 0x00010000 | 0x0001 /*DEFPUSHBUTTON*/, 300, 226, 92, 28, hWindow, (IntPtr)ID_OK, inst, IntPtr.Zero);
            IntPtr cancel = CreateWindowEx(0, "BUTTON", "Cancel", 0x50000000 | 0x00010000, 400, 226, 92, 28, hWindow, (IntPtr)ID_CANCEL, inst, IntPtr.Zero);
            foreach (var h in new[] { rot, hText, hPrint, ok, cancel }) SendMessage(h, 0x0030 /*WM_SETFONT*/, source, (IntPtr)1);
            SendMessage(hPrint, 0x00F1 /*BM_SETCHECK*/, (IntPtr)(hasPrint ? 1 : 0), IntPtr.Zero);
            if (!hasPrint) EnableWindow(hPrint, false);
            ShowWindow(hWindow, 5); SetForegroundWindow(hWindow); SetFocus(hText);
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == 0x0100 /*KEYDOWN*/ && (int)m.wParam == 0x0D && (GetKeyState(0x11) & 0x8000) != 0) { Close(true); continue; }
                if (m.message == 0x0100 && (int)m.wParam == 0x1B) { Close(false); continue; }
                if (!IsDialogMessage(hWindow, ref m)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            }
            textOut = text; printOut = withPrint;
            return result;
        }

        static void Close(bool ok)
        {
            if (hWindow == IntPtr.Zero) return;
            if (ok)
            {
                int n = GetWindowTextLength(hText); var sb = new System.Text.StringBuilder(n + 2); GetWindowText(hText, sb, n + 1); text = sb.ToString();
                withPrint = SendMessage(hPrint, 0x00F0 /*BM_GETCHECK*/, IntPtr.Zero, IntPtr.Zero) == (IntPtr)1;
            }
            result = ok;
            var h = hWindow; hWindow = IntPtr.Zero; DestroyWindow(h);
        }

        static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            switch (msg)
            {
                case 0x0111: /*WM_COMMAND*/
                    int id = (int)((long)w & 0xFFFF);
                    if (id == ID_OK) { Close(true); return IntPtr.Zero; }
                    if (id == ID_CANCEL) { Close(false); return IntPtr.Zero; }
                    break;
                case 0x0010: /*WM_CLOSE*/ Close(false); return IntPtr.Zero;
                case 0x0002: /*WM_DESTROY*/ PostQuitMessage(0); return IntPtr.Zero;
            }
            return DefWindowProc(h, msg, w, l);
        }

        delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX { public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool IsDialogMessage(IntPtr h, ref MSG m);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern void PostQuitMessage(int c);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr h, bool e);
        [DllImport("user32.dll")] static extern short GetKeyState(int vk);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadCursor(IntPtr inst, int id);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    }
}
