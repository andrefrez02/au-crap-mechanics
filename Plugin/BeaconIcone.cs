using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Mecanica
{
    /// <summary>0.17.0 (Andre: "icone de carro e um de nave + o que ja temos, e no displayzinho do beacon eu escolho qual icone exibir,
    /// por um knob proprio do beacon"): o knob do beacon (EPC_Actionable_Knob do jogo, 3 posicoes) escolhe 0 = ondas, 1 = carro,
    /// 2 = nave. O display (painel escuro da tampa inclinada) tem os 3 icones escondidos 4/5/6 mm pra dentro; o escolhido sai.
    /// O radar usa a fatia do icone escolhido (RadarIcone: ondas por cima do icone 8, carro e nave em fatias novas).</summary>
    internal static class BeaconIcone
    {
        // do modelos_v2 (BEACON_ICONE_P0 / _N / _ESCONDE, knob): ponto do icone aceso e normal da tampa inclinada, no bloco
        static readonly float3 P0 = new float3 { x = 0.0464726f, y = 0.0653310f, z = -0.03f };
        static readonly float3 N = new float3 { x = 0.4280863f, y = 0.9037378f, z = 0f };
        const float Esconde = 0.004f, PassoIcone = 0.001f;
        static TypeIndex tiData, tiBelong; static bool ok, falhou; static EntityQuery qData;
        public static readonly string[] Nomes = { "signal waves", "car", "ship" };

        static bool Preparar(EntityManager em)
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiData = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableData>());
                tiBelong = TypeManager.GetTypeIndex(Il2CppType.Of<BelongingSC>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2);
                a[0] = ComponentType.ReadOnly(tiBelong); a[1] = ComponentType.ReadOnly(tiData);
                qData = em.CreateEntityQuery(a); ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Erro("beacon knob: types: " + ex.Message); return false; }
        }

        static float Dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        /// <summary>Le o knob e atualiza o display. Roda na garagem tambem (pra ver o icone escolhido enquanto monta).</summary>
        public static unsafe void Passo(EntityManager em, Beacon b, float dt)
        {
            if (!Preparar(em)) return;
            b.tKnob += dt;
            if (b.tKnob >= 3f || (b.knob.Index == 0 && b.knob.Version == 0 && b.tKnob >= 0.5f))
            {
                b.tKnob = 0f;
                if (b.knob.Index == 0 && b.knob.Version == 0 || !Ecs.Vivo(em, b.knob)) b.knob = AcharKnob(em, b.bloco);
                if (b.icones == null || b.icones.Length < 3 || !Ecs.Vivo(em, b.icones[0])) AcharIcones(em, b);
            }
            int k = b.icone;
            if (Ecs.Vivo(em, b.knob))
            {
                var v = (Ecs.TemC(em, b.knob, tiData) ? (float*)em.GetComponentDataRawRO(b.knob, tiData) : null);
                if (v != null)
                {
                    float x = v[0]; if (float.IsNaN(x)) x = 0f;
                    k = x <= 1.01f ? (int)Math.Round(x * 2f) : (int)Math.Round(x);   // 3 posicoes: 0 / 0,5 / 1 (ou 0 / 1 / 2)
                    k = Math.Max(0, Math.Min(2, k));
                    if (Math.Abs(x - b.knobLog) > 0.01f) { if (b.knobLog < -1f) Registro.Info("SC_Beacon " + b.bloco.Index + ": knob value " + x.ToString("F2")); b.knobLog = x; }
                }
            }
            Dica(em, b, k);
            if (k != b.icone || b.iconeMostrado != k) { if (k != b.icone) Registro.Info("SC_Beacon " + b.bloco.Index + ": icon " + Nomes[k]); b.icone = k; Mostrar(em, b); }
        }

        /// <summary>0.19.1 (Andre: "o knob do beacon ta sem tooltip; no knob do monitor ele fala qual modo esta configurado"): o knob
        /// do beacon e' _drawUIValue = Custom, e o jogo poe nele um ActionableTargetDrawCustomUIValue (FixedString64Bytes: +0 ushort
        /// tamanho em bytes UTF-8, depois o texto) que a dica do ponteiro mostra — vazio. Aqui o texto vira o icone escolhido.</summary>
        static TypeIndex tiDica; static bool dicaOk, dicaFalhou;
        static unsafe void Dica(EntityManager em, Beacon b, int k)
        {
            if (dicaFalhou || !Ecs.Vivo(em, b.knob)) return;
            if (k == b.dica && b.knob.Index == b.dicaKnob.Index && b.knob.Version == b.dicaKnob.Version) return;
            try
            {
                if (!dicaOk) { tiDica = TypeManager.GetTypeIndex(Il2CppType.Of<ActionableTargetDrawCustomUIValue>()); dicaOk = true; }
                if (!Ecs.TemC(em, b.knob, tiDica)) { b.dica = k; b.dicaKnob = b.knob; if (!dicaAvisada) { dicaAvisada = true; Registro.Aviso("beacon knob: no custom tooltip component on the knob (rebuild the bundle in the Modkit)"); } return; }
                string txt = Texto(k);
                var bytes = System.Text.Encoding.UTF8.GetBytes(txt); int n = Math.Min(bytes.Length, 60);
                var p = (byte*)em.GetComponentDataRawRW(b.knob, tiDica);
                *(ushort*)p = (ushort)n; for (int i = 0; i < n; i++) p[2 + i] = bytes[i]; p[2 + n] = 0;
                b.dica = k; b.dicaKnob = b.knob;
            }
            catch (Exception ex) { dicaFalhou = true; Registro.Aviso("beacon knob tooltip: " + ex.Message); }
        }
        static bool dicaAvisada;
        static readonly string[] DicaPadrao = { "Radar icon: signal waves", "Radar icon: car", "Radar icon: ship" };
        static string Texto(int k)
        {
            string chave = "SC_Beacon_KnobIcon" + k;
            try { var loc = Localization._cachedInstance; if (loc != null && loc._map != null && loc._map.ContainsKey(chave)) return loc._map[chave]; } catch (Exception) { }
            return DicaPadrao[k];
        }

        /// <summary>0.19.3: o mesmo achador pros knobs do bearing/pistao (Knobs.cs) — na garagem o knob pode nao estar no buffer Child do
        /// bloco, so no BelongingSC dele.</summary>
        public static Entity AcharKnobDe(EntityManager em, Entity bloco) => Preparar(em) ? AcharKnob(em, bloco) : Entity.Null;
        /// <summary>0.19.15: todos os actionables com BelongingSC (knobs, alavancas, botoes) e o bloco dono de cada um.</summary>
        public static Entity[] TodosActionables(EntityManager em) => Preparar(em) ? Ecs.Lista(qData).ToArray() : new Entity[0];
        public static unsafe Entity BlocoDoActionable(EntityManager em, Entity a)
        {
            var bs = (Ecs.TemC(em, a, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(a, tiBelong) : null);
            return bs == null ? Entity.Null : bs->_sc;
        }
        /// <summary>0.23.0: o Knobs manda aqui o TypeIndex do ActionableToggle (o switch do bearing) pra nao ser confundido com o knob.</summary>
        public static TypeIndex PularToggle;
        static bool Pular(EntityManager em, Entity f) => PularToggle.Value != 0 && Ecs.TemC(em, f, PularToggle);
        /// <summary>0.23.0: todos os actionables (por BelongingSC) de um bloco — o switch do bearing na garagem nao esta no Child.</summary>
        public static unsafe System.Collections.Generic.List<Entity> ActionablesDe(EntityManager em, Entity bloco)
        {
            var r = new System.Collections.Generic.List<Entity>();
            if (!Preparar(em)) return r;
            foreach (var f in Ecs.Lista(qData))
            {
                var bs = (Ecs.TemC(em, f, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(f, tiBelong) : null);
                if (bs != null && bs->_sc.Index == bloco.Index && bs->_sc.Version == bloco.Version) r.Add(f);
            }
            return r;
        }
        static unsafe Entity AcharKnob(EntityManager em, Entity bloco)
        {
            foreach (var f in Ecs.Filhos(em, bloco)) if (Ecs.TemC(em, f, tiData) && !Pular(em, f)) return f;
            foreach (var f in Ecs.Lista(qData))
            {
                var bs = (Ecs.TemC(em, f, tiBelong) ? (BelongingSC*)em.GetComponentDataRawRO(f, tiBelong) : null);
                if (bs != null && bs->_sc.Index == bloco.Index && bs->_sc.Version == bloco.Version && !Pular(em, f)) return f;
            }
            return Entity.Null;
        }

        /// <summary>0.19.2: versao M (espelhada em X): P0.x e N.x trocam de sinal. Descoberto pelo lado em que os icones estao.</summary>
        static float3 P0De(Beacon b) => b.espelho ? new float3 { x = -P0.x, y = P0.y, z = P0.z } : P0;
        static float3 NDe(Beacon b) => b.espelho ? new float3 { x = -N.x, y = N.y, z = N.z } : N;
        static void AcharIcones(EntityManager em, Beacon b)
        {
            var l = new Entity[3]; int n = 0;
            if (!b.espelhoTestado) { b.espelhoTestado = true; b.espelho = false; int nn = 0; foreach (var f in Ecs.Filhos(em, b.bloco)) { var p = Ecs.LT(em, f).Position; if (Math.Abs(p.x + P0.x) < 0.01f && Math.Abs(p.y - P0.y) < 0.03f) nn++; } b.espelho = nn >= 2; }
            var P0b = P0De(b); var Nb = NDe(b);
            foreach (var f in Ecs.Filhos(em, b.bloco))
            {
                var p = Ecs.LT(em, f).Position; var d = new float3 { x = p.x - P0b.x, y = p.y - P0b.y, z = p.z - P0b.z };
                float prof = Dot(d, Nb); var t = new float3 { x = d.x - Nb.x * prof, y = d.y - Nb.y * prof, z = d.z - Nb.z * prof };
                if (Dot(t, t) > 0.0015f * 0.0015f) continue;
                int k = (int)Math.Round((-prof - Esconde) / PassoIcone);
                if (k < 0 || k > 2 || Math.Abs(-prof - Esconde - k * PassoIcone) > 0.0003f) continue;   // (so os escondidos; o aceso ja e' conhecido)
                l[k] = f; n++;
            }
            if (n == 3 || b.icones == null) { b.icones = l; b.iconeMostrado = -1; }
            if (n < 3 && !b.iconesAvisado && Ecs.EhPart(em, Ecs.Pai(em, b.bloco))) { b.iconesAvisado = true; Registro.Aviso("SC_Beacon " + b.bloco.Index + ": display icons not found (" + n + "/3) — rebuild the bundle in the Modkit"); }
        }

        static unsafe void Mostrar(EntityManager em, Beacon b)
        {
            if (b.icones == null) return;
            for (int k = 0; k < b.icones.Length; k++)
            {
                var e = b.icones[k]; if (!Ecs.Vivo(em, e)) continue;
                var lt = (Ecs.TemC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) continue;
                float prof = k == b.icone ? 0f : -(Esconde + PassoIcone * k);
                var P0b = P0De(b); var Nb = NDe(b);
                lt->Position = new float3 { x = P0b.x + Nb.x * prof, y = P0b.y + Nb.y * prof, z = P0b.z + Nb.z * prof };
            }
            b.iconeMostrado = b.icone;
        }
    }
}
