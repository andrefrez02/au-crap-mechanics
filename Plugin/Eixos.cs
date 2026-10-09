using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Unity.Entities;
using UnityEngine;

namespace Mecanica
{
    /// <summary>0.15.0 — Drive Shaft (Andre: "o motor divide a forca por bearing", "cabo proprio", "Differential", "inverter").
    /// O jogo so tem 4 tipos de cabo e a regra de encaixe e o desenho sao do codigo compilado dele, entao o Drive Shaft e' um cabo
    /// do tipo PLASMA do mod (item proprio, cor de ferro). O mod le a transmissao sozinho: da saida de eixo do motor (porta plasma)
    /// ele anda pelas celulas do cabo — so celulas do Drive Shaft contam — ate a porta da outra ponta:
    ///   bearing (portas sem tipo) = acionado por aquele motor; Differential = continua por todas as outras portas dele;
    ///   Shaft Reverser = continua pela outra porta com o sentido invertido; qualquer outra coisa (cabo de plasma do jogo, gerador de
    ///   plasma...) = nao transmite (aviso no log uma vez).
    /// A forca do motor (x force_pool) e' dividida pelos bearings que ele alcanca.</summary>
    internal static partial class Juntas
    {
        public const string CABO = "SC_DriveShaft", DIFERENCIAL = "SC_Differential", REVERSOR = "SC_ShaftReverser", TRANSMISSOR = "SC_ShaftTransmitter";
        public const byte PortaEixo = 4, PortaSemTipo = 5;
        static TypeIndex tiCelula; static bool celulaOk, celulaFalhou; static ulong hCabo;
        /// <summary>0.17.0: rede = motores + bearings ligados pelos eixos (motores que chegam no mesmo bearing viram UMA rede e somam).</summary>
        internal class Rede
        {
            public List<Motor> motores = new List<Motor>(); public List<long> bearings = new List<long>();
            public Motor rep; public bool alimentado, conflito; public float forcaBearing, velMax; public string nome = ""; public string conflitoLog = "";
        }
        /// <summary>Um bearing acionado: de que motores (e com que sentido: -1 = passou por um Reverser) e o valor combinado neste frame.</summary>
        internal class Acionado { public List<Motor> ms = new List<Motor>(); public List<float> ss = new List<float>(); public string caminho; public Rede rede; public float valor; }
        static readonly Dictionary<long, Acionado> acionados = new Dictionary<long, Acionado>();
        static readonly List<Rede> redes = new List<Rede>();
        static readonly HashSet<string> avisosEixo = new HashSet<string>();
        static readonly Dictionary<string, int> redesLog = new Dictionary<string, int>();
        static float tMapaEixos = 9f;
        // quantas portas de ENTRADA cada bloco tem (as primeiras do prefab); o resto e' saida. Transmitter: a porta unica serve pros dois.
        const int EntradasDiferencial = 4, EntradasReversor = 1;

        static bool PrepararCelula()
        {
            if (celulaOk) return true; if (celulaFalhou) return false;
            try { tiCelula = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCableCell>()); hCabo = Ecs.HashPrefab(CABO); celulaOk = true; return true; }
            catch (Exception ex) { celulaFalhou = true; Registro.Erro("drive shaft: cable cell type: " + ex.Message); return false; }
        }

        static void AvisoEixo(string s) { if (avisosEixo.Add(s)) Registro.Aviso("drive shaft: " + s); }

        /// <summary>Nome do prefab do mod (sem " M") de um bloco, ou "" se for do jogo.</summary>
        static string NomeBloco(EntityManager em, Entity bloco)
        {
            try { ulong h = Ecs.Prefab(em, bloco); return porHash.TryGetValue(h, out var i) ? PREFABS[i].nome : ""; } catch (Exception) { return ""; }
        }
        static int IndicePorta(EntityManager em, Entity dono, Entity porta)
        {
            var ps = Ecs.Portas(em, dono); for (int i = 0; i < ps.Length; i++) if (Igual(ps[i], porta)) return i; return -1;
        }

