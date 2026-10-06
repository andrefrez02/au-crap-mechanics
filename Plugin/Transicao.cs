using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace Mecanica
{
    /// <summary>0.19.12 (Andre, 03/10: "adiciona mais log, principalmente quando roda esse CleanupWorldToDefaultState; lê todos os valores que
    /// estão quebrando antes e depois"). SO LOG, nada muda. A cada Core.CleanupWorldToDefaultState / Core.ClearGame: fotografia ANTES (prefix),
    /// DEPOIS (postfix) e com +1 s, +3 s e +6 s, sempre das MESMAS entities capturadas na foto "antes": LRTs, Wireless Transmitters do jogo,
    /// cameras e monitores — bloco vivo, part, energia, e por porta: viva, tipo, valor, elo persistente, _runtimeEndEntity (porta / cabo / nada)
    /// e o caminho ate a fonte; transmitter: canal; camera: fatia e sinal; monitor: fatias e sinais. Mais densidade do ar e velocidade.</summary>
    [HarmonyPatch]
    internal static class Transicao
    {
        static int n; static readonly List<Entity> lrts = new List<Entity>(), transm = new List<Entity>(), cams = new List<Entity>(), mons = new List<Entity>();
        static readonly List<(float em, string rotulo)> pendentes = new List<(float, string)>(); static float t0; static bool tiOk, tiFalhou;
        static TypeIndex tiCam, tiMon, tiAr, tiVel, tiLTW, tiTransm, tiPorta;
        static string motivo = "";

        static bool Preparar()
        {
            if (tiOk) return true; if (tiFalhou) return false;
            try
            {
                tiCam = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCamera>());
                tiMon = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeMonitor>());
                tiTransm = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>());
                tiAr = TypeManager.GetTypeIndex(Il2CppType.Of<AtmosphereDensityMeter>());
                tiVel = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Physics.PhysicsVelocity>());
                tiLTW = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Transforms.LocalToWorld>());
                tiPorta = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPort>());
                tiOk = true; return true;
            }
            catch (Exception ex) { tiFalhou = true; Registro.Aviso("transition log: " + ex.Message); return false; }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void AntesCleanup() { Antes("Core.CleanupWorldToDefaultState"); }
        [HarmonyPostfix, HarmonyPatch(typeof(Core), nameof(Core.CleanupWorldToDefaultState))]
        static void DepoisCleanup() { Depois(); }
        [HarmonyPrefix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void AntesClear() { Antes("Core.ClearGame"); }
        [HarmonyPostfix, HarmonyPatch(typeof(Core), nameof(Core.ClearGame))]
        static void DepoisClear() { Depois(); }

        static void Antes(string de)
        {
            try
            {
                if (!Juntas.EmOk || !Preparar()) return;
                var em = Juntas.EmAtual; n++; motivo = de; t0 = UnityEngine.Time.unscaledTime;
                lrts.Clear(); transm.Clear(); cams.Clear(); mons.Clear();
                foreach (var e in Radio.Blocos()) lrts.Add(e);
                foreach (var e in Ecs.Componentes(em))
                {
                    if (Ecs.TemC(em, e, tiTransm)) transm.Add(e);
                    else if (Ecs.TemC(em, e, tiCam)) cams.Add(e);
                    else if (Ecs.TemC(em, e, tiMon)) mons.Add(e);
                }
                pendentes.Clear(); pendentes.Add((1f, "+1 s")); pendentes.Add((3f, "+3 s")); pendentes.Add((6f, "+6 s"));
                Foto(em, "BEFORE");
            }
            catch (Exception ex) { Registro.Aviso("transition log (before): " + ex.Message); }
        }
        static void Depois()
        {
            try { if (Juntas.EmOk && tiOk && n > 0) Foto(Juntas.EmAtual, "AFTER (method returned)"); }
            catch (Exception ex) { Registro.Aviso("transition log (after): " + ex.Message); }
        }
        /// <summary>Chamado do Update do plugin (antes de qualquer early-return): fotos atrasadas.</summary>
        public static void Passo()
        {
            if (pendentes.Count == 0 || !Juntas.EmOk) return;
            float dt = UnityEngine.Time.unscaledTime - t0;
            for (int i = 0; i < pendentes.Count; i++)
            {
                if (dt < pendentes[i].em) continue;
                var r = pendentes[i].rotulo; pendentes.RemoveAt(i);
                try { Foto(Juntas.EmAtual, r); } catch (Exception ex) { Registro.Aviso("transition log (" + r + "): " + ex.Message); }
                return;
            }
        }

        static unsafe void Foto(EntityManager em, string quando)
        {
            var sb = new StringBuilder();
            sb.Append("===== TRANSITION #").Append(n).Append(" (").Append(motivo).Append(") — ").Append(quando).Append(" — t=").Append(UnityEngine.Time.unscaledTime.ToString("F2")).Append(" s; ");
            sb.Append("mod state: ").Append(Juntas.EmOk ? "ecs ok" : "ecs NOT ok").Append(Descarga.Ativa ? ", mod paused (unload window)" : "").Append(Descarga.Pendente ? ", reset pending" : "");
            sb.Append("; radios tracked ").Append(Radio.Quantos()).Append(", joints tracked ").Append(Juntas.Quantas());
            sb.Append("\n  player: ").Append(Jogador(em));
            foreach (var e in lrts) Bloco(em, sb, "LRT", e);
            foreach (var e in transm) Bloco(em, sb, "WirelessTransmitter", e);
            foreach (var e in cams) Bloco(em, sb, "Camera", e);
            foreach (var e in mons) Bloco(em, sb, "Monitor", e);
            Registro.Info(sb.ToString());
        }

        static unsafe string Jogador(EntityManager em)
        {
            try
            {
                var j = Ecs.MeuJogadorOuNulo(em); if (Ecs.Nulo(j) || !Ecs.Vivo(em, j)) return "not found";
                var s = "entity " + j.Index + ":" + j.Version + " parent " + Ecs.Pai(em, j).Index;
                s += " air " + Ar(em, j) + " vel " + Vel(em, j) + " seat " + (Ecs.Nulo(Ecs.Pai(em, j)) ? "none" : (Ecs.EhPart(em, Ecs.Pai(em, j)) ? "part" : "entity " + Ecs.Pai(em, j).Index));
                return s;
            }
            catch (Exception ex) { return "error " + ex.Message; }
        }
        static unsafe string Ar(EntityManager em, Entity e)
        {
            try
            {
                var p = Ecs.TemC(em, e, tiAr) ? (float*)em.GetComponentDataRawRO(e, tiAr) : null; if (p != null) return p[0].ToString("F3");
                var pai = Ecs.Pai(em, e); p = Ecs.TemC(em, pai, tiAr) ? (float*)em.GetComponentDataRawRO(pai, tiAr) : null; if (p != null) return p[0].ToString("F3") + "(part)";
                return "?";
            }
            catch (Exception) { return "?"; }
        }
        static unsafe string Vel(EntityManager em, Entity e)
        {
            try
            {
                var alvo = Ecs.TemC(em, e, tiVel) ? e : Ecs.Pai(em, e);
                var p = Ecs.TemC(em, alvo, tiVel) ? (float*)em.GetComponentDataRawRO(alvo, tiVel) : null; if (p == null) return "?";
                return Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]).ToString("F1") + " m/s";
            }
            catch (Exception) { return "?"; }
        }

        static unsafe void Bloco(EntityManager em, StringBuilder sb, string tipo, Entity e)
        {
            sb.Append("\n  ").Append(tipo).Append(' ').Append(e.Index).Append(':').Append(e.Version);
            if (!Ecs.Vivo(em, e)) { sb.Append(" DEAD"); return; }
            var pai = Ecs.Pai(em, e);
            sb.Append(" parent ").Append(pai.Index).Append(':').Append(pai.Version).Append(Ecs.EhPart(em, pai) ? " (part)" : " (not a part)");
            byte estE; bool energia = Ecs.TemEnergia(em, e, out estE); sb.Append(" power ").Append(estE == 255 ? "n/a" : Juntas.NomeEstado(estE));
            sb.Append(" air ").Append(Ar(em, e)).Append(" vel ").Append(Vel(em, e));
            try
            {
                if (tipo == "WirelessTransmitter") sb.Append(" channel ").Append(Ecs.Canal(em, e));
                if (tipo == "Camera")
                {
                    var c = Ecs.TemC(em, e, tiCam) ? (byte*)em.GetComponentDataRawRO(e, tiCam) : null;
                    if (c != null) { var ce = *(Entity*)c; sb.Append(" cameraEntity ").Append(ce.Index).Append(Ecs.Vivo(em, ce) ? "" : "(DEAD)").Append(" slice ").Append(*(int*)(c + 0x30)).Append(" nextDraw ").Append((*(double*)(c + 0x28)).ToString("F2")); }
                }
                if (tipo == "Monitor")
                {
                    var m = Ecs.TemC(em, e, tiMon) ? (byte*)em.GetComponentDataRawRO(e, tiMon) : null;
                    if (m != null) { sb.Append(" slices (").Append(*(int*)(m + 0x28)).Append(',').Append(*(int*)(m + 0x2C)).Append(") lastSignals (").Append((*(float*)(m + 0x20)).ToString("G9")).Append(',').Append((*(float*)(m + 0x24)).ToString("G9")).Append(')'); }
                }
            }
            catch (Exception ex) { sb.Append(" [").Append(ex.Message).Append(']'); }
            var portas = Ecs.Portas(em, e);
            sb.Append(" ports ").Append(portas.Length).Append(':');
            for (int i = 0; i < portas.Length; i++)
            {
                var p = portas[i]; sb.Append("\n      [").Append(i).Append("] ").Append(p.Index).Append(':').Append(p.Version);
                if (!Ecs.Vivo(em, p)) { sb.Append(" DEAD"); continue; }
                if (!Ecs.PortaCruaPub(em, p, out var t, out var v, out var fim, out var persist)) { sb.Append(" no SpaceshipElectricPort"); continue; }
                byte rt = 255; try { var b = Ecs.TemC(em, p, tiPorta) ? (byte*)em.GetComponentDataRawRO(p, tiPorta) : null; if (b != null) rt = b[21]; } catch (Exception) { }
                sb.Append(" setup ").Append(t).Append(" runtime ").Append(rt).Append(" value ").Append(v.ToString("G9"));
                sb.Append(" persistent -> ").Append(Quem(em, persist)).Append(" runtimeEnd -> ").Append(Quem(em, fim));
                if (t == Ecs.PortaEntrada || rt == Ecs.PortaEntrada || t == 3)
                {
                    try { var fonte = Ecs.Fonte(em, p, out var trilha); sb.Append(" source ").Append(Ecs.Nulo(fonte) ? "NONE" : fonte.Index.ToString()).Append(" path:").Append(trilha); } catch (Exception ex) { sb.Append(" source error ").Append(ex.Message); }
                }
            }
        }
        static string Quem(EntityManager em, Entity x)
        {
            if (Ecs.Nulo(x)) return "nothing";
            if (!Ecs.Vivo(em, x)) return x.Index + "(DEAD)";
            if (Ecs.EhCabo(em, x)) return x.Index + "(cable cell)";
            if (Ecs.PortaCruaPub(em, x, out var t, out _, out _, out _)) { var dono = Ecs.Pai(em, x); return x.Index + "(port t" + t + " of " + dono.Index + ")"; }
            return x.Index + "(other)";
        }

        public static void Patch(Harmony h)
        {
            try { h.PatchAll(typeof(Transicao)); Registro.Info("transition log: on (snapshots before/after Core.CleanupWorldToDefaultState and Core.ClearGame, +1/+3/+6 s)"); }
            catch (Exception ex) { Registro.Aviso("transition log hook failed (" + ex.Message + ")"); }
        }
    }
}
