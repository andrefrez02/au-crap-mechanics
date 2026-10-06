using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mecanica
{
    /// <summary>0.12.6 (Andre): duas versoes do turret pra testar, e um modo de ajuste com o jogo rodando.
    /// Versao A (banco_invisivel = true): sentado no turret, os renderers do banco do jogo (Upper, Upper Paintable, Piston) vao pra
    /// camada CRPLayer.MyPlayer (4) — a camera principal nao desenha essa camada (e' o que esconde o proprio corpo); camera de bloco e a
    /// camera livre do Replay continuam mostrando. Ao levantar/trocar de versao, a camada volta.
    /// Modo CABINE (padrao, 0.12.7): banco visivel e o boneco do jogador gira junto com o desenho (Torreta.GirarJogador) — a camera e' filha dele.
    /// Versao B (false): banco visivel e voce senta em jogador_posicao_b (ActionableSeat._playerTransform.pos reescrito, relativo ao Upper).
    /// Modo ajuste (so sentado no turret): Num/ liga; Num* A/B; Num1 banco, Num3 jogador; Num4/6 x, Num8/2 y, Num7/9 z (Shift x5);
    /// Num5 volta o alvo pro padrao. Cada mudanca vai pro log e pro cfg.</summary>
    internal static partial class Juntas
    {
        // 0.12.7: modo do banco — 0 CABINE (padrao: banco visivel e VOCE gira junto com ele — a camera vai junto, nada atravessa a visao),
        // 1 A (banco invisivel pra voce), 2 B (banco visivel, voce senta em jogador_posicao_b)
        public static int ModoBanco; public static bool Ajuste; static int alvoAjuste;   // alvo 0 banco, 1 jogador
        public static bool BancoInvisivel => ModoBanco == 1;
        static string NomeModo(int m) => m == 0 ? "cockpit" : m == 1 ? "A" : "B";
        public static readonly float3 VisaoPadrao = new float3 { x = 0f, y = 0.42f, z = -0.46f };   // 0.12.9: 7 cm mais alta e 6 cm mais pra tras (Andre), ~6 cm do tubo do arco de tras
        public static bool VisaoGira = true;
        public static float3 BancoOff, JogadorB, VisaoA = VisaoPadrao;
        static TypeIndex tiLayer, tiActSeat; static bool layerOk, layerFalhou;
        static readonly Dictionary<long, int> camadaOriginal = new Dictionary<long, int>();
        static readonly Dictionary<long, float3> jogadorOriginal = new Dictionary<long, float3>();
        static Entity escondidoBloco; static bool escondidoAtivo;

        public static void CarregarAjuste(string modo, string banco, string jogador, string visao)
        {
            VisaoA = Ler3(visao, VisaoA);
            var m = (modo ?? "").Trim().ToLowerInvariant(); ModoBanco = m == "a" ? 1 : m == "b" ? 2 : 0; BancoOff = Ler3(banco, float3.zero); JogadorB = Ler3(jogador, float3.zero);
        }
        /// <summary>0.12.9: a visao (girando com os aros) esta a menos de ~4 cm do tubo do arco de tras (elipse y 1,045 x z 0,62, tubo 0,055)?</summary>
        static bool ArcoPerto(float3 v)
        {
            if (v.z > 0f) return false;
            float r = (float)System.Math.Sqrt(v.y * v.y + v.z * v.z); if (r < 1e-4f) return false;
            float s2 = v.y * v.y / (r * r), c2 = v.z * v.z / (r * r);
            float rArco = 1f / (float)System.Math.Sqrt(s2 / (1.045f * 1.045f) + c2 / (0.62f * 0.62f));
            return rArco - r < 0.055f + 0.04f;
        }
        internal static float3 Ler3(string s, float3 padrao)
        {
            try
            {
                var p = s.Split(';'); if (p.Length != 3) return padrao;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return new float3 { x = float.Parse(p[0].Trim().Replace(',', '.'), ci), y = float.Parse(p[1].Trim().Replace(',', '.'), ci), z = float.Parse(p[2].Trim().Replace(',', '.'), ci) };
            }
            catch (Exception) { return padrao; }
        }
        internal static string Esc3(float3 v) { var ci = System.Globalization.CultureInfo.InvariantCulture; return v.x.ToString("0.###", ci) + ";" + v.y.ToString("0.###", ci) + ";" + v.z.ToString("0.###", ci); }
        static void Salvar()
        {
            try
            {
                MecanicaPlugin.TorretaModoBanco.Value = NomeModo(ModoBanco);
                MecanicaPlugin.TorretaBancoAjuste.Value = Esc3(BancoOff);
                MecanicaPlugin.TorretaJogadorB.Value = Esc3(JogadorB);
                MecanicaPlugin.TorretaVisaoA.Value = Esc3(VisaoA);
            }
            catch (Exception ex) { Registro.Aviso("turret adjust: saving to cfg: " + ex.Message); }
        }
        static string Resumo() => "mode " + (ModoBanco == 0 ? "COCKPIT (you turn with the seat)" : ModoBanco == 1 ? "A (seat invisible to you)" : "B (seat visible, you at player_position_b)") + " | seat = model + (" + Esc3(BancoOff) + ") = Upper at (" + Esc3(Ecs.Add(TorretaUpper, BancoOff)) + ") | player B at (" + Esc3(JogadorB) + ") | view A at (" + Esc3(VisaoA) + "), " + Mat3(VisaoA).ToString("F2") + " m from center";
        static float Mat3(float3 v) => (float)System.Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        static bool PrepararLayer(EntityManager em)
        {
            if (layerOk) return true; if (layerFalhou) return false;
            try { tiLayer = TypeManager.GetTypeIndex(Il2CppType.Of<CRPLayerID>()); tiActSeat = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableSeat>()); layerOk = true; return true; }
            catch (Exception ex) { layerFalhou = true; Registro.Aviso("turret adjust: CRPLayerID/ActionableSeat types: " + ex.Message); return false; }
        }

        /// <summary>Update: teclas do modo ajuste + camada do banco (versao A).</summary>
        static void PassoAjuste(EntityManager em)
        {
            var t = TorreSentada;
            // ---- camada: esconde o banco de quem esta sentado (versao A), devolve quando levanta ou troca pra B
            bool querEsconder = t != null && BancoInvisivel && Ecs.Vivo(em, t.bloco);
            if (querEsconder && (!escondidoAtivo || escondidoBloco != t.bloco)) { if (escondidoAtivo) Camada(em, escondidoBloco, false); Camada(em, t.bloco, true); escondidoBloco = t.bloco; escondidoAtivo = true; }
            else if (!querEsconder && escondidoAtivo) { Camada(em, escondidoBloco, false); escondidoAtivo = false; }
            if (t == null) { if (Ajuste) { Ajuste = false; Registro.Info("turret adjust: off (stood up)"); } return; }
            // ---- teclas
            if (MecanicaPlugin.AjusteTurret == null || !MecanicaPlugin.AjusteTurret.Value) { if (Ajuste) Ajuste = false; return; }   // 0.12.14: debug, desligado por padrao
            if (Entrada.Apertou(Key.NumpadDivide)) { Ajuste = !Ajuste; Registro.Info("turret adjust: " + (Ajuste ? "ON — " + Resumo() : "off")); }
            if (!Ajuste) return;
            bool mudou = false;
            if (Entrada.Apertou(Key.NumpadMultiply)) { ModoBanco = (ModoBanco + 1) % 3; mudou = true; }
            if (Entrada.Apertou(Key.Numpad1)) { alvoAjuste = 0; Registro.Info("turret adjust: moving the SEAT"); }
            if (Entrada.Apertou(Key.Numpad3)) { alvoAjuste = alvoAjuste == 2 ? 1 : 2; Registro.Info("turret adjust: moving " + (alvoAjuste == 2 ? "the VIEW (mode A)" : "the PLAYER POSITION (mode B)")); }
            float passo = (Entrada.Segurando(Key.LeftShift) || Entrada.Segurando(Key.RightShift)) ? 0.05f : 0.01f;
            float3 d = float3.zero;
            if (Entrada.Apertou(Key.Numpad4)) d.x -= passo; if (Entrada.Apertou(Key.Numpad6)) d.x += passo;
            if (Entrada.Apertou(Key.Numpad8)) d.y += passo; if (Entrada.Apertou(Key.Numpad2)) d.y -= passo;
            if (Entrada.Apertou(Key.Numpad7)) d.z -= passo; if (Entrada.Apertou(Key.Numpad9)) d.z += passo;
            if (d.x != 0f || d.y != 0f || d.z != 0f) { if (alvoAjuste == 0) BancoOff = Ecs.Add(BancoOff, d); else if (alvoAjuste == 2) VisaoA = Ecs.Add(VisaoA, d); else JogadorB = Ecs.Add(JogadorB, d); mudou = true; }
            if (Entrada.Apertou(Key.Numpad5)) { if (alvoAjuste == 0) BancoOff = float3.zero; else if (alvoAjuste == 2) VisaoA = VisaoPadrao; else JogadorB = float3.zero; mudou = true; }
            if (mudou) { Salvar(); Registro.Info("turret adjust: " + Resumo()); }
        }

        /// <summary>0.12.10 (Andre: "a cadeira do turret apareceu no video mas nao de primeira"): durante a reproducao do Replay o ciclo
        /// da Mecanica para — se voce entrou sentado no turret (modo A), o banco ficava na camada invisivel. Devolve ao comecar; quando a
        /// reproducao acaba e voce continua sentado, o ciclo normal esconde de novo.</summary>
        public static void BancoVisivelNaReproducao(EntityManager em)
        {
            if (!escondidoAtivo) return;
            Camada(em, escondidoBloco, false); escondidoAtivo = false;
            Registro.Info("turret seat: Replay playback — seat visible during playback");
        }

        /// <summary>Poe (ou devolve) a camada MyPlayer nos renderers do banco do jogo deste turret: Upper, filhos do Upper, Piston.</summary>
        static unsafe void Camada(EntityManager em, Entity bloco, bool esconder)
        {
            try
            {
                if (!PrepararLayer(em) || !scSeatOk || !Ecs.TemC(em, bloco, tiSCSeat)) return;
                var sc = (Entity*)em.GetComponentDataRawRO(bloco, tiSCSeat);
                var lista = new List<Entity> { sc[0], sc[1] };
                lista.AddRange(Ecs.Filhos(em, sc[0]));
                int n = 0;
                foreach (var e in lista)
                {
                    if (!Ecs.TemC(em, e, tiLayer)) continue;
                    var b = (int*)em.GetComponentDataRawRW(e, tiLayer); long k = Ecs.K(e);
                    if (esconder) { if (!camadaOriginal.ContainsKey(k)) camadaOriginal[k] = *b; *b = 4; n++; }
                    else if (camadaOriginal.TryGetValue(k, out var orig)) { *b = orig; camadaOriginal.Remove(k); n++; }
                }
                Registro.Info("turret seat " + bloco.Index + ": seat " + (esconder ? "INVISIBLE to you (MyPlayer layer)" : "visible again") + " — " + n + " renderer(s)");
            }
            catch (Exception ex) { Registro.Aviso("turret seat: seat layer: " + ex.Message); }
        }

        /// <summary>No prefix do TransformSystemGroup (AlturasDosSeats): versao B = onde o jogador senta (relativo ao Upper) = JogadorB - Upper;
        /// versao A = o valor original do jogo.</summary>
        static unsafe void AplicarJogador(EntityManager em, Entity bloco, Entity upper, float3 upperPos)
        {
            try
            {
                if (!PrepararLayer(em) || !Ecs.TemC(em, upper, tiActSeat)) return;
                var p = (float3*)((byte*)em.GetComponentDataRawRW(upper, tiActSeat) + 16);   // RigidTransform _playerTransform: quaternion rot, float3 pos
                long k = Ecs.K(upper);
                if (!jogadorOriginal.TryGetValue(k, out var orig)) { orig = *p; jogadorOriginal[k] = orig; Registro.Info("turret seat " + bloco.Index + ": player position on the seat (game's) = " + Ecs.F3(orig) + " relative to Upper = " + Ecs.F3(Ecs.Add(upperPos, orig)) + " in block"); }
                *p = ModoBanco == 2 ? Ecs.Sub(JogadorB, upperPos) : orig;
            }
            catch (Exception ex) { if (errosJog++ < 3) Registro.Aviso("turret seat: player position: " + ex.Message); }
        }
        static int errosJog;

        // ---- painelzinho (OnGUI)
        static GUIStyle stAj;
        public static void DesenharAjuste()
        {
            if (!Ajuste || TorreSentada == null) return;
            if (stAj == null) { stAj = new GUIStyle(); stAj.fontSize = 14; var n = stAj.normal; n.textColor = Color.white; stAj.normal = n; stAj.wordWrap = false; }
            var linhas = new[]
            {
                "TURRET ADJUST  (Num/ exits)",
                "Num* mode: " + (ModoBanco == 0 ? "COCKPIT — you turn with the seat" : ModoBanco == 1 ? "A — seat INVISIBLE to you" : "B — seat visible, you sit at the player point"),
                "Num1 seat / Num3 view(A) <-> player(B) — moving: " + (alvoAjuste == 0 ? "SEAT" : alvoAjuste == 2 ? "VIEW" + (ModoBanco != 1 ? " (mode A only)" : "") : "PLAYER" + (ModoBanco != 2 ? " (mode B only)" : "")),
                "view (A): (" + Esc3(VisaoA) + ") — " + Mat3(VisaoA).ToString("F2") + " m from center" + (!VisaoGira && Mat3(VisaoA) > 0.56f ? "  (far from center: the ring/hub may cross the view)" : VisaoGira && ArcoPerto(VisaoA) ? "  (close to the back arch)" : ""),
                "seat: +(" + Esc3(BancoOff) + ")  -> Upper at (" + Esc3(Ecs.Add(TorretaUpper, BancoOff)) + ")",
                "player (B): (" + Esc3(JogadorB) + ") in block (0;0;0 = pivot center)",
                "Num4/6 left/right  Num8/2 up/down  Num7/9 back/front  (Shift x5)  Num5 resets the target",
            };
            float x = 12f, y = 120f, w = 560f, lh = 20f;
            GUI.Box(new Rect(x, y, w, lh * linhas.Length + 10f), ""); GUI.Box(new Rect(x, y, w, lh * linhas.Length + 10f), "");
            for (int i = 0; i < linhas.Length; i++) GUI.Label(new Rect(x + 8f, y + 5f + i * lh, w - 16f, lh), linhas[i], stAj);
        }
    }
}