        /// <summary>Do lado de fora de uma porta ate a porta da outra ponta de UM trecho (cabo ou encostada). soEixo = todas as celulas
        /// sao Drive Shaft; temCabo = passou por celula; tipoCabo = SpaceshipCableType da 1a celula.</summary>
        static unsafe Entity OutraPonta(EntityManager em, Entity porta, out bool soEixo, out bool temCabo, out byte tipoCabo)
        {
            soEixo = true; temCabo = false; tipoCabo = 255;
            if (!PrepararCelula() || !Ecs.PortaCruaPub(em, porta, out _, out _, out var fim, out var persist)) return Entity.Null;
            if (Ecs.Nulo(persist) || !Ecs.Vivo(em, persist)) return Entity.Null;
            if (Ecs.TemC(em, persist, Ecs.tiPort)) return persist;   // porta encostada direto na outra
            Entity prev = porta, cur = persist, ultimaPorta = Entity.Null;
            for (int i = 0; i < 4096; i++)
            {
                if (Ecs.Nulo(cur) || !Ecs.Vivo(em, cur)) break;
                if (Ecs.TemC(em, cur, Ecs.tiPort)) { ultimaPorta = cur; break; }
                if (!Ecs.TemC(em, cur, tiCelula)) break;
                temCabo = true;
                var c = (byte*)em.GetComponentDataRawRO(cur, tiCelula); if (c == null) break;
                if (tipoCabo == 255) tipoCabo = c[1];
                if (Ecs.Prefab(em, cur) != hCabo) soEixo = false;
                var fwd = *(Entity*)(c + 16); var bwd = *(Entity*)(c + 24);
                var prox = Igual(fwd, prev) ? bwd : fwd;
                if (Igual(prox, prev)) break;
                prev = cur; cur = prox;
            }
            if (!Ecs.Nulo(ultimaPorta)) return ultimaPorta;
            return (!Ecs.Nulo(fim) && !Igual(fim, porta)) ? fim : Entity.Null;
        }

        /// <summary>0.17.0: segue o eixo atravessando Frame With Ports (porta de uma face -> porta par na face oposta, igual os dados:
        /// Ecs.Fonte) ate a porta de um bloco que nao e' frame. plasma = algum trecho e' cabo de plasma (Drive Shaft ou do jogo);
        /// soEixo = nenhum trecho e' cabo do jogo; via = frames atravessados (pro log).</summary>
        static Entity Seguir(EntityManager em, Entity porta, out bool soEixo, out bool plasma, out bool temCabo, out string via)
        {
            soEixo = true; plasma = false; temCabo = false; via = "";
            var visto = new HashSet<long>(); Entity cur = porta;
            for (int hop = 0; hop < 24; hop++)
            {
                if (!visto.Add(Ecs.K(cur))) return Entity.Null;
                var fim = OutraPonta(em, cur, out bool se, out bool tc, out byte tp);
                if (tc) { temCabo = true; if (tp == 3) plasma = true; if (!se) soEixo = false; }
                if (Ecs.Nulo(fim)) return Entity.Null;
                var dono = Ecs.Pai(em, fim);
                if (Ecs.Nulo(dono) || !Ecs.TemC(em, dono, Ecs.tiFramePortas)) return fim;
                var ps = Ecs.Portas(em, dono); int i = Array.FindIndex(ps, x => Igual(x, fim));
                if (i < 0 || ps.Length < 2) return Entity.Null;
                cur = ps[(i + ps.Length / 2) % ps.Length];
                via += " -> frame " + dono.Index;
            }
            return Entity.Null;
        }

        /// <summary>A porta do bearing esta num eixo? (algum trecho de cabo de plasma, ou chega numa porta de eixo, atravessando frames)</summary>
        static bool PortaNoEixo(EntityManager em, Entity porta)
        {
            var outra = Seguir(em, porta, out _, out bool plasma, out _, out _);
            if (plasma) return true;
            if (Ecs.Nulo(outra)) return false;
            return Ecs.Porta(em, outra, out var t, out _, out _) && t == PortaEixo;
        }

