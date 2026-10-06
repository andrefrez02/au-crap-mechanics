using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace Mecanica
{
    /// <summary>Recoupler (0.7.7): reacopla dois pedacos que um Decoupler do jogo JA separou.
    /// Regras: sinal &gt;= 0,5 (nivel, nao precisa subir); encostado (folga Tolerancia) na face que solta (+Y) de um Decoupler de
    /// OUTRO pedaco que ja disparou. Depois de travar/juntar, so volta a agir quando o sinal cai &lt; 0,5 ou os pedacos se afastam.
    /// Etapas: (1) PUXAR — joint fixo que leva o recoupler, em ~0,6 s, pra pose certa no decoupler: a pose original da garagem
    /// (GarageTransform) se os dois eram vizinhos la; senao encostado reto (face com face, rotacao de 90 em 90).
    /// (2) JUNTAR (so no caso "original") — o contrario do decoupler: move os blocos do pedaco do recoupler pra part do
    /// decoupler, religa o joint do jogo entre os dois, rearma o decoupler e pede pro jogo recalcular a part
    /// (SpaceshipPartConsistencyCheck). Se algo falhar, fica so o joint fixo.</summary>
    internal class Recoupler
    {
        public Entity bloco, joint, partA, partB, decoupler; public int avisos;
        public int estado;          // 0 livre, 1 puxando/travado (joint fixo), 2 juntado
        public bool armado = true, original; public float tPuxar; public string motivoLog; public float tMotivo;
        public BodyFrame fb, fa0, fa1;        // fb no pedaco do recoupler; fa0 (pose inicial) -> fa1 (pose certa) no pedaco do decoupler
        public int jointJogo = -1; public bool rearmarDecoupler;
        public Entity ancora; public bool naAncora; public float duracao = 0.6f;   // 0.7.8: puxa preso na base do pistao (nave pesada), nao no topo leve
        public Entity[] leds; public float3[] ledRepouso; public bool ledAceso; public float tLed = 9f; public bool encostado; public float tEncostado = 9f;   // 0.11.2: leds verdes (renderers extras) que saem do painel quando acopla
    }

    /// <summary>Beacon: aparece no radar do jogo (icone proprio, ver RadarIcone), em tempo real. O radar desenha todo entity com
    /// LocalToWorld + RadarAppearanceIcon (SCTick_Radar.FindIconsJob); crio um entity "fantasma" com esses dois e copio a
    /// posicao do bloco todo frame (nao mexo no entity do bloco).</summary>
    internal class Beacon { public Entity bloco, fantasma; public bool escondido; public float tLog; public bool filhosLogados; public string nome = ""; public float tNome = 9f; public string guid; public bool semEnergia; public byte estadoEnergia = 255;
        public Entity knob; public Entity[] icones; public int icone, iconeMostrado = -1; public float tKnob = 9f, knobLog = -9f; public bool iconesAvisado; public int dica = -1; public Entity dicaKnob; public bool espelho, espelhoTestado; }   // 0.17.0; 0.19.1: dica = icone escrito na dica do knob

    internal static class Acoplador
    {
        public static float Tolerancia = 0.5f;    // m de folga pra considerar "encostado" (0.7.0-0.7.5: 0,2; config Recoupler.folga)
        public static bool EsconderProprio = true;   // 0.17.3: config Beacon.hide_on_own_vehicle
        public static float EsconderPerto = 40f;   // m: beacon perto do jogador nao aparece (e' o veiculo onde ele esta)
        public static float ConsumoBeacon = 5f;    // 0.11.1: P/s que o beacon pede na porta de energia (config Beacon.consumo)
        static TypeIndex tiJogador; static EntityQuery qJogador; static bool jogOk;
        public static uint IconeBeacon = 2;        // RadarIconID.PlanetPackage ate o RadarIcone montar o icone proprio
        static readonly Dictionary<long, Recoupler> recs = new Dictionary<long, Recoupler>();
        static readonly Dictionary<long, Beacon> beacons = new Dictionary<long, Beacon>();
        static TypeIndex tiIcone; static bool ok; static EntityQuery qDec; static bool qDecOk;

        public static bool Tem(long k) => recs.ContainsKey(k) || beacons.ContainsKey(k);
        /// <summary>0.7.12: foto do estado pra marca de erro (F8).</summary>
        public static string Estado(EntityManager em)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in recs.Values)
            {
                sb.Append("  SC_Recoupler ").Append(r.bloco.Index).Append(": ").Append(r.estado == 0 ? "free" : r.estado == 1 ? (r.naAncora ? "pulling" : "locked") : "merged");
                if (!Ecs.Nulo(r.decoupler)) sb.Append(", decoupler ").Append(r.decoupler.Index);
                if (!Ecs.Nulo(r.partA)) sb.Append(", part ").Append(r.partA.Index).Append(" / ").Append(r.partB.Index);
                sb.Append('\n');
            }
            foreach (var b in beacons.Values) sb.Append("  SC_Beacon ").Append(b.bloco.Index).Append(b.escondido ? ": hidden" : ": visible").Append(Ecs.Nulo(b.fantasma) ? ", no ghost" : ", ghost " + b.fantasma.Index).Append('\n');
            try { sb.Append(Alavancas.Estado(em)); } catch (Exception) { }
            if (sb.Length == 0) sb.Append("  (none)\n");
            return sb.ToString();
        }
        public static IEnumerable<Recoupler> Travados() { foreach (var r in recs.Values) if (r.estado == 1 && !r.naAncora && !Ecs.Nulo(r.joint)) yield return r; }
        public static void NovoRecoupler(Entity e) { recs[Ecs.K(e)] = new Recoupler { bloco = e }; Registro.Info("SC_Recoupler found: block " + e.Index + ":" + e.Version); }
        public static void NovoBeacon(Entity e) { beacons[Ecs.K(e)] = new Beacon { bloco = e }; Registro.Info("SC_Beacon found: block " + e.Index + ":" + e.Version); }
        public static IEnumerable<Beacon> Beacons() => beacons.Values;   // 0.19.13: regra dos 100 km (Ativo)

        public static void Passo(EntityManager em, float dt)
        {
            if (!ok) { ok = true; try { tiIcone = TypeManager.GetTypeIndex(Il2CppType.Of<RadarAppearanceIcon>()); } catch (Exception ex) { Registro.Erro("beacon: RadarAppearanceIcon type: " + ex.Message); } }
            var mortos = new List<long>();
            foreach (var kv in recs)
            {
                try { if (!Ecs.Vivo(em, kv.Value.bloco)) { Soltar(em, kv.Value, "block is gone"); mortos.Add(kv.Key); continue; } PassoRec(em, kv.Value); }
                catch (Exception ex) { if (kv.Value.avisos++ < 5) Registro.Aviso("recoupler " + kv.Value.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortos) recs.Remove(k);
            mortos.Clear();
            foreach (var kv in beacons)
            {
                try
                {
                    if (!Ecs.Vivo(em, kv.Value.bloco)) { ApagarFantasma(em, kv.Value); mortos.Add(kv.Key); continue; }
                    // 0.11.0 (pesquisa da etiqueta): lista uma vez os filhos do bloco (renderers, etiqueta, texto) e seus componentes
                    if (!kv.Value.filhosLogados && Ecs.EhPart(em, Ecs.Pai(em, kv.Value.bloco)))
                    {
                        kv.Value.filhosLogados = true;
                        var sb = new System.Text.StringBuilder("SC_Beacon " + kv.Value.bloco.Index + ": block children:");
                        foreach (var f in Ecs.Filhos(em, kv.Value.bloco)) { var lt = Ecs.LT(em, f); sb.Append("\n    ").Append(f.Index).Append(" at (").Append(Ecs.F3(lt.Position)).Append("): ").Append(Ecs.Componentes(em, f)); }
                        sb.Append("\n  block components: ").Append(Ecs.Componentes(em, kv.Value.bloco));
                        Registro.Info(sb.ToString());
                    }
                    try { BeaconIcone.Passo(em, kv.Value, UnityEngine.Time.deltaTime); } catch (Exception exk) { Registro.Aviso("beacon " + kv.Value.bloco.Index + " knob: " + exk.Message); }
                    PassoBeacon(em, kv.Value);
                }
                catch (Exception ex) { Registro.Aviso("beacon " + kv.Value.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortos) beacons.Remove(k);
            DestruirOrfaos(em, dt);
            if (beacons.Count > 0) DiagRadar(em);
            // 0.17.1: desenhos do beacon so por cima de icones que o jogo nao esta usando agora (RadarIcone.Atualizar)
            try
            {
                int pedidos = 0; var fantasmas = new HashSet<int>();
                foreach (var bc in beacons.Values) { if (Ecs.EhPart(em, Ecs.Pai(em, bc.bloco))) pedidos |= 1 << bc.icone; if (!Ecs.Nulo(bc.fantasma)) fantasmas.Add(bc.fantasma.Index); }
                RadarIcone.Atualizar(em, pedidos, fantasmas, UnityEngine.Time.deltaTime);
                // 0.19.2: lista do que vai direto pro buffer do radar (RadarEscrita, depois do TransformSystemGroup)
                iconesRadar.Clear();
                foreach (var bc in beacons.Values) if (Ecs.Vivo(em, bc.fantasma) && !bc.escondido && !bc.semEnergia) iconesRadar.Add((Ecs.Posicao(em, bc.bloco), RadarIcone.SlotDe(bc.icone)));
            }
            catch (Exception ex) { Registro.Aviso("radar icons: " + ex.Message); }
        }

        // ---------------------------------------------------------------- diagnostico do radar (0.7.9)
        // O radar do jogo (SCTick_Radar.FindIconsJob) escreve cada icone num buffer RadarLocationGPUData {_uv, _type, _isTarget}
        // (16 B). Aqui leio esse buffer e logo quais tipos estao nele: se o 8 (o do beacon) nao aparece, o jogo nem pegou o
        // nosso entity fantasma. E logo os componentes de um icone de verdade do jogo, pra copiar o que faltar.
        static TypeIndex tiGPU; static EntityQuery qGPU, qIcones; static bool diagOk, diagFalhou, compLogados, fantasmaLogado; static readonly List<TypeIndex> tagsExtras = new List<TypeIndex>(); static float tDiag = 9f; static string diagLog; static int diagLogs;
        static unsafe void DiagRadar(EntityManager em)
        {
            if (diagFalhou || diagLogs >= 60) return;   // 0.17.3: volta a logar o buffer do radar (quando muda) pra achar por que o icone nao aparece
            tDiag += UnityEngine.Time.deltaTime; if (tDiag < 3f) return; tDiag = 0f;
            try
            {
                if (!diagOk)
                {
                    diagOk = true;
                    tiGPU = TypeManager.GetTypeIndex(Il2CppType.Of<SCTick_Radar.RadarLocationGPUData>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiGPU); qGPU = em.CreateEntityQuery(a);
                    var b = new Il2CppStructArray<ComponentType>(1); b[0] = ComponentType.ReadOnly(tiIcone); qIcones = em.CreateEntityQuery(b);
                }
                var sb = new System.Text.StringBuilder();
                var donos = Ecs.Lista(qGPU);
                foreach (var e in donos)
                {
                    int n = em.GetBufferLength(e, tiGPU); var p = (byte*)em.GetBufferRawRO(e, tiGPU);
                    var cont = new SortedDictionary<uint, int>();
                    for (int i = 0; i < n && p != null; i++) { uint t = *(uint*)(p + i * 16 + 8); cont.TryGetValue(t, out int c); cont[t] = c + 1; }
                    sb.Append(" [entity ").Append(e.Index).Append(": ").Append(n).Append(" icon(s)");
                    foreach (var kv in cont) sb.Append(", type ").Append(kv.Key).Append(" x").Append(kv.Value);
                    sb.Append("]");
                }
                int fantasmas = 0, doJogo = 0; Entity exemplo = Entity.Null, meu = Entity.Null;
                var meus = new HashSet<int>(); foreach (var bc in beacons.Values) if (!Ecs.Nulo(bc.fantasma)) meus.Add(bc.fantasma.Index);
                foreach (var e in Ecs.Lista(qIcones)) { if (meus.Contains(e.Index)) { fantasmas++; meu = e; } else { doJogo++; if (Ecs.Nulo(exemplo)) exemplo = e; } }
                string linha = "radar/diag: radar buffers:" + (donos.Count == 0 ? " none" : sb.ToString()) + " | entities with RadarAppearanceIcon: " + fantasmas + " ours, " + doJogo + " from the game";
                if (!compLogados && !Ecs.Nulo(exemplo)) { compLogados = true; TagsQueFaltam(em, exemplo); }
                if (fantasmas > 0 && linha != diagLog) { diagLog = linha; diagLogs++; Registro.Info(linha + " | ours use icon " + string.Join(",", IconesFantasmas(em)) + " | " + RadarIcone.Estado()); }
            }
            catch (Exception ex) { diagFalhou = true; Registro.Aviso("radar/diag failed: " + ex.Message); }
        }

        static unsafe List<uint> IconesFantasmas(EntityManager em)
        {
            var l = new List<uint>();
            foreach (var bc in beacons.Values) { if (Ecs.Nulo(bc.fantasma) || !Ecs.Vivo(em, bc.fantasma)) continue; var p = (Ecs.TemC(em, bc.fantasma, tiIcone) ? (uint*)em.GetComponentDataRawRO(bc.fantasma, tiIcone) : null); if (p != null) l.Add(*p); }
            return l;
        }

        /// <summary>0.7.10: o buffer do radar so tinha o icone da HQ (tipo 0) — o nosso fantasma (tipo 8) nunca entrou. Copio pro
        /// fantasma as TAGS (componentes sem dados) que um icone de verdade do jogo tem e o fantasma nao — a query do radar pode
        /// exigir alguma. Componentes com dados nao copio (so logo o nome) pra nao acordar outros sistemas do jogo.</summary>
        static unsafe void TagsQueFaltam(EntityManager em, Entity exemplo)
        {
            try
            {
                var arr = em.GetComponentTypes(exemplo, Unity.Collections.Allocator.Temp);
                var faltando = new System.Text.StringBuilder();
                for (int i = 0; i < arr.Length; i++)
                {
                    var ti = arr[i].TypeIndex;
                    if (ti.Value == Ecs.tiLTW.Value || ti.Value == tiIcone.Value) continue;
                    bool tag = false; try { tag = TypeManager.GetTypeInfo(ti).IsZeroSized; } catch (Exception) { }
                    string nome = "#" + ti.Value; try { var t = TypeManager.GetType(ti); if (t != null) nome = t.FullName; } catch (Exception) { }
                    if (tag) { tagsExtras.Add(ti); faltando.Append(" +").Append(nome); } else faltando.Append(" (data, not copying: ").Append(nome).Append(")");
                }
                Registro.Info("radar/diag: for the ghost:" + faltando);
                foreach (var b in beacons.Values) if (Ecs.Vivo(em, b.fantasma)) PorTags(em, b.fantasma);
            }
            catch (Exception ex) { Registro.Aviso("radar/diag: tags: " + ex.Message); }
        }
        static void PorTags(EntityManager em, Entity e)
        {
            foreach (var ti in tagsExtras) { try { if (!em.HasComponent(e, ComponentType.ReadOnly(ti))) em.AddComponent(e, ComponentType.ReadWrite(ti)); } catch (Exception) { } }
        }

        static unsafe string Componentes(EntityManager em, Entity e)
        {
            try
            {
                var arr = em.GetComponentTypes(e, Unity.Collections.Allocator.Temp);
                var sb = new System.Text.StringBuilder();
                int n = arr.Length;
                for (int i = 0; i < n; i++)
                {
                    var ct = arr[i]; string nome = null;
                    try { var t = TypeManager.GetType(ct.TypeIndex); if (t != null) nome = t.FullName; } catch (Exception) { }
                    try { if (TypeManager.GetTypeInfo(ct.TypeIndex).IsZeroSized) nome = (nome ?? "#" + ct.TypeIndex.Value) + "(tag)"; } catch (Exception) { }
                    sb.Append(i == 0 ? "" : ", ").Append(nome ?? ("#" + ct.TypeIndex.Value));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "(could not list: " + ex.Message + ")"; }
        }

        // ---------------------------------------------------------------- recoupler
        public static bool Juntar = true; public static float PuxarTempo = 0.6f, MaxAngulo = 30f;
        static TypeIndex tiChild, tiPrev, tiLEG, tiGT, tiConn, tiCheck; static bool tiRecOk;

        static unsafe void PassoRec(EntityManager em, Recoupler r)
        {
            if (!tiRecOk)
            {
                tiRecOk = true;
                tiChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>()); tiPrev = TypeManager.GetTypeIndex(Il2CppType.Of<PreviousParent>());
                tiLEG = TypeManager.GetTypeIndex(Il2CppType.Of<LinkedEntityGroup>()); tiGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
                tiConn = TypeManager.GetTypeIndex(Il2CppType.Of<SCConnectedJoint>()); tiCheck = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipPartConsistencyCheck>());
            }
            var pa = Ecs.Pai(em, r.bloco);
            if (!Ecs.EhPart(em, pa)) { r.estado = 0; r.armado = true; AtualizarLed(em, r); return; }   // garagem
            AtualizarLed(em, r);
            float s = 0f; var pe = Ecs.PortaDoTipo(em, r.bloco, Ecs.PortaEntrada);
            if (!Ecs.Nulo(pe)) Ecs.LerEntrada(em, pe, out s, out _, out _);
            bool alto = s >= 0.5f;
            if (!alto) r.armado = true;

            if (r.estado == 2)
            {
                // juntado: continua enquanto recoupler e decoupler estiverem na mesma part; rearma o decoupler quando o sinal dele baixar
                if (r.rearmarDecoupler) Rearmar(em, r);
                if (!Ecs.Vivo(em, r.decoupler) || Ecs.Pai(em, r.decoupler).Index != pa.Index)
                {
                    r.estado = 0; Registro.Info("SC_Recoupler " + r.bloco.Index + ": separated again (decoupler fired or broke)");
                }
                return;
            }
            if (r.estado == 1)
            {
                bool valido = Ecs.Vivo(em, r.joint) && Ecs.EhPart(em, r.partA) && Ecs.EhPart(em, r.partB) && pa.Index == r.partA.Index && Ecs.Vivo(em, r.decoupler) && Ecs.Pai(em, r.decoupler).Index == r.partB.Index && (!r.naAncora || Ecs.EhPart(em, r.ancora));
                if (!valido) { Soltar(em, r, "pieces changed (break or load)"); return; }
                if (!alto) { Soltar(em, r, "signal < 0.5"); return; }
                Puxar(em, r);
                return;
            }
            // livre: se nao esta armado, rearma quando se afastar de todo decoupler
            if (!alto) return;
            string motivo; var melhor = Procurar(em, r, pa, out motivo, out float dist);
            if (!r.armado)
            {
                if (Ecs.Nulo(melhor) && dist > Tolerancia + 0.2f) r.armado = true;
                else return;
            }
            if (Ecs.Nulo(melhor))
            {
                // no maximo 1 linha a cada 3 s (a distancia muda todo frame)
                r.tMotivo -= UnityEngine.Time.deltaTime;
                if (r.tMotivo <= 0f) { Registro.Info("SC_Recoupler " + r.bloco.Index + ": signal on, not coupling yet (" + motivo + ")"); r.motivoLog = motivo; r.tMotivo = 3f; }
                return;
            }
            // 0.7.9: so puxa se estiver ate MaxAngulo torto (girar 7 t em 90 graus com as rodas no chao explodia o carro)
            r.decoupler = melhor;
            PoseCerta(em, r, out _, out var axC, out var ayC, out _);
            Ecs.Eixos(em, r.bloco, out var rx0, out var ry0, out _);
            float ang = Angulo(rx0, ry0, axC, ayC);
            if (ang > MaxAngulo)
            {
                r.tMotivo -= UnityEngine.Time.deltaTime;
                if (r.tMotivo <= 0f) { Registro.Info("SC_Recoupler " + r.bloco.Index + ": signal on, not coupling yet (it is " + ang.ToString("F0") + " degrees off relative to Decoupler " + melhor.Index + "; the maximum is " + MaxAngulo.ToString("F0") + " — approach straighter)"); r.tMotivo = 3f; }
                return;
            }
            r.motivoLog = null; r.tMotivo = 0f;
            Travar(em, r, pa, Ecs.Pai(em, melhor), melhor);
            r.duracao = PuxarTempo + ang * 0.03f;
        }

        /// <summary>Decoupler ja disparado mais perto da face do recoupler, dentro da folga. motivo = por que nao achou.</summary>
        static Entity Procurar(EntityManager em, Recoupler r, Entity pa, out string motivo, out float distMin)
        {
            var pR = Ecs.Posicao(em, r.bloco);
            Entity melhor = Entity.Null; float eMelhor = 99f; motivo = "no Decoupler nearby"; distMin = 1e9f; string maisPerto = null;
            foreach (var d in Decouplers(em))
            {
                var pd = Ecs.Pai(em, d); if (!Ecs.EhPart(em, pd) || pd.Index == pa.Index) continue;
                Ecs.Eixos(em, d, out _, out var eyD, out _);
                var v = Ecs.Sub(pR, Ecs.Posicao(em, d)); float ao = Ecs.Dot(v, eyD);
                float lat = Ecs.Comp(Ecs.Sub(v, Ecs.Mul(eyD, ao)));
                float erro = System.Math.Abs(ao - 0.5f) + lat;
                float dd = Ecs.Comp(Ecs.Sub(v, Ecs.Mul(eyD, 0.5f)));
                if (dd < distMin) { distMin = dd; maisPerto = "Decoupler " + d.Index + " at " + dd.ToString("F2") + " m from the release face (along " + (ao - 0.5f).ToString("F2") + ", sideways " + lat.ToString("F2") + ")"; }
                if (ao < 0.5f - Tolerancia || ao > 0.5f + Tolerancia || lat > Tolerancia) continue;
                int presos = 0; try { presos = em.GetBufferLength(d, Ecs.tiDecJ); } catch (Exception) { }
                if (presos > 0) { motivo = "Decoupler " + d.Index + " has not decoupled yet"; continue; }
                if (erro < eMelhor) { eMelhor = erro; melhor = d; }
            }
            if (Ecs.Nulo(melhor) && motivo == "no Decoupler nearby" && maisPerto != null) motivo = "closest: " + maisPerto + "; tolerance " + Tolerancia.ToString("F2") + " m";
            return melhor;
        }

        static float Angulo(float3 ax, float3 ay, float3 bx, float3 by)
        {
            float c = System.Math.Min(Ecs.Dot(Ecs.Norm(ax), Ecs.Norm(bx)), Ecs.Dot(Ecs.Norm(ay), Ecs.Norm(by)));
            return (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, c))) * 180.0 / System.Math.PI);
        }
        static float3 X1 => new float3 { x = 1f }; static float3 Y1 => new float3 { y = 1f };
        static quaternion QMul(quaternion a, quaternion b)
        {
            float4 p = a.value, q = b.value;
            return new quaternion { value = new float4 { x = p.w * q.x + p.x * q.w + p.y * q.z - p.z * q.y, y = p.w * q.y - p.x * q.z + p.y * q.w + p.z * q.x, z = p.w * q.z + p.x * q.y - p.y * q.x + p.z * q.w, w = p.w * q.w - p.x * q.x - p.y * q.y - p.z * q.z } };
        }
        static float3 Snap(float3 v) { float ax = System.Math.Abs(v.x), ay = System.Math.Abs(v.y), az = System.Math.Abs(v.z); return ax >= ay && ax >= az ? new float3 { x = System.Math.Sign(v.x) } : ay >= az ? new float3 { y = System.Math.Sign(v.y) } : new float3 { z = System.Math.Sign(v.z) }; }
        static float3 NoMundo(float3 l, float3 ex, float3 ey, float3 ez) => Ecs.Add(Ecs.Add(Ecs.Mul(ex, l.x), Ecs.Mul(ey, l.y)), Ecs.Mul(ez, l.z));

        /// <summary>Pose certa do recoupler (no mundo, agora): ponto + eixos X e Y. "original" = eram vizinhos na garagem.</summary>
        static unsafe void PoseCerta(EntityManager em, Recoupler r, out float3 p, out float3 ax, out float3 ay, out bool original)
        {
            Ecs.Eixos(em, r.decoupler, out var dx, out var dy, out var dz); var pd = Ecs.Posicao(em, r.decoupler);
            original = false;
            try
            {
                if (em.HasComponent(r.bloco, ComponentType.ReadOnly(tiGT)) && em.HasComponent(r.decoupler, ComponentType.ReadOnly(tiGT)))
                {
                    var gr = *(Ecs.TemC(em, r.bloco, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(r.bloco, tiGT) : null); var gd = *(Ecs.TemC(em, r.decoupler, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(r.decoupler, tiGT) : null);
                    var qi = Ecs.Inv(gd.rot);
                    var tp = Ecs.Rot(qi, Ecs.Sub(gr.pos, gd.pos));
                    if (Ecs.Dist(tp, new float3 { y = 0.5f }) < 0.02f)
                    {
                        original = true;
                        p = Ecs.Add(pd, NoMundo(tp, dx, dy, dz));
                        ax = NoMundo(Ecs.Rot(qi, Ecs.Rot(gr.rot, X1)), dx, dy, dz); ay = NoMundo(Ecs.Rot(qi, Ecs.Rot(gr.rot, Y1)), dx, dy, dz);
                        return;
                    }
                }
            }
            catch (Exception) { }
            // encostado reto: centro a 0,5 m na face que solta, eixos do recoupler arredondados pros do decoupler
            Ecs.Eixos(em, r.bloco, out var rx, out var ry, out _);
            var lx = Snap(new float3 { x = Ecs.Dot(rx, dx), y = Ecs.Dot(rx, dy), z = Ecs.Dot(rx, dz) });
            var ly = Snap(new float3 { x = Ecs.Dot(ry, dx), y = Ecs.Dot(ry, dy), z = Ecs.Dot(ry, dz) });
            if (System.Math.Abs(Ecs.Dot(lx, ly)) > 0.5f) { lx = new float3 { x = Ecs.Dot(rx, dx), y = Ecs.Dot(rx, dy), z = Ecs.Dot(rx, dz) }; ly = new float3 { x = Ecs.Dot(ry, dx), y = Ecs.Dot(ry, dy), z = Ecs.Dot(ry, dz) }; }
            p = Ecs.Add(pd, Ecs.Mul(dy, 0.5f)); ax = Ecs.Norm(NoMundo(lx, dx, dy, dz)); ay = Ecs.Norm(NoMundo(ly, dx, dy, dz));
        }

        static unsafe void Travar(EntityManager em, Recoupler r, Entity pa, Entity pb, Entity dec)
        {
            r.decoupler = dec; r.partA = pa; r.partB = pb;
            // quem segura durante o puxao: se o decoupler esta no topo de um pistao, a BASE do pistao (nave, pesada) — senao o
            // topo leve (110 kg) e' que ia ate o carro (7 t). O pistao mantem o topo parado em relacao a base.
            var baseP = Juntas.BaseDoPistao(pb); r.naAncora = !Ecs.Nulo(baseP) && Ecs.EhPart(em, baseP) && baseP.Index != pa.Index; r.ancora = r.naAncora ? baseP : pb;
            var pR = Ecs.Posicao(em, r.bloco); Ecs.Eixos(em, r.bloco, out var rx, out var ry, out _);
            r.fb = Ecs.Frame(em, pa, pR, rx, ry);                  // no pedaco do recoupler: o proprio recoupler
            r.fa0 = Ecs.Frame(em, r.ancora, pR, rx, ry);           // em quem segura: onde ele esta agora
            PoseCerta(em, r, out var pC, out var axC, out var ayC, out r.original);
            r.fa1 = Ecs.Frame(em, r.ancora, pC, axC, ayC);         // ... e onde ele tem que ficar
            var pj = PhysicsJoint.CreateFixed(r.fa0, r.fb);
            var par = new PhysicsConstrainedBodyPair(); par.Entities = new EntityPair { EntityA = r.ancora, EntityB = pa }; par.EnableCollision = 0;
            var tipos = new Il2CppStructArray<ComponentType>(3);
            tipos[0] = ComponentType.ReadWrite(Ecs.tiPar); tipos[1] = ComponentType.ReadWrite(Ecs.tiJoint); tipos[2] = ComponentType.ReadWrite(Ecs.tiPWI);
            var e = em.CreateEntity(tipos);
            *(Ecs.TemC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = par;
            *(Ecs.TemC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = pj;
            r.joint = e; r.estado = 1; r.armado = false; r.tPuxar = 0f;
            Registro.Info("SC_Recoupler " + r.bloco.Index + ": COUPLING to Decoupler " + dec.Index + " (part " + pa.Index + " " + Ecs.Massa(em, pa).ToString("F0") + " kg + part " + pb.Index + " " + Ecs.Massa(em, pb).ToString("F0") + " kg), pulling to the " + (r.original ? "original garage pose" : "straight flush pose") + (r.naAncora ? " anchored to the piston base (part " + r.ancora.Index + " " + Ecs.Massa(em, r.ancora).ToString("F0") + " kg)" : "") + ", initial error " + Ecs.Dist(pR, pC).ToString("F2") + " m");
        }

        /// <summary>Leva o frame A de fa0 ate fa1 em PuxarTempo; quando chega e esta alinhado, junta (caso original).</summary>
        static unsafe void Puxar(EntityManager em, Recoupler r)
        {
            if (r.tPuxar < 0f) return;   // ja terminou (travado sem juntar)
            r.tPuxar += UnityEngine.Time.deltaTime;
            float t = r.duracao <= 0f ? 1f : System.Math.Min(1f, r.tPuxar / r.duracao); t = t * t * (3f - 2f * t);
            var fa = new BodyFrame();
            fa.Position = Ecs.Add(Ecs.Mul(r.fa0.Position, 1f - t), Ecs.Mul(r.fa1.Position, t));
            var ax = Ecs.Norm(Ecs.Add(Ecs.Mul(r.fa0.Axis, 1f - t), Ecs.Mul(r.fa1.Axis, t)));
            var pp = Ecs.Add(Ecs.Mul(r.fa0.PerpendicularAxis, 1f - t), Ecs.Mul(r.fa1.PerpendicularAxis, t));
            pp = Ecs.Norm(Ecs.Sub(pp, Ecs.Mul(ax, Ecs.Dot(pp, ax))));
            fa.Axis = ax; fa.PerpendicularAxis = pp;
            var pj = (Ecs.TemC(em, r.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(r.joint, Ecs.tiJoint) : null);
            int v = pj->m_Version; var novo = PhysicsJoint.CreateFixed(fa, r.fb); novo.m_Version = (byte)(v + 1); *pj = novo;
            if (t < 1f) return;
            // chegou: mede o erro real
            PoseCerta(em, r, out var pC, out var axC, out var ayC, out _);
            Ecs.Eixos(em, r.bloco, out var rx, out var ry, out _);
            float dErro = Ecs.Dist(Ecs.Posicao(em, r.bloco), pC);
            float aErro = (float)(System.Math.Acos(System.Math.Min(1f, System.Math.Min(Ecs.Dot(rx, axC), Ecs.Dot(ry, ayC)))) * 180.0 / System.Math.PI);
            if ((dErro > 0.02f || aErro > 2f) && r.tPuxar < r.duracao + 2f) return;   // espera assentar ate 2 s
            if (!r.original || !Juntar)
            {
                r.tPuxar = -1f; TravarNoDecoupler(em, r);
                Registro.Info("SC_Recoupler " + r.bloco.Index + ": RECOUPLED (locked, error " + (dErro * 100f).ToString("F1") + " cm / " + aErro.ToString("F1") + " degrees)" + (!r.original ? " — they were not neighbors in the garage, so the parts are not merged" : " — merge disabled in config"));
                return;
            }
            if (dErro > 0.05f || aErro > 5f) { r.tPuxar = -1f; TravarNoDecoupler(em, r); Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": did not align (" + (dErro * 100f).ToString("F1") + " cm / " + aErro.ToString("F1") + " degrees) — only locked"); return; }
            string falha = JuntarParts(em, r);
            if (falha != null) { r.tPuxar = -1f; TravarNoDecoupler(em, r); Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": merge failed (" + falha + ") — only locked"); }
        }

        /// <summary>Troca o joint de puxar (preso na nave) por um fixo entre o decoupler e o recoupler na pose atual — assim o
        /// pistao continua movendo o que foi travado.</summary>
        static unsafe void TravarNoDecoupler(EntityManager em, Recoupler r)
        {
            if (!r.naAncora) return;
            try
            {
                var pR = Ecs.Posicao(em, r.bloco); Ecs.Eixos(em, r.bloco, out var rx, out var ry, out _);
                var fa = Ecs.Frame(em, r.partB, pR, rx, ry); var fb = Ecs.Frame(em, r.partA, pR, rx, ry);
                var pj = (Ecs.TemC(em, r.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(r.joint, Ecs.tiJoint) : null); int v = pj->m_Version;
                var novo = PhysicsJoint.CreateFixed(fa, fb); novo.m_Version = (byte)(v + 1); *pj = novo;
                var par = (Ecs.TemC(em, r.joint, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(r.joint, Ecs.tiPar) : null);
                par->Entities = new EntityPair { EntityA = r.partB, EntityB = r.partA };
                r.naAncora = false; r.ancora = r.partB;
            }
            catch (Exception ex) { Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": handing the lock over to the decoupler failed: " + ex.Message); }
        }

        // ---- BufferHeader (Entities): {Pointer, Length, Capacity}; elementos em Pointer (heap) ou logo depois do cabecalho (inline)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] unsafe struct Cab { public byte* Pointer; public int Length; public int Capacity; }
        static unsafe Cab* Cabecalho(EntityManager em, Entity e, TypeIndex ti)
        {
            var h = (Ecs.TemC(em, e, ti) ? (Cab*)em.GetComponentDataRawRW(e, ti) : null); if (h == null) return null;
            int n = em.GetBufferLength(e, ti); var raw = (byte*)em.GetBufferRawRW(e, ti);
            byte* elems = h->Pointer != null ? h->Pointer : (byte*)h + sizeof(Cab);
            if (h->Length != n || elems != raw || h->Capacity < n) return null;
            return h;
        }
        static unsafe byte* Elems(Cab* h) => h->Pointer != null ? h->Pointer : (byte*)h + sizeof(Cab);
        static unsafe void Anexar(Cab* h, void* item, int tam)
        {
            if (h->Length >= h->Capacity)
            {
                int nova = System.Math.Max(8, h->Capacity * 2);
                var p = (byte*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc((long)nova * tam, 16, Unity.Collections.Allocator.Persistent);
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(p, Elems(h), (long)h->Length * tam);
                if (h->Pointer != null) Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(h->Pointer, Unity.Collections.Allocator.Persistent);
                h->Pointer = p; h->Capacity = nova;
            }
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(Elems(h) + (long)h->Length * tam, item, tam);
            h->Length++;
        }
        static unsafe bool TemInt(Cab* h, int v) { var p = (int*)Elems(h); for (int i = 0; i < h->Length; i++) if (p[i] == v) return true; return false; }

        /// <summary>O contrario do decoupler. Retorna null se deu certo, senao o motivo.</summary>
        static unsafe string JuntarParts(EntityManager em, Recoupler r)
        {
            var dec = r.decoupler; var pDec = r.partB; var pRec = r.partA;
            // 1) joint do jogo entre decoupler e recoupler (o que o decoupler quebrou)
            var joints = Ecs.Joints(em, out int n); if (joints == null) return "no joint list";
            int k = -1;
            for (int i = 0; i < n; i++) { var sj = &joints[i]; if ((sj->_a.Index == dec.Index && sj->_b.Index == r.bloco.Index) || (sj->_b.Index == dec.Index && sj->_a.Index == r.bloco.Index)) { k = i; break; } }
            if (k < 0) return "game joint between the Decoupler and the Recoupler not found (were they not attached in the garage?)";
            // 2) cabecalhos dos buffers (tudo validado antes de mexer em qualquer coisa)
            var cDec = Cabecalho(em, dec, tiConn); var cRec = Cabecalho(em, r.bloco, tiConn);
            var chDec = Cabecalho(em, pDec, tiChild); var chRec = Cabecalho(em, pRec, tiChild);
            if (cDec == null || cRec == null) return "SCConnectedJoint buffer has an unexpected format";
            if (chDec == null || chRec == null) return "the parts' Child buffer has an unexpected format";
            var filhos = new List<Entity>(); var pf = (Entity*)Elems(chRec); for (int i = 0; i < chRec->Length; i++) filhos.Add(pf[i]);
            if (filhos.Count == 0) return "recoupler part has no children";
            // transformacao garagem: pose na part do decoupler = LT_dec * inv(GT_dec) * GT_filho
            var ltDec = *(Ecs.TemC(em, dec, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(dec, Ecs.tiLT) : null);
            var gDec = *(Ecs.TemC(em, dec, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(dec, tiGT) : null); var qiG = Ecs.Inv(gDec.rot);
            var novos = new List<(Entity e, LocalTransform lt)>();
            foreach (var f in filhos)
            {
                if (!em.HasComponent(f, ComponentType.ReadOnly(tiGT)) || !em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiLT))) return "block " + f.Index + " has no GarageTransform/LocalTransform";
                var g = *(Ecs.TemC(em, f, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(f, tiGT) : null); var lt = *(Ecs.TemC(em, f, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(f, Ecs.tiLT) : null);
                var rel = Ecs.Rot(qiG, Ecs.Sub(g.pos, gDec.pos));
                lt.Position = Ecs.Add(ltDec.Position, Ecs.Rot(ltDec.Rotation, rel));
                lt.Rotation = QMul(ltDec.Rotation, QMul(qiG, g.rot));
                novos.Add((f, lt));
            }
            // 3) aplica: blocos -> part do decoupler
            bool temPrev = false;
            foreach (var (f, lt) in novos)
            {
                *(Ecs.TemC(em, f, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(f, Ecs.tiLT) : null) = lt;
                *(Ecs.TemC(em, f, Ecs.tiParent) ? (Entity*)em.GetComponentDataRawRW(f, Ecs.tiParent) : null) = pDec;
                if (em.HasComponent(f, ComponentType.ReadOnly(tiPrev))) { *(Ecs.TemC(em, f, tiPrev) ? (Entity*)em.GetComponentDataRawRW(f, tiPrev) : null) = pDec; temPrev = true; }
            }
            chDec = Cabecalho(em, pDec, tiChild); chRec = Cabecalho(em, pRec, tiChild);   // (re-le: os ponteiros podem mudar com escritas)
            if (chDec == null || chRec == null) return "Child changed midway (blocks already moved!)";
            foreach (var (f, _) in novos) { var ff = f; Anexar(chDec, &ff, sizeof(Entity)); }
            chRec->Length = 0;
            // 4) religa o joint e rearma o decoupler
            var sjk = &joints[k]; sjk->_currentStrain = 0f; if (sjk->_breakForce <= 0f) sjk->_breakForce = 3300f;
            cDec = Cabecalho(em, dec, tiConn); cRec = Cabecalho(em, r.bloco, tiConn);
            if (cDec != null && !TemInt(cDec, k)) Anexar(cDec, &k, 4);
            if (cRec != null && !TemInt(cRec, k)) Anexar(cRec, &k, 4);
            r.jointJogo = k; r.rearmarDecoupler = true;
            // 0.7.9: a lista de ligacoes ATIVAS do jogo (SpaceshipSingleton._connectedJoints, UnsafeList<int>): so o que esta nela
            // e' checado pra quebrar. Sem isso o decoupler mandava soltar e nada acontecia.
            string ativa = AtivarJoint(em, k);
            // 5) solta o joint fixo, some com a part vazia do recoupler e pede pro jogo recalcular a part do decoupler
            try { if (Ecs.Vivo(em, r.joint)) em.DestroyEntity(r.joint); } catch (Exception) { }
            r.joint = Entity.Null;
            string leg = "";
            try
            {
                if (em.HasComponent(pRec, ComponentType.ReadOnly(tiLEG)))
                {
                    var cl = Cabecalho(em, pRec, tiLEG);
                    if (cl != null) { var le = (Entity*)Elems(cl); int m = 0; for (int i = 0; i < cl->Length; i++) if (le[i].Index == pRec.Index) le[m++] = le[i]; leg = " (LinkedEntityGroup " + cl->Length + " -> " + m + ")"; cl->Length = m; }
                }
            }
            catch (Exception ex) { leg = " (LEG: " + ex.Message + ")"; }
            try { em.DestroyEntity(pRec); } catch (Exception ex) { Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": destroying the empty part failed: " + ex.Message); }
            em.AddComponent(pDec, ComponentType.ReadWrite(tiCheck));
            r.estado = 2; r.partA = Entity.Null; r.partB = Entity.Null;
            Registro.Info("SC_Recoupler " + r.bloco.Index + ": MERGED — " + novos.Count + " block(s) moved to part " + pDec.Index + ", game joint " + k + " re-enabled (" + ativa + ")" + (temPrev ? ", PreviousParent updated" : "") + leg + "; the game will recompute the part");
            Rearmar(em, r);
            return null;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] unsafe struct ListaInt { public int* Ptr; public int Length; public int Capacity; public ushort AlocIndex; public ushort AlocVersao; }
        static unsafe string AtivarJoint(EntityManager em, int k)
        {
            try
            {
                var q = Ecs.Lista(Ecs.QSing); if (q.Count == 0) return "no SpaceshipSingleton";
                var sing = (Ecs.TemC(em, q[0], Ecs.tiSing) ? (SpaceshipSingleton*)em.GetComponentDataRawRO(q[0], Ecs.tiSing) : null);
                var l = (ListaInt*)sing->_connectedJoints; if (l == null || l->Ptr == null) return "active list is null";
                if (l->Length < 0 || l->Length > l->Capacity || l->Capacity > 10000000) return "active list has an odd format";
                for (int i = 0; i < l->Length; i++) if (l->Ptr[i] == k) return "was already active";
                if (l->Length >= l->Capacity)
                {
                    if (l->AlocIndex != 4) return "list full and allocator " + l->AlocIndex + " (not Persistent) — NOT activated";
                    int nova = System.Math.Max(64, l->Capacity * 2);
                    var p = (int*)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc((long)nova * 4, 16, Unity.Collections.Allocator.Persistent);
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(p, l->Ptr, (long)l->Length * 4);
                    Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(l->Ptr, Unity.Collections.Allocator.Persistent);
                    l->Ptr = p; l->Capacity = nova;
                }
                l->Ptr[l->Length] = k; l->Length++;
                return "active again";
            }
            catch (Exception ex) { return "activation failed: " + ex.Message; }
        }

        /// <summary>Poe o joint religado de volta na lista do decoupler (pra ele poder soltar de novo) — so quando o sinal dele
        /// estiver &lt; 0,5, senao ele dispararia na hora.</summary>
        static unsafe void Rearmar(EntityManager em, Recoupler r)
        {
            try
            {
                if (!Ecs.Vivo(em, r.decoupler) || r.jointJogo < 0) { r.rearmarDecoupler = false; return; }
                Ecs.Sinal(em, r.decoupler, out float sd, out _);
                if (sd >= 0.5f) return;
                var c = Cabecalho(em, r.decoupler, Ecs.tiDecJ); if (c == null) { r.rearmarDecoupler = false; Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": could not re-arm the decoupler (buffer)"); return; }
                int k = r.jointJogo; if (!TemInt(c, k)) Anexar(c, &k, 4);
                r.rearmarDecoupler = false;
                Registro.Info("SC_Recoupler " + r.bloco.Index + ": Decoupler " + r.decoupler.Index + " re-armed (can release again)");
            }
            catch (Exception ex) { r.rearmarDecoupler = false; Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": re-arming decoupler: " + ex.Message); }
        }

        // ---- 0.11.2 (Andre: "um detalhezinho quando o recoupler e o decoupler estiverem presos"): dois leds verdes (renderers
        // extras "LedA"/"LedB" do prefab) ficam 2 cm DENTRO do painel das faces +Z/-Z (apagados/escondidos); acoplado (travado ou
        // juntado) eles saem 3 mm pra fora do painel. Mesmo truque da haste do pistao: mexe o LocalTransform da entity filha.
        const float LedX = 0.12f, LedY = 0.19f, LedRepousoZ = 0.224f, LedDesloc = 0.023f;
        static unsafe void AtualizarLed(EntityManager em, Recoupler r)
        {
            // 0.12.1 (Andre): aceso tambem no comeco da simulacao, enquanto o recoupler esta colado num decoupler (mesma part, a menos de
            // 0,6 m) que ainda nao disparou — mostra que esta preso
            r.tEncostado += UnityEngine.Time.deltaTime;
            if (r.tEncostado >= 0.5f)
            {
                r.tEncostado = 0f; r.encostado = false;
                try
                {
                    var pa = Ecs.Pai(em, r.bloco);
                    if (r.estado == 0 && Ecs.EhPart(em, pa))
                    {
                        var pr = Ecs.Posicao(em, r.bloco);
                        foreach (var d in Decouplers(em))
                        {
                            if (Ecs.Pai(em, d).Index != pa.Index || Ecs.Dist(Ecs.Posicao(em, d), pr) > 0.6f) continue;
                            int presos = 0; try { presos = em.GetBufferLength(d, Ecs.tiDecJ); } catch (Exception) { }
                            if (presos > 0) { r.encostado = true; break; }
                        }
                    }
                }
                catch (Exception) { }
            }
            bool aceso = (r.estado == 1 && !r.naAncora && !Ecs.Nulo(r.joint)) || r.estado == 2 || r.encostado;
            r.tLed += UnityEngine.Time.deltaTime;
            if (r.leds == null || r.tLed >= 5f)
            {
                r.tLed = 0f;
                var achados = new List<Entity>(); var rep = new List<float3>();
                foreach (var f in Ecs.Filhos(em, r.bloco))
                {
                    var p = Ecs.LT(em, f).Position;
                    if (System.Math.Abs(System.Math.Abs(p.x) - LedX) < 0.003f && System.Math.Abs(p.y - LedY) < 0.003f && (System.Math.Abs(System.Math.Abs(p.z) - LedRepousoZ) < 0.003f || System.Math.Abs(System.Math.Abs(p.z) - (LedRepousoZ + LedDesloc)) < 0.003f))
                    {
                        achados.Add(f); rep.Add(new float3 { x = p.x, y = p.y, z = p.z < 0f ? -LedRepousoZ : LedRepousoZ });
                    }
                }
                bool primeira = r.leds == null;
                r.leds = achados.ToArray(); r.ledRepouso = rep.ToArray();
                if (primeira) { if (r.leds.Length == 0) Registro.Aviso("SC_Recoupler " + r.bloco.Index + ": LEDs not found among the block's children (old prefab? rebuild the bundle in the Modkit)"); r.ledAceso = !aceso; }
            }
            if (aceso == r.ledAceso || r.leds.Length == 0) return;
            r.ledAceso = aceso;
            for (int i = 0; i < r.leds.Length; i++)
            {
                if (!Ecs.Vivo(em, r.leds[i])) continue;
                var lt = (Ecs.TemC(em, r.leds[i], Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(r.leds[i], Ecs.tiLT) : null); if (lt == null) continue;
                var p = r.ledRepouso[i];
                lt->Position = new float3 { x = p.x, y = p.y, z = aceso ? p.z + (p.z < 0f ? -LedDesloc : LedDesloc) : p.z };
            }
            Registro.Info("SC_Recoupler " + r.bloco.Index + ": LED " + (aceso ? (r.encostado ? "on (held by a decoupler that hasn't fired yet)" : "on (coupled)") : "off"));
        }

        static void Soltar(EntityManager em, Recoupler r, string motivo)
        {
            if (Ecs.Nulo(r.joint)) { r.estado = 0; return; }
            try { if (Ecs.Vivo(em, r.joint)) em.DestroyEntity(r.joint); } catch (Exception) { }
            Registro.Info("SC_Recoupler " + r.bloco.Index + ": released (" + motivo + ")");
            r.joint = Entity.Null; r.partA = Entity.Null; r.partB = Entity.Null; r.estado = 0;
        }

        static List<Entity> Decouplers(EntityManager em)
        {
            if (!qDecOk) { var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(Ecs.tiDecTipo); a[1] = ComponentType.ReadOnly(Ecs.tiLTW); qDec = em.CreateEntityQuery(a); qDecOk = true; }
            return Ecs.Lista(qDec);
        }

        // ---------------------------------------------------------------- beacon
        static unsafe void PassoBeacon(EntityManager em, Beacon b)
        {
            if (tiIcone.Value == 0) return;
            if (!Ecs.EhPart(em, Ecs.Pai(em, b.bloco))) { ApagarFantasma(em, b); return; }   // so em voo
            // 0.7.6: o beacon do proprio veiculo nao aparece no radar dele. O radar que o jogador olha e' o do veiculo onde ele
            // esta, entao: jogador a menos de EsconderPerto m do beacon -> some do radar.
            // 0.7.8: esconde o beacon que esta no MESMO veiculo do radar mais perto do jogador (o radar que ele esta olhando).
            // Veiculo = parts ligadas por bearing/pistao/recoupler. Sem radar em voo: volta pra regra da distancia.
            AtualizarVeiculo(em);
            bool esconder; string porque;
            if (!Ecs.Nulo(radarPerto))
            {
                esconder = veiculo.Contains(Ecs.Pai(em, b.bloco).Index);
                porque = esconder ? "same vehicle as radar " + radarPerto.Index : "other vehicle, radar " + radarPerto.Index;
            }
            else
            {
                float dj = DistJogador(em, Ecs.Posicao(em, b.bloco));
                esconder = EsconderPerto > 0f && dj < EsconderPerto; porque = "no radar in flight; player at " + (dj > 1e8f ? "?" : dj.ToString("F0")) + " m";
            }
            // 0.17.3 (Andre: com a camera livre do Replay o jogador fica no carro e o beacon do carro sumia de TODOS os radares — o radar
            // nao tem filtro por radar: ou o fantasma existe pra todos ou pra ninguem). hide_on_own_vehicle = false: nunca esconde.
            if (!EsconderProprio && esconder) { esconder = false; porque += " — hide_on_own_vehicle is off"; }
            if (esconder != b.escondido) { b.escondido = esconder; Registro.Info("SC_Beacon " + b.bloco.Index + (esconder ? ": hidden from radar (" : ": visible on radar (") + porque + ")"); }
            // (0.11.2: a etiqueta saiu de todos os blocos — Andre)
            // 0.11.1 (Andre: "faz ele consumir energia tambem, 5 P"): pede 5 P/s na porta de energia; sem energia fica fora do radar
            Ecs.Consumo(em, b.bloco, ConsumoBeacon);
            byte estadoE = 255; bool energia = ConsumoBeacon <= 0f || Ecs.TemEnergia(em, b.bloco, out estadoE);
            if (estadoE != b.estadoEnergia) { b.estadoEnergia = estadoE; Registro.Info("SC_Beacon " + b.bloco.Index + ": power " + Juntas.NomeEstado(estadoE)); }
            if (!energia != b.semEnergia) { b.semEnergia = !energia; Registro.Info("SC_Beacon " + b.bloco.Index + (energia ? ": powered, back on radar" : ": NO power — off radar")); }
            if (esconder || !energia) { ApagarFantasma(em, b); return; }
            if (!Ecs.Vivo(em, b.fantasma))
            {
                var tipos = new Il2CppStructArray<ComponentType>(2);
                tipos[0] = ComponentType.ReadWrite(Ecs.tiLTW); tipos[1] = ComponentType.ReadWrite(tiIcone);
                b.fantasma = em.CreateEntity(tipos);
                fantasmasCriados.Add(Ecs.K(b.fantasma));   // 0.19.5: todo fantasma fica registrado pra ser destruido se ficar orfao
                PorTags(em, b.fantasma);
                *(Ecs.TemC(em, b.fantasma, tiIcone) ? (uint*)em.GetComponentDataRawRW(b.fantasma, tiIcone) : null) = RadarIcone.SlotDe(b.icone);
                Registro.Info("SC_Beacon " + b.bloco.Index + ": on radar (icon " + RadarIcone.SlotDe(b.icone) + " = " + BeaconIcone.Nomes[b.icone] + ", entity " + b.fantasma.Index + ")");
            }
            *(Ecs.TemC(em, b.fantasma, Ecs.tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRW(b.fantasma, Ecs.tiLTW) : null) = *(Ecs.TemC(em, b.bloco, Ecs.tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRO(b.bloco, Ecs.tiLTW) : null);
            MundoDoFantasma(em, b);
            var ic = (Ecs.TemC(em, b.fantasma, tiIcone) ? (uint*)em.GetComponentDataRawRW(b.fantasma, tiIcone) : null);
            uint quer = RadarIcone.SlotDe(b.icone);
            if (*ic != quer) { Registro.Info("SC_Beacon " + b.bloco.Index + ": radar icon " + *ic + " -> " + quer + " (" + BeaconIcone.Nomes[b.icone] + ")"); *ic = quer; }
        }
        static readonly List<(float3 pos, uint icone)> iconesRadar = new List<(float3, uint)>();
        /// <summary>0.19.2: chamado no postfix do TransformSystemGroup — escreve os icones dos beacons no buffer do radar.</summary>
        public static void EscreverRadar(EntityManager em) { RadarEscrita.Escrever(em, iconesRadar); }
        static Entity radarPerto; static readonly HashSet<int> veiculo = new HashSet<int>(); static float tVeiculo = 9f; static int frameVeiculo = -1;
        static ulong hRadar, hRadarM;
        static void AtualizarVeiculo(EntityManager em)
        {
            if (frameVeiculo == UnityEngine.Time.frameCount) return; frameVeiculo = UnityEngine.Time.frameCount;
            tVeiculo += UnityEngine.Time.deltaTime; if (tVeiculo < 0.5f) return; tVeiculo = 0f;
            try
            {
                if (hRadar == 0) { hRadar = Ecs.HashPrefab("SC_Radar"); hRadarM = Ecs.HashPrefab("SC_Radar M"); }
                var pj = PosJogador(em, out bool temJog);
                Entity melhor = Entity.Null; float dm = 1e9f;
                foreach (var e in Ecs.Componentes(em))
                {
                    ulong h = Ecs.Prefab(em, e); if (h != hRadar && h != hRadarM) continue;
                    if (!Ecs.EhPart(em, Ecs.Pai(em, e))) continue;
                    float d = temJog ? Ecs.Dist(Ecs.Posicao(em, e), pj) : 0f; if (d < dm) { dm = d; melhor = e; }
                }
                radarPerto = melhor; veiculo.Clear();
                if (Ecs.Nulo(melhor)) return;
                // grupo de parts ligadas ao radar pelos joints do mod
                var viz = new Dictionary<int, List<int>>();
                void Liga(Entity a, Entity b2) { if (Ecs.Nulo(a) || Ecs.Nulo(b2)) return; if (!viz.TryGetValue(a.Index, out var la)) viz[a.Index] = la = new List<int>(); la.Add(b2.Index); if (!viz.TryGetValue(b2.Index, out var lb)) viz[b2.Index] = lb = new List<int>(); lb.Add(a.Index); }
                foreach (var (a, b2) in Juntas.Ligacoes()) Liga(a, b2);
                foreach (var r in recs.Values) if (r.estado == 1) { Liga(r.partA, r.partB); Liga(r.partA, r.ancora); }
                var fila = new Queue<int>(); int ini = Ecs.Pai(em, melhor).Index; veiculo.Add(ini); fila.Enqueue(ini);
                while (fila.Count > 0) { int x = fila.Dequeue(); if (viz.TryGetValue(x, out var l)) foreach (var y in l) if (veiculo.Add(y)) fila.Enqueue(y); }
            }
            catch (Exception ex) { radarPerto = Entity.Null; if (!avisoVeic) { avisoVeic = true; Registro.Aviso("beacon: finding the closest radar failed: " + ex.Message); } }
        }
        static bool avisoVeic;

        static float3 PosJogador(EntityManager em, out bool tem)
        {
            tem = false;
            try
            {
                if (!jogOk) { jogOk = true; tiJogador = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiJogador); a[1] = ComponentType.ReadOnly(Ecs.tiLTW); qJogador = em.CreateEntityQuery(a); }
                foreach (var e in Ecs.Lista(qJogador)) { tem = true; return Ecs.Posicao(em, e); }
            }
            catch (Exception) { }
            return float3.zero;
        }

        // 0.7.11: os icones do jogo tem WormholeWorldIndex (em que "mundo" esta), ActiveWormholeWorld (marca do mundo ativo),
        // UniversePosition (double3) e LocalTransform. O radar provavelmente so pega os do mundo ativo -> o fantasma copia essas
        // 4 da peca do beacon (bloco ou part), todo frame.
        static TypeIndex tiWWI, tiAWW, tiUP; static bool mundoOk, mundoLogado;
        static unsafe void MundoDoFantasma(EntityManager em, Beacon b)
        {
            try
            {
                if (!mundoOk) { mundoOk = true; tiWWI = TypeManager.GetTypeIndex(Il2CppType.Of<WormholeWorldIndex>()); tiAWW = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>()); tiUP = TypeManager.GetTypeIndex(Il2CppType.Of<UniversePosition>()); }
                var g = b.fantasma; var part = Ecs.Pai(em, b.bloco);
                Entity Fonte(TypeIndex ti) => em.HasComponent(b.bloco, ComponentType.ReadOnly(ti)) ? b.bloco : (!Ecs.Nulo(part) && em.HasComponent(part, ComponentType.ReadOnly(ti))) ? part : Entity.Null;
                var fW = Fonte(tiWWI); var fA = Fonte(tiAWW); var fU = Fonte(tiUP);
                if (!em.HasComponent(g, ComponentType.ReadOnly(tiWWI))) em.AddComponent(g, ComponentType.ReadWrite(tiWWI));
                if (!em.HasComponent(g, ComponentType.ReadOnly(tiUP))) em.AddComponent(g, ComponentType.ReadWrite(tiUP));
                if (!em.HasComponent(g, ComponentType.ReadOnly(Ecs.tiLT))) em.AddComponent(g, ComponentType.ReadWrite(Ecs.tiLT));
                bool querAtivo = Ecs.Nulo(fA) ? true : true;   // o beacon esta no mundo em que o jogador esta voando
                if (querAtivo && !em.HasComponent(g, ComponentType.ReadOnly(tiAWW))) em.AddComponent(g, ComponentType.ReadWrite(tiAWW));
                if (!Ecs.Nulo(fW)) *(Ecs.TemC(em, g, tiWWI) ? (int*)em.GetComponentDataRawRW(g, tiWWI) : null) = *(Ecs.TemC(em, fW, tiWWI) ? (int*)em.GetComponentDataRawRO(fW, tiWWI) : null);
                Ecs.Pose(em, b.bloco, out var pos, out var rot);
                if (!Ecs.Nulo(fU))
                {
                    // posicao no universo do fantasma = a da fonte + (posicao do bloco - posicao da fonte)
                    var up = *(Ecs.TemC(em, fU, tiUP) ? (double3*)em.GetComponentDataRawRO(fU, tiUP) : null);
                    var pf = fU.Index == b.bloco.Index ? pos : Ecs.LT(em, fU).Position;
                    var d = Ecs.Sub(pos, pf);
                    *(Ecs.TemC(em, g, tiUP) ? (double3*)em.GetComponentDataRawRW(g, tiUP) : null) = new double3 { x = up.x + d.x, y = up.y + d.y, z = up.z + d.z };
                }
                var lt = new LocalTransform { Position = pos, Rotation = rot, Scale = 1f };
                *(Ecs.TemC(em, g, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(g, Ecs.tiLT) : null) = lt;
                if (!mundoLogado) { mundoLogado = true; Registro.Info("SC_Beacon " + b.bloco.Index + ": ghost with the game's world — WormholeWorldIndex from " + Qual(b, part, fW) + ", ActiveWormholeWorld " + (Ecs.Nulo(fA) ? "(neither block nor part has it; set anyway)" : "from " + Qual(b, part, fA)) + ", UniversePosition from " + Qual(b, part, fU)); }
            }
            catch (Exception ex) { if (!mundoLogado) { mundoLogado = true; Registro.Aviso("SC_Beacon: copying the world to the ghost failed: " + ex.Message); } }
        }
        static string Qual(Beacon b, Entity part, Entity f) => Ecs.Nulo(f) ? "none" : f.Index == b.bloco.Index ? "block" : "part";

        static float DistJogador(EntityManager em, float3 p)
        {
            try
            {
                if (!jogOk) { jogOk = true; tiJogador = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiJogador); a[1] = ComponentType.ReadOnly(Ecs.tiLTW); qJogador = em.CreateEntityQuery(a); }
                float melhor = 1e9f;
                foreach (var e in Ecs.Lista(qJogador)) { float d = Ecs.Dist(Ecs.Posicao(em, e), p); if (d < melhor) melhor = d; }
                return melhor;
            }
            catch (Exception) { return 1e9f; }
        }

        static void ApagarFantasma(EntityManager em, Beacon b)
        {
            try { if (Ecs.Vivo(em, b.fantasma)) em.DestroyEntity(b.fantasma); } catch (Exception) { }
            fantasmasCriados.Remove(Ecs.K(b.fantasma));
            b.fantasma = Entity.Null;
        }
        // 0.19.5 (log do Andre: "entities with RadarAppearanceIcon: 19 from the game" virando 42 ao longo da sessao; "NO free icon";
        // icones duplicados): na volta pra garagem o Esquecer limpava a lista de beacons SEM destruir os fantasmas, que o jogo passava a
        // tratar como icones dele (ocupavam todos os slots e apareciam no radar). Agora todo fantasma criado fica registrado e os que
        // nao pertencem a nenhum beacon sao destruidos (a cada 2 s e logo depois da descarga).
        static readonly HashSet<long> fantasmasCriados = new HashSet<long>(); static float tOrfaos = 9f;
        static void DestruirOrfaos(EntityManager em, float dt)
        {
            tOrfaos += dt; if (tOrfaos < 2f || fantasmasCriados.Count == 0) return; tOrfaos = 0f;
            var vivos = new HashSet<long>(); foreach (var b in beacons.Values) if (!Ecs.Nulo(b.fantasma)) vivos.Add(Ecs.K(b.fantasma));
            var orfaos = new List<long>(); foreach (var k in fantasmasCriados) if (!vivos.Contains(k)) orfaos.Add(k);
            int n = 0;
            foreach (var k in orfaos)
            {
                fantasmasCriados.Remove(k);
                var e = Ecs.DeK(k);
                try { if (Ecs.Vivo(em, e) && Ecs.TemC(em, e, tiIcone)) { em.DestroyEntity(e); n++; } } catch (Exception) { }
            }
            if (n > 0) { Registro.Info("radar: " + n + " orphan beacon icon entity(ies) destroyed"); RadarIcone.PedirRecheck = true; }
        }

        /// <summary>0.10.0: time rewind — o jogo refez as parts do momento salvo (se o carro estava juntado depois, volta separado).
        /// Solta todo recoupler e desarma: pra reacoplar, o sinal tem que baixar e subir de novo.</summary>
        public static void Rebobinou(EntityManager em)
        {
            foreach (var r in recs.Values)
            {
                if (r.estado != 0) Soltar(em, r, "time rewind");
                r.estado = 0; r.armado = false; r.tPuxar = 0f; r.rearmarDecoupler = false; r.naAncora = false; r.jointJogo = -1;
            }
        }

        public static void Esquecer() { recs.Clear(); beacons.Clear(); iconesRadar.Clear(); RadarEscrita.Esquecer(); }
        public static void Limpar(EntityManager em)
        {
            foreach (var r in recs.Values) Soltar(em, r, "cleaning up");
            foreach (var b in beacons.Values) ApagarFantasma(em, b);
            recs.Clear(); beacons.Clear();
        }
    }
}
