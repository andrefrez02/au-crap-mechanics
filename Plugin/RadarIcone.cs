using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>Icone proprio do Beacon no radar do jogo.
    /// Como o jogo faz: GameFeature (feature do render pipeline, um ScriptableObject) tem _radarIcons (Texture2D[], mascaras
    /// so no canal vermelho, formato R8) e no Create monta _radarIconsT2A (Texture2DArray 256x256, uma fatia por RadarIconID).
    /// Todo frame (FrameSetup) ele passa _radarIconsT2A pro shader como _RadarIconsTexture2DArray, e o shader desenha a fatia
    /// RadarLocationGPUData._type. A cor vem do shader (nao da pra trocar por icone), entao a saida e' um desenho novo.
    /// Aqui: crio um Texture2DArray com uma fatia a mais (copio as do jogo na GPU e desenho a do beacon: ponto com ondas),
    /// troco o _radarIconsT2A e o Beacon usa o indice novo. Se o jogo recriar o array (troca de qualidade grafica etc.),
    /// faco de novo.</summary>
    internal static class RadarIcone
    {
        public static int Pedido = -1;          // config: -1 = icone proprio; 0..9 = icone do jogo
        public const uint Reserva = 2;          // RadarIconID.PlanetPackage, se o proprio falhar
        static readonly System.Collections.Generic.HashSet<IntPtr> feitos = new System.Collections.Generic.HashSet<IntPtr>(); static Texture2DArray meu; static IntPtr meuPtr; static int meuIdx; static IntPtr hNovo; static float t = 9f, espera = 1f; static bool falhou; static int tentativas; static string passo = "";

        public static void Passo(float dt)
        {
            if (Pedido >= 0) { Acoplador.IconeBeacon = (uint)Pedido; return; }
            if (falhou) { Acoplador.IconeBeacon = Reserva; return; }
            t += dt; if (t < espera) return; t = 0f;
            try
            {
                passo = "find GameFeature";
                var objs = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<GameFeature>());
                if (objs == null) return;
                foreach (var o in objs)
                {
                    passo = "TryCast GameFeature";
                    var gf = o.TryCast<GameFeature>(); if (gf == null) continue;
                    passo = "read _radarIconsT2A";
                    var velho = gf._radarIconsT2A; if (velho == null) continue;
                    passo = "compare with mine";
                    if (feitos.Contains(velho.Pointer)) { Acoplador.IconeBeacon = (uint)meuIdx; continue; }
                    Montar(gf, velho);
                }
            }
            catch (Exception ex)
            {
                // 0.7.2 desistiu na 1a falha ("Object was garbage collected in IL2CPP domain", no carregamento do mundo).
                // Agora tenta 5x, de 5 em 5 s; se o meu array foi coletado, esquece ele e monta de novo.
                tentativas++; espera = 5f; meu = null; meuPtr = IntPtr.Zero; Acoplador.IconeBeacon = Reserva;
                Registro.Aviso("radar: custom beacon icon failed (attempt " + tentativas + "/5, step '" + passo + "'): " + (tentativas == 1 ? ex.ToString() : ex.Message));
                if (tentativas >= 5) { falhou = true; Registro.Aviso("radar: gave up on the custom icon, the beacon keeps icon " + Reserva + " (config Beacon.icon picks a game icon)"); }
            }
        }

        public static int Slot = 8;   // RadarIconID.ReindeerAntlers (evento): o desenho do beacon vai por cima dele

        /// <summary>0.17.1 (Andre: "checa quais estao no mundo e quais missoes usam icones, usa so os que nao estao sendo usados, e a
        /// cada R checa de novo"): o radar do jogo so tem 10 icones (RadarIconID 0..9) e todos sao do jogo. Os desenhos do beacon
        /// (ondas, carro, nave) vao por cima de icones que NINGUEM esta usando agora (nenhuma entidade com RadarAppearanceIcon daquele
        /// tipo alem dos beacons). A cada troca: a fatia liberada volta pro desenho original do jogo (copia guardada na GPU).
        /// Checa ao carregar, a cada 30 s, quando um knob muda e ao entrar/sair da simulacao (UIManager.OnSpaceshipLaunch).</summary>
        static readonly int[] Preferencia = { 8, 7, 3, 9, 5, 6, 4, 2, 1, 0 };
        static readonly int[] atual = { -1, -1, -1 };      // fatia de cada desenho (ondas, carro, nave)
        static int[] pintado;                              // fatia -> desenho que esta nela (-1 = a do jogo)
        static Texture2DArray copia; static Texture2D[] desenhos; static int W, H, Mips; static TextureFormat Fmt;
        static float tRecheck = 25f; static int ultimosPedidos = -1; static TypeIndex tiIc; static EntityQuery qIc; static bool qOk;
        public static bool PedirRecheck;
        static readonly List<float> rechecksLancamento = new List<float>(); static string motivoLancamento = "";

        [HarmonyLib.HarmonyPostfix, HarmonyLib.HarmonyPatch(typeof(UIManager), nameof(UIManager.OnSpaceshipLaunch))]
        static void AoLancar(bool start)
        {
            try
            {
                motivoLancamento = start ? "simulation started" : "back to the garage";
                Sinal.Lancado = start;   // 0.24.0: o Sinal so mexe nas portas com a simulacao rodando
                rechecksLancamento.Clear(); rechecksLancamento.Add(Time.unscaledTime + 1f); rechecksLancamento.Add(Time.unscaledTime + 5f);
            }
            catch (Exception) { }
        }

        public static string Estado()
        {
            var sb = new System.Text.StringBuilder("icon array ");
            try { sb.Append(meu == null ? "none" : (meu.depth + " slices" + (meu.Pointer == meuPtr ? "" : " (?)"))); } catch (Exception) { sb.Append("?"); }
            if (pintado != null) { sb.Append(", painted:"); for (int i = 0; i < pintado.Length; i++) if (pintado[i] >= 0) sb.Append(' ').Append(i).Append('=').Append(BeaconIcone.Nomes[pintado[i]]); }
            return sb.ToString();
        }

        /// <summary>Icone do radar pra escolha do knob do beacon (0 ondas, 1 carro, 2 nave).</summary>
        public static uint SlotDe(int k)
        {
            if (Pedido >= 0) return (uint)Pedido;
            if (falhou || pintado == null) return Reserva;
            if (k >= 0 && k < 3 && atual[k] >= 0) return (uint)atual[k];
            if (atual[0] >= 0) return (uint)atual[0];
            return Reserva;
        }

        /// <summary>Guarda uma copia do array do jogo e prepara os 3 desenhos (a atribuicao vem no Atualizar).</summary>
        static void Montar(GameFeature gf, Texture2DArray arr)
        {
            passo = "read size/format";
            W = arr.width; H = arr.height; int d = arr.depth; Mips = arr.mipmapCount; Fmt = arr.format;
            passo = "copy of the game's icons";
            copia = new Texture2DArray(W, H, d, Fmt, Mips > 1, true);
            IL2CPPSegurar(copia.Pointer);
            int mc = System.Math.Min(Mips, copia.mipmapCount);
            for (int sl = 0; sl < d; sl++) for (int m = 0; m < mc; m++) Graphics.CopyTexture(arr, sl, m, copia, sl, m);
            passo = "drawings";
            desenhos = new[] { Textura(Desenho(W, H)), Textura(DesenhoCarro(W, H)), Textura(DesenhoNave(W, H)) };
            pintado = new int[d]; for (int i = 0; i < d; i++) pintado[i] = -1;
            atual[0] = atual[1] = atual[2] = -1;
            meu = arr; meuPtr = arr.Pointer; feitos.Add(meuPtr); espera = 1f; ultimosPedidos = -1; PedirRecheck = true;
            Registro.Info("radar: " + d + " game icons (" + W + "x" + H + ", " + Fmt + ", " + Mips + " mips); the beacon's icons go over the ones nobody is using");
        }
        static void IL2CPPSegurar(IntPtr p) => segurar.Add(Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(p, false));
        static readonly List<IntPtr> segurar = new List<IntPtr>();
        static Texture2D Textura(byte[] pr)
        {
            var px = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(W * H);
            IntPtr hPx = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(px.Pointer, false);
            for (int i = 0; i < pr.Length; i++) px[i] = new Color32(pr[i], pr[i], pr[i], pr[i]);
            var t2 = new Texture2D(W, H, Fmt, Mips > 1, true); IL2CPPSegurar(t2.Pointer);
            t2.SetPixels32(px); t2.Apply(true, false);
            Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(hPx);
            return t2;
        }
        static void CopiarFatia(Texture src, int fatiaSrc, int fatia)
        {
            int mt = System.Math.Min(Mips, meu.mipmapCount);
            for (int m = 0; m < mt; m++) Graphics.CopyTexture(src, fatiaSrc, m, meu, fatia, m);
        }

        /// <summary>Chamado pelo ciclo dos beacons: pedidos = bits dos desenhos em uso (1 ondas, 2 carro, 4 nave); fantasmas = entidades
        /// dos beacons (nao contam como "em uso pelo jogo").</summary>
        public static unsafe void Atualizar(EntityManager em, int pedidos, HashSet<int> fantasmas, float dt)
        {
            if (Pedido >= 0 || falhou || pintado == null || meu == null) return;
            tRecheck += dt;
            // 0.17.2 (Andre: "quando entro na simulacao segurando R ou quando saio dela"): UIManager.OnSpaceshipLaunch (start/stop) marca
            // um recheck 1 s e 5 s depois (as entidades do mundo/objetivos ainda estao nascendo no frame do lancamento)
            bool tecla = false;
            if (rechecksLancamento.Count > 0 && Time.unscaledTime >= rechecksLancamento[0]) { rechecksLancamento.RemoveAt(0); tecla = true; }
            if (!(tecla || PedirRecheck || pedidos != ultimosPedidos || tRecheck >= 30f)) return;
            PedirRecheck = false; tRecheck = 0f; ultimosPedidos = pedidos;
            try
            {
                if (!qOk) { tiIc = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<RadarAppearanceIcon>()); var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiIc); qIc = em.CreateEntityQuery(a); qOk = true; }
                var usados = new HashSet<int>();
                foreach (var e in Ecs.Lista(qIc))
                {
                    if (fantasmas.Contains(e.Index)) continue;
                    var p = (uint*)em.GetComponentDataRawRO(e, tiIc); if (p != null && *p < (uint)pintado.Length) usados.Add((int)*p);
                }
                var novo = new int[] { -1, -1, -1 }; var ocupadas = new HashSet<int>();
                for (int k = 0; k < 3; k++)   // quem ja tinha fatia livre fica nela (nao pisca)
                    if ((pedidos & (1 << k)) != 0 && atual[k] >= 0 && !usados.Contains(atual[k])) { novo[k] = atual[k]; ocupadas.Add(atual[k]); }
                for (int k = 0; k < 3; k++)
                {
                    if ((pedidos & (1 << k)) == 0 || novo[k] >= 0) continue;
                    var ordem = new List<int>(); if (Slot >= 0 && Slot < pintado.Length) ordem.Add(Slot); ordem.AddRange(Preferencia);
                    foreach (int s in ordem) if (s < pintado.Length && !usados.Contains(s) && !ocupadas.Contains(s)) { novo[k] = s; ocupadas.Add(s); break; }
                }
                // devolve as fatias que sairam, pinta as novas
                for (int s = 0; s < pintado.Length; s++)
                {
                    int quer = -1; for (int k = 0; k < 3; k++) if (novo[k] == s) quer = k;
                    if (pintado[s] == quer) continue;
                    if (quer < 0) CopiarFatia(copia, s, s); else CopiarFatia(desenhos[quer], 0, s);
                    pintado[s] = quer;
                }
                bool mudou = novo[0] != atual[0] || novo[1] != atual[1] || novo[2] != atual[2];
                atual[0] = novo[0]; atual[1] = novo[1]; atual[2] = novo[2];
                if (mudou || tecla)
                {
                    var sb = new System.Text.StringBuilder("radar: game icons in use now: ");
                    var l = new List<int>(usados); l.Sort(); sb.Append(l.Count == 0 ? "none" : string.Join(",", l));
                    for (int k = 0; k < 3; k++) if ((pedidos & (1 << k)) != 0) sb.Append(" | ").Append(BeaconIcone.Nomes[k]).Append(" -> ").Append(atual[k] >= 0 ? "icon " + atual[k] : "NO free icon (shows " + (atual[0] >= 0 ? "the waves" : "a package") + ")");
                    Registro.Info(sb.ToString() + (tecla ? " (" + motivoLancamento + ")" : ""));
                }
            }
            catch (Exception ex) { Registro.Aviso("radar: re-check failed: " + ex.Message); }
        }

        static void Pintar(Texture2DArray arr, int fatia, int w, int h, TextureFormat fmt, int mips, byte[] pr)
        {
            var px = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(w * h);
            IntPtr hPx = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(px.Pointer, false);
            for (int i = 0; i < pr.Length; i++) px[i] = new Color32(pr[i], pr[i], pr[i], pr[i]);
            var t2 = new Texture2D(w, h, fmt, mips > 1, true);
            var hT2 = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(t2.Pointer, false);
            t2.SetPixels32(px); t2.Apply(true, false);
            int mt = System.Math.Min(System.Math.Min(mips, t2.mipmapCount), arr.mipmapCount);
            for (int m = 0; m < mt; m++) Graphics.CopyTexture(t2, 0, m, arr, fatia, m);
            Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(hT2); Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_free(hPx);
        }

        delegate bool Forma(float x, float y);
        /// <summary>Mascara com supersampling 4x4 de uma forma em -1..1 (y pra cima).</summary>
        static byte[] Rasterizar(int w, int h, Forma f)
        {
            var r = new byte[w * h]; const int S = 4;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int soma = 0;
                    for (int sj = 0; sj < S; sj++)
                        for (int si = 0; si < S; si++)
                            if (f((i + (si + 0.5f) / S) / w * 2f - 1f, (j + (sj + 0.5f) / S) / h * 2f - 1f)) soma++;
                    r[j * w + i] = (byte)(255 * soma / (S * S));
                }
            return r;
        }
        static bool Circulo(float x, float y, float cx, float cy, float r) => (x - cx) * (x - cx) + (y - cy) * (y - cy) < r * r;
        static bool Tri(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by), d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy), d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0; return !(neg && pos);
        }
        /// <summary>Carro de lado (igual o display): carroceria, cabine trapezio, 2 rodas.</summary>
        static byte[] DesenhoCarro(int w, int h) => Rasterizar(w, h, (x, y) =>
            (x > -0.86f && x < 0.86f && y > -0.18f && y < 0.2f) ||
            Tri(x, y, -0.5f, 0.2f, 0.42f, 0.2f, 0.2f, 0.56f) || Tri(x, y, -0.5f, 0.2f, 0.2f, 0.56f, -0.36f, 0.56f) ||
            Circulo(x, y, -0.5f, -0.24f, 0.24f) || Circulo(x, y, 0.5f, -0.24f, 0.24f));
        /// <summary>Nave (foguete pra cima): corpo, bico, 2 aletas.</summary>
        static byte[] DesenhoNave(int w, int h) => Rasterizar(w, h, (x, y) =>
            (x > -0.2f && x < 0.2f && y > -0.5f && y < 0.36f) || Tri(x, y, -0.2f, 0.36f, 0.2f, 0.36f, 0f, 0.88f) ||
            Tri(x, y, -0.2f, -0.5f, -0.2f, -0.05f, -0.56f, -0.62f) || Tri(x, y, 0.2f, -0.5f, 0.2f, -0.05f, 0.56f, -0.62f) ||
            (x > -0.1f && x < 0.1f && y > -0.72f && y < -0.5f));

        /// <summary>Mascara 0..255: ponto no centro com duas ondas de cada lado ((( • ))), traco grosso igual aos icones do jogo.</summary>
        static byte[] Desenho(int w, int h)
        {
            var r = new byte[w * h]; const int S = 4;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int soma = 0;
                    for (int sj = 0; sj < S; sj++)
                        for (int si = 0; si < S; si++)
                        {
                            float x = (i + (si + 0.5f) / S) / w * 2f - 1f, y = (j + (sj + 0.5f) / S) / h * 2f - 1f;
                            float rr = (float)Math.Sqrt(x * x + y * y);
                            bool dentro = rr < 0.24f;
                            if (!dentro && Math.Atan2(Math.Abs(y), Math.Abs(x)) <= 48.0 * Math.PI / 180.0)
                                dentro = Math.Abs(rr - 0.56f) < 0.095f || Math.Abs(rr - 0.88f) < 0.095f;
                            if (dentro) soma++;
                        }
                    r[j * w + i] = (byte)(255 * soma / (S * S));
                }
            return r;
        }
    }
}