        static readonly Dictionary<long, bool> noEixo = new Dictionary<long, bool>();
        static bool PortaNoEixoCache(EntityManager em, Entity p)
        {
            long k = Ecs.K(p); if (noEixo.TryGetValue(k, out var b)) return b;
            b = false; try { b = PortaNoEixo(em, p); } catch (Exception) { }
            noEixo[k] = b; return b;
        }

        // 0.16.0: Shaft Transmitter = copia do wireless transmitter do jogo (alavanca e numero do canal do jogo; canal em
        // SCTypeWirelessTransmitter +0x20, Ecs.Canal). Os de EIXO do mesmo canal se ligam entre si, em qualquer nave (igual os de dados);
        // separados dos de dados: aqui so entram os do mod, e a porta deles e' de plasma (nao troca dados com os do jogo).
        static ulong hTransm, hTransmM; static List<Entity> listaTransm;
        // 0.19.2: LEDs dos Shaft Transmitters — por canal: quantos transmitters e quantos recebem eixo de motor (emissores)
        sealed class LedT { public Entity led; public int escrito = -1; public float t = 9f; }
        static readonly Dictionary<long, LedT> ledsTransm = new Dictionary<long, LedT>();
        static readonly HashSet<long> emissoresEixo = new HashSet<long>();
        static void LedsTransmissores(EntityManager em)
        {
            var porCanal = new Dictionary<int, int>(); var emis = new Dictionary<int, int>();
            var lista = Transmissores(em);
            foreach (var t in lista)
            {
                if (!Ecs.Vivo(em, t)) { listaTransm = null; continue; }   // 0.19.3: lista refeita se um transmitter sumiu (previa da garagem)
                int c = Ecs.Canal(em, t); if (c < 0) continue;
                porCanal.TryGetValue(c, out int n); porCanal[c] = n + 1;
                if (emissoresEixo.Contains(Ecs.K(t))) { emis.TryGetValue(c, out int e2); emis[c] = e2 + 1; }
            }
            foreach (var t in lista)
            {
                if (!Ecs.Vivo(em, t)) continue;
                long k = Ecs.K(t);
                if (!ledsTransm.TryGetValue(k, out var l)) ledsTransm[k] = l = new LedT();
                l.t += UnityEngine.Time.deltaTime;
                if (!Ecs.Vivo(em, l.led) && l.t > 2f) { l.t = 0f; l.led = Led.Achar(em, t); l.escrito = -1; }
                if (Ecs.Nulo(l.led)) continue;
                int c = Ecs.Canal(em, t); porCanal.TryGetValue(c, out int n); emis.TryGetValue(c, out int ne);
                int estado = ne > 1 ? Led.Amarelo : (n > 1 && ne == 1) ? Led.Verde : Led.Vermelho;
                l.escrito = Led.Pintar(em, l.led, estado, l.escrito);
            }
        }
        static readonly Dictionary<long, int> canalLog = new Dictionary<long, int>();
        static List<Entity> Transmissores(EntityManager em)
        {
            if (listaTransm != null) return listaTransm;
            listaTransm = new List<Entity>();
            if (hTransm == 0) { hTransm = Ecs.HashPrefab(TRANSMISSOR); hTransmM = Ecs.HashPrefab(TRANSMISSOR + " M"); }
            foreach (var e in Ecs.Transmitters(em)) { ulong h = Ecs.Prefab(em, e); if (h == hTransm || h == hTransmM) listaTransm.Add(e); }
            return listaTransm;
        }

