using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>0.7.12: F8 = "marcar erro". Na hora do F8: linha bem visivel no log da sessao com o horario exato + foto do estado
    /// de todas as articulacoes + print da tela (logs/marcas). Depois abre uma janelinha do Windows por cima do jogo pro Andre
    /// escrever o que viu, com a caixa "enviar com print". O texto entra no log com o numero da marca. Print desmarcado vai
    /// pra logs/_lixeira (nada e' apagado).
    /// (IMGUI do jogo esta quase todo removido no build — GUI.Label/Button(string) nem existem — por isso a janela e' Win32.)</summary>
    internal static class Marca
    {
        public static string PastaLogs;
        public const string ModNome = "Crap Mechanics";
        static int vkMarca = 0x77; static string nomeMarca = "", nomeCursor = "";
        /// <summary>0.12.12: F8 e a tecla do cursor do turret vem do menu de opcoes (lib Mod Menu) se instalada.</summary>
        internal static void EsquecerTeclas() { nomeMarca = ""; nomeCursor = ""; }
        static void AtualizarTeclas()
        {
            string m = KeybindsLink.Key(ModNome, "mark_error", MecanicaPlugin.TeclaMarca != null ? MecanicaPlugin.TeclaMarca.Value : "");
            if (m != nomeMarca)
            {
                nomeMarca = m;
                if (string.IsNullOrWhiteSpace(m)) vkMarca = 0;
                else if (System.Enum.TryParse<UnityEngine.InputSystem.Key>(m.Trim(), true, out var k) && Entrada.VKde(k) > 0) vkMarca = Entrada.VKde(k);
                else { Registro.Aviso("mark: key '" + m + "' can't be used to mark errors; keeping the previous value"); }
            }
            string c = KeybindsLink.Key(ModNome, "turret_cursor", null);
            if (c != null && c != nomeCursor)
            {
                nomeCursor = c;
                if (System.Enum.TryParse<UnityEngine.InputSystem.Key>(c.Trim(), true, out var kc)) Juntas.TorretaTeclaCursor = kc;
                else if (string.IsNullOrWhiteSpace(c)) Juntas.TorretaTeclaCursor = UnityEngine.InputSystem.Key.None;
            }
        }
        static int numero; static bool teclaAntes; static volatile bool janelaAberta;
        static readonly ConcurrentQueue<Action> principal = new ConcurrentQueue<Action>();

        public static void Passo(EntityManager em)
        {
            while (principal.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { Registro.Aviso("mark: " + ex.Message); } }
            AtualizarTeclas();
            bool tecla = vkMarca > 0 && (GetAsyncKeyState(vkMarca) & 0x8000) != 0;
            bool apertou = tecla && !teclaAntes; teclaAntes = tecla;
            if (!apertou || janelaAberta) return;
            IntPtr jogo = GetForegroundWindow();
            GetWindowThreadProcessId(jogo, out uint pid);
            if (pid != (uint)Environment.ProcessId) return;   // F8 em outro programa: ignora
            Marcar(em, jogo);
        }

        static void Marcar(EntityManager em, IntPtr jogo)
        {
            int n = ++numero; var agora = DateTime.Now; string hora = agora.ToString("HH:mm:ss.fff");
            string nomeBase = "mark-" + agora.ToString("yyyyMMdd-HHmmss") + "-" + n;
            string pasta = Path.Combine(PastaLogs ?? ".", "marks"); try { Directory.CreateDirectory(pasta); } catch (Exception) { }
            string estado; try { estado = Juntas.Estado(em); } catch (Exception ex) { estado = "  (could not read: " + ex.Message + ")\n"; }
            string acop; try { acop = Acoplador.Estado(em); } catch (Exception ex) { acop = "  (could not read: " + ex.Message + ")\n"; }
            Registro.Aviso("===== ERROR MARK #" + n + " — mark key pressed at " + hora + " =====\njoint state at this moment:\n" + estado + "recouplers/beacons:\n" + acop + "===== (the note follows in a line 'MARK #" + n + " note') =====");

            // print: captura ja, antes da janela abrir por cima
            string print = null;
            try
            {
                if (GetClientRect(jogo, out var r) && r.right > r.left && r.bottom > r.top)
                {
                    var p = new PONTO { x = 0, y = 0 }; ClientToScreen(jogo, ref p);
                    int w = r.right - r.left, h = r.bottom - r.top;
                    var px = Capturar(p.x, p.y, w, h);
                    if (px != null)
                    {
                        print = Path.Combine(pasta, nomeBase + ".png");
                        string destino = print;
                        new Thread(() => { try { File.WriteAllBytes(destino, Png(px, w, h)); } catch (Exception ex) { principal.Enqueue(() => Registro.Aviso("mark #" + n + ": saving the screenshot failed: " + ex.Message)); } }) { IsBackground = true }.Start();
                    }
                }
            }
            catch (Exception ex) { Registro.Aviso("mark #" + n + ": screenshot failed: " + ex.Message); print = null; }

            string txt = Path.Combine(pasta, nomeBase + ".txt");
            try { File.WriteAllText(txt, "ERROR MARK #" + n + " — at " + hora + "\n\njoint state:\n" + estado + "\nrecouplers/beacons:\n" + acop); } catch (Exception) { }

            janelaAberta = true;
            string printFinal = print;
            var t = new Thread(() =>
            {
                string texto = null; bool comPrint = true, ok = false;
                try { ok = Janela.Mostrar("Mark error #" + n + " (" + hora + ")", printFinal != null, out texto, out comPrint); }
                catch (Exception ex) { principal.Enqueue(() => Registro.Aviso("mark #" + n + ": window failed: " + ex.Message)); }
                principal.Enqueue(() => Concluir(n, hora, ok, texto, comPrint, printFinal, txt));
                janelaAberta = false;
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        static void Concluir(int n, string hora, bool ok, string texto, bool comPrint, string print, string txt)
        {
            texto = (texto ?? "").Replace("\r\n", "\n").Trim();
            if (print != null && (!ok || !comPrint))
            {
                // sem print: move pra _lixeira (regra do projeto: nao apagar). Espera o PNG terminar de gravar.
                string origem = print;
                new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 50 && !File.Exists(origem); i++) Thread.Sleep(100);
                        Thread.Sleep(300);
                        var lixo = Path.Combine(PastaLogs, "_old"); Directory.CreateDirectory(lixo);
                        var d = Path.Combine(lixo, Path.GetFileName(origem)); if (!File.Exists(d)) File.Move(origem, d);
                    }
                    catch (Exception) { }
                }) { IsBackground = true }.Start();
            }
            string sobrePrint = print == null ? "no screenshot (capture failed)" : ok && comPrint ? "screenshot: marks/" + Path.GetFileName(print) : "no screenshot (unchecked)";
            string linha = ok ? "MARK #" + n + " note (at " + hora + "): " + (texto.Length == 0 ? "(empty)" : texto) + " — " + sobrePrint
                              : "MARK #" + n + " (at " + hora + "): window cancelled — the mark and the state stay in the log, " + sobrePrint;
            Registro.Aviso(linha);
            try { File.AppendAllText(txt, "\n" + linha + "\n"); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- captura (GDI) e PNG
        static byte[] Capturar(int x, int y, int w, int h)
        {
            IntPtr tela = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, velho = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(tela); bmp = CreateCompatibleBitmap(tela, w, h); velho = SelectObject(mem, bmp);
                if (!BitBlt(mem, 0, 0, w, h, tela, x, y, 0x00CC0020 | 0x40000000)) return null;   // SRCCOPY | CAPTUREBLT
                SelectObject(mem, velho); velho = IntPtr.Zero;
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
                var px = new byte[w * h * 4];
                if (GetDIBits(mem, bmp, 0, (uint)h, px, ref bi, 0) == 0) return null;
                return px;   // BGRA, de cima pra baixo
            }
            finally
            {
                if (velho != IntPtr.Zero) SelectObject(mem, velho);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, tela);
            }
        }

        static byte[] Png(byte[] bgra, int w, int h)
        {
            var cru = new byte[(w * 3 + 1) * h];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                cru[o++] = 0;
                int i = y * w * 4;
                for (int x = 0; x < w; x++, i += 4) { cru[o++] = bgra[i + 2]; cru[o++] = bgra[i + 1]; cru[o++] = bgra[i]; }
            }
            byte[] comprimido;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var d = new DeflateStream(ms, CompressionLevel.Fastest, true)) d.Write(cru, 0, cru.Length);
                uint a = 1, b = 0; foreach (var c in cru) { a = (a + c) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                comprimido = ms.ToArray();
            }
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13]; BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Pedaco(png, "IHDR", ihdr); Pedaco(png, "IDAT", comprimido); Pedaco(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }
        static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static uint[] tabelaCrc;
        static void Pedaco(Stream s, string tipo, byte[] dados)
        {
            if (tabelaCrc == null) { tabelaCrc = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; tabelaCrc[n] = c; } }
            var len = new byte[4]; BE(len, 0, (uint)dados.Length); s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(tipo); s.Write(t, 0, 4); s.Write(dados, 0, dados.Length);
            uint crc = 0xFFFFFFFFu;
            foreach (var x in t) crc = tabelaCrc[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (var x in dados) crc = tabelaCrc[(crc ^ x) & 0xFF] ^ (crc >> 8);
            crc ^= 0xFFFFFFFFu; var cb = new byte[4]; BE(cb, 0, crc); s.Write(cb, 0, 4);
        }

        // ---------------------------------------------------------------- Win32
        [StructLayout(LayoutKind.Sequential)] struct RETANGULO { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] struct PONTO { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RETANGULO r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref PONTO p);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint linhas, byte[] bits, ref BITMAPINFOHEADER bi, uint uso);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    }

    /// <summary>Janelinha Win32 (thread propria): caixa de texto, "enviar com print", Enviar / Cancelar. Enter = nova linha;
    /// Ctrl+Enter = Enviar; Esc = Cancelar.</summary>
    internal static class Janela
    {
        const int ID_TEXTO = 101, ID_PRINT = 102, ID_OK = 1, ID_CANCELA = 2;
        static IntPtr hTexto, hPrint, hJanela; static bool resultado; static string texto; static bool comPrint;
        static WndProc proc;   // referencia viva (senao o GC recolhe o delegate)
        static bool registrada;

        public static bool Mostrar(string titulo, bool temPrint, out string textoOut, out bool printOut)
        {
            resultado = false; texto = ""; comPrint = temPrint;
            IntPtr inst = GetModuleHandle(null);
            if (!registrada)
            {
                proc = Proc;
                var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = inst, hCursor = LoadCursor(IntPtr.Zero, 32512), hbrBackground = (IntPtr)(15 + 1), lpszClassName = "MecanicaMarca" };
                if (RegisterClassEx(ref wc) == 0) throw new Exception("RegisterClassEx " + Marshal.GetLastWin32Error());
                registrada = true;
            }
            int W = 520, H = 300;
            int sx = GetSystemMetrics(0), sy = GetSystemMetrics(1);
            hJanela = CreateWindowEx(0x00000008 /*TOPMOST*/ | 0x00000001 /*DLGMODALFRAME*/, "MecanicaMarca", titulo, 0x00C00000 | 0x00080000 | 0x10000000 /*CAPTION|SYSMENU|VISIBLE*/, (sx - W) / 2, (sy - H) / 2, W, H, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hJanela == IntPtr.Zero) throw new Exception("CreateWindowEx " + Marshal.GetLastWin32Error());
            IntPtr fonte = GetStockObject(17);
            IntPtr rot = CreateWindowEx(0, "STATIC", "What happened? (Ctrl+Enter sends, Esc cancels)", 0x50000000, 12, 10, 480, 20, hJanela, IntPtr.Zero, inst, IntPtr.Zero);
            hTexto = CreateWindowEx(0x00000200 /*CLIENTEDGE*/, "EDIT", "", 0x50000000 | 0x00010000 /*TABSTOP*/ | 0x0004 /*MULTILINE*/ | 0x0040 /*AUTOVSCROLL*/ | 0x1000 /*WANTRETURN*/ | 0x00200000 /*VSCROLL*/, 12, 34, 480, 150, hJanela, (IntPtr)ID_TEXTO, inst, IntPtr.Zero);
            hPrint = CreateWindowEx(0, "BUTTON", temPrint ? "Send with screenshot" : "Send with screenshot (capture failed)", 0x50000000 | 0x00010000 | 0x0003 /*AUTOCHECKBOX*/, 12, 194, 300, 22, hJanela, (IntPtr)ID_PRINT, inst, IntPtr.Zero);
            IntPtr ok = CreateWindowEx(0, "BUTTON", "Send", 0x50000000 | 0x00010000 | 0x0001 /*DEFPUSHBUTTON*/, 300, 226, 92, 28, hJanela, (IntPtr)ID_OK, inst, IntPtr.Zero);
            IntPtr cancela = CreateWindowEx(0, "BUTTON", "Cancel", 0x50000000 | 0x00010000, 400, 226, 92, 28, hJanela, (IntPtr)ID_CANCELA, inst, IntPtr.Zero);
            foreach (var h in new[] { rot, hTexto, hPrint, ok, cancela }) SendMessage(h, 0x0030 /*WM_SETFONT*/, fonte, (IntPtr)1);
            SendMessage(hPrint, 0x00F1 /*BM_SETCHECK*/, (IntPtr)(temPrint ? 1 : 0), IntPtr.Zero);
            if (!temPrint) EnableWindow(hPrint, false);
            ShowWindow(hJanela, 5); SetForegroundWindow(hJanela); SetFocus(hTexto);
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == 0x0100 /*KEYDOWN*/ && (int)m.wParam == 0x0D && (GetKeyState(0x11) & 0x8000) != 0) { Fechar(true); continue; }
                if (m.message == 0x0100 && (int)m.wParam == 0x1B) { Fechar(false); continue; }
                if (!IsDialogMessage(hJanela, ref m)) { TranslateMessage(ref m); DispatchMessage(ref m); }
            }
            textoOut = texto; printOut = comPrint;
            return resultado;
        }

        static void Fechar(bool ok)
        {
            if (hJanela == IntPtr.Zero) return;
            if (ok)
            {
                int n = GetWindowTextLength(hTexto); var sb = new System.Text.StringBuilder(n + 2); GetWindowText(hTexto, sb, n + 1); texto = sb.ToString();
                comPrint = SendMessage(hPrint, 0x00F0 /*BM_GETCHECK*/, IntPtr.Zero, IntPtr.Zero) == (IntPtr)1;
            }
            resultado = ok;
            var h = hJanela; hJanela = IntPtr.Zero; DestroyWindow(h);
        }

        static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            switch (msg)
            {
                case 0x0111: /*WM_COMMAND*/
                    int id = (int)((long)w & 0xFFFF);
                    if (id == ID_OK) { Fechar(true); return IntPtr.Zero; }
                    if (id == ID_CANCELA) { Fechar(false); return IntPtr.Zero; }
                    break;
                case 0x0010: /*WM_CLOSE*/ Fechar(false); return IntPtr.Zero;
                case 0x0002: /*WM_DESTROY*/ PostQuitMessage(0); return IntPtr.Zero;
            }
            return DefWindowProc(h, msg, w, l);
        }

        delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX { public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string nome, uint estilo, int x, int y, int w, int h, IntPtr pai, IntPtr menu, IntPtr inst, IntPtr p);
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string nome);
    }
}