        /// <summary>Uma vez a cada 0,25 s: de cada motor, quais bearings ele alcanca (e em que sentido); depois junta em redes os
        /// motores que chegam nos mesmos bearings.</summary>
        static void MapearEixos(EntityManager em, float dt)
        {
            tMapaEixos += dt; if (tMapaEixos < 0.25f) { AtualizarRedes(); try { LedsTransmissores(em); } catch (Exception) { } return; }
            tMapaEixos = 0f;
            acionados.Clear(); redes.Clear(); noEixo.Clear(); listaTransm = null; emissoresEixo.Clear();
            if (!PrepararCelula()) return;
            var alcancados = new Dictionary<Motor, List<long>>();
            foreach (var m in motores.Values)
            {
                if (!m.portasOk || Ecs.Nulo(m.saida)) continue;
                var lista = new List<long>(); alcancados[m] = lista;
                var vistos = new HashSet<long> { Ecs.K(m.bloco) };
                try { Andar(em, m, m.saida, 1f, vistos, 0, m.nome + " " + m.bloco.Index, lista); } catch (Exception ex) { AvisoEixo(m.nome + ": " + ex.Message); }
            }
            // redes: motores que dividem um bearing ficam juntos (uniao simples)
            var redeDe = new Dictionary<Motor, Rede>();
            foreach (var kv in alcancados)
            {
                var r = new Rede(); r.motores.Add(kv.Key); redeDe[kv.Key] = r; redes.Add(r);
            }
            foreach (var ac in acionados.Values)
            {
                Rede alvo = null;
                foreach (var m in ac.ms)
                {
                    var r = redeDe[m]; if (alvo == null) { alvo = r; continue; }
                    if (r == alvo) continue;
                    foreach (var m2 in r.motores) { alvo.motores.Add(m2); redeDe[m2] = alvo; }
                    r.motores.Clear(); redes.Remove(r);
                }
            }
            foreach (var kv in acionados) { var r = redeDe[kv.Value.ms[0]]; kv.Value.rede = r; r.bearings.Add(kv.Key); }
            foreach (var r in redes)
            {
                r.motores.Sort((a, b) => a.bloco.Index.CompareTo(b.bloco.Index));
                var sb = new System.Text.StringBuilder();
                foreach (var m in r.motores) sb.Append(sb.Length == 0 ? "" : " + ").Append(m.nome).Append(' ').Append(m.bloco.Index);
                r.nome = sb.ToString();
                string chave = r.nome;
                if (!redesLog.TryGetValue(chave, out int nlog) || nlog != r.bearings.Count)
                {
                    redesLog[chave] = r.bearings.Count;
                    Registro.Info(r.nome + ": drives " + r.bearings.Count + " bearing(s) through Drive Shafts" + (r.motores.Count > 1 ? " (" + r.motores.Count + " motors on the same shaft: their force adds up)" : "") + (r.bearings.Count > 1 ? ", force split " + r.bearings.Count + " ways" : ""));
                }
            }
            AtualizarRedes();
        }

        /// <summary>Anda pelo eixo saindo de uma porta de SAIDA. So entra em bloco pela porta de ENTRADA (laranja) e sai pelas de saida
        /// (roxo); bearing aceita qualquer porta. Motor chegando no mesmo bearing de novo: guarda o sentido (conflito se oposto).</summary>
        static void Andar(EntityManager em, Motor m, Entity porta, float sentido, HashSet<long> vistos, int prof, string caminho, List<long> achados)
        {
            if (prof > 24) return;
            var fim = Seguir(em, porta, out bool soEixo, out _, out bool temCabo, out string via);
            if (Ecs.Nulo(fim)) return;
            var dono = Ecs.Pai(em, fim); if (Ecs.Nulo(dono)) return;
            string nome = NomeBloco(em, dono);
            caminho += via;
            if (temCabo && !soEixo) { AvisoEixo(m.nome + " " + m.bloco.Index + ": a game cable (not a Drive Shaft) is in the way to " + (nome.Length > 0 ? nome : "block " + dono.Index) + " — it does not transmit"); return; }
            if (nome == "SC_Bearing" || nome == "SC_GyroBearing" || nome == "SC_MiniBearing" || nome == "SC_MiniGyroBearing")   // 0.20.4 (Andre: "o mini bearing nao ta aceitando shaft"): so o SC_Bearing era reconhecido
            {
                if (Anel.EhLateral(em, dono, fim, nome)) { AvisoEixo(m.nome + " " + m.bloco.Index + ": its shaft goes into a PASS-THROUGH port (slip ring) of the " + nome + " " + dono.Index + " — connect it to one of the ports underneath"); return; }   // 0.22.0
                long kb = Ecs.K(dono);
                if (!acionados.TryGetValue(kb, out var ac)) { ac = new Acionado { caminho = caminho + " -> bearing" + (sentido < 0 ? " (reversed)" : "") }; acionados[kb] = ac; }
                for (int i = 0; i < ac.ms.Count; i++) if (ac.ms[i] == m && ac.ss[i] == sentido) return;   // mesmo caminho de novo
                ac.ms.Add(m); ac.ss.Add(sentido); if (!achados.Contains(kb)) achados.Add(kb);
                return;
            }
            if (!vistos.Add(Ecs.K(dono))) return;
            if (nome == DIFERENCIAL || nome == REVERSOR)
            {
                int nEntra = nome == REVERSOR ? EntradasReversor : EntradasDiferencial;
                int idx = IndicePorta(em, dono, fim);
                string curto = nome == REVERSOR ? "Shaft Reverser" : "Differential";
                if (idx < 0 || idx >= nEntra) { AvisoEixo(m.nome + " " + m.bloco.Index + ": its shaft goes into an OUTPUT (purple) of the " + curto + " " + dono.Index + " — connect it to an input (orange)"); return; }
                float s = nome == REVERSOR ? -sentido : sentido;
                var ps = Ecs.Portas(em, dono);
                for (int i = nEntra; i < ps.Length; i++) Andar(em, m, ps[i], s, vistos, prof + 1, caminho + " -> " + (nome == REVERSOR ? "reverser" : "differential"), achados);
                return;
            }
            if (nome == TRANSMISSOR)
            {
                int canal = Ecs.Canal(em, dono);
                if (canal < 0) { AvisoEixo("shaft transmitter " + dono.Index + ": channel not readable"); return; }
                long kd = Ecs.K(dono); emissoresEixo.Add(kd);
                if (!canalLog.TryGetValue(kd, out int cl) || cl != canal) { canalLog[kd] = canal; Registro.Info(m.nome + " " + m.bloco.Index + ": shaft goes into Shaft Transmitter " + dono.Index + " on channel " + canal); }
                int outros = 0;
                foreach (var t in Transmissores(em))
                {
                    if (Igual(t, dono)) continue;
                    if (Ecs.Canal(em, t) != canal || !vistos.Add(Ecs.K(t))) continue;
                    outros++;
                    foreach (var p in Ecs.Portas(em, t)) Andar(em, m, p, sentido, vistos, prof + 1, caminho + " -> transmitter ch " + canal, achados);
                }
                if (outros == 0) AvisoEixo(m.nome + " " + m.bloco.Index + ": no other Shaft Transmitter on channel " + canal + " (set the same channel on the receiving one)");
                return;
            }
            if (PREFABS_MOTOR(nome)) { AvisoEixo(m.nome + " " + m.bloco.Index + ": its shaft goes into the OUTPUT of another motor (" + nome + " " + dono.Index + ") — to join motors, connect each one to an input of a Differential"); return; }
            AvisoEixo(m.nome + " " + m.bloco.Index + ": its shaft ends at " + (nome.Length > 0 ? nome : "a game part (block " + dono.Index + ")") + " — Drive Shafts only turn bearings, Differentials, Shaft Reversers and Shaft Transmitters (Frames With Ports pass it through)");
        }
        static bool PREFABS_MOTOR(string nome) => nome != null && nome.StartsWith("SC_Motor");

        /// <summary>Todo frame (os sinais mudam): forca, velocidade e sinal de cada rede. Motor sem energia/combustivel nao soma.
        /// Forca = soma (x force_pool) / bearings; velocidade maxima e sinal = media pesada pela forca de cada motor.
        /// Sinais opostos chegando no mesmo bearing (motores ao contrario, ou o mesmo motor com e sem Reverser) = rede TRAVADA.</summary>
        static void AtualizarRedes()
        {
            foreach (var r in redes)
            {
                float ftot = 0f, vel = 0f; Motor rep = null; float frep = -1f;
                foreach (var m in r.motores)
                {
                    float f = m.alimentado ? m.impulsoEfetivo : 0f;
                    ftot += f; vel += f * m.velMax;
                    if (f > frep) { frep = f; rep = m; }
                }
                r.rep = rep; r.alimentado = ftot > 0f;
                r.velMax = ftot > 0f ? vel / ftot : (rep != null ? rep.velMax : 0f);
                r.forcaBearing = r.bearings.Count > 0 ? ftot * ForcaReserva / r.bearings.Count : 0f;
                bool conflito = false; string porque = "";
                foreach (var kb in r.bearings)
                {
                    var ac = acionados[kb]; float num = 0f; bool pos = false, neg = false;
                    for (int i = 0; i < ac.ms.Count; i++)
                    {
                        var m = ac.ms[i]; float f = m.alimentado ? m.impulsoEfetivo : 0f; if (f <= 0f) continue;
                        float v = m.sinal * ac.ss[i]; num += f * v;
                        if (v > 0.001f) pos = true; else if (v < -0.001f) neg = true;
                    }
                    ac.valor = ftot > 0f ? num / ftot : 0f;
                    if (pos && neg && !conflito) { conflito = true; porque = "opposite shaft signals reach bearing " + (kb >> 32); }
                }
                if (conflito != r.conflito) Registro.Info(r.nome + ": " + (conflito ? "LOCKED — " + porque + " (motors turning against each other, or a Shaft Reverser on one path only)" : "unlocked"));
                r.conflito = conflito; r.conflitoLog = porque;
            }
        }

        /// <summary>Configure do Components Framework: o Drive Shaft copia as malhas (e colisores) do cabo de plasma do jogo.</summary>
        public static void PrepararCabo(EPC_SpaceshipComponent epc, Core core)
        {
            if (epc == null || epc.name != CABO) return;
            try
            {
                var meu = epc.TryCast<EPC_SCCable>(); var jogo = core != null ? core._prefabCablePlasma : null;
                if (meu == null || jogo == null) { Registro.Aviso("drive shaft: " + (meu == null ? "the prefab is not a cable" : "the game's plasma cable was not found") + " — it will be invisible"); return; }
                int n = 0;
                foreach (var pi in typeof(EPC_SCCable).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (pi.PropertyType != typeof(Mesh) || !pi.CanWrite || !pi.CanRead) continue;
                    try { pi.SetValue(meu, pi.GetValue(jogo)); n++; } catch (Exception) { }
                }
                if (meu._renderer != null && jogo._renderer != null) meu._renderer._mesh = jogo._renderer._mesh;
                if (meu._cableHolderRenderer != null && jogo._cableHolderRenderer != null) meu._cableHolderRenderer._mesh = jogo._cableHolderRenderer._mesh;
                CopiarColisor(meu._cableColliderStraight, jogo._cableColliderStraight); CopiarColisor(meu._cableColliderCurved, jogo._cableColliderCurved);
                Registro.Info("drive shaft: " + n + " cable meshes copied from the game's plasma cable (type " + meu._type + ", color " + meu._defaultPaintableColor + ")");
            }
            catch (Exception ex) { Registro.Aviso("drive shaft: could not copy the plasma cable meshes: " + ex.Message); }
        }
        static void CopiarColisor(DOTSCollider meu, DOTSCollider jogo)
        {
            try
            {
                var a = meu != null ? meu.TryCast<DOTSColliderConvex>() : null; var b = jogo != null ? jogo.TryCast<DOTSColliderConvex>() : null;
                if (a != null && b != null) a._mesh = b._mesh;
            }
            catch (Exception) { }
        }
    }
}
