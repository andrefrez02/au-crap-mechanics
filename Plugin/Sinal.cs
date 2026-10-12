using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>0.24.0 (Andre: "o wireless transmitter nem sempre funciona num joint separado... fica meio que sem sinal (led amarelo)").
    ///
    /// O LED ambar NAO vem do pareamento sem fio: o SCTick_WirelessTransmitter refaz o pareamento do ZERO todo tick (varredura por
    /// canal, alcance de 2000 unidades, sem cache, sem filtro por nave ou por part), entao dois transmitters em parts diferentes se
    /// enxergam normalmente e nao existe "inicializacao" que possa perder a corrida.
    ///
    /// O que trava e' a DIRECAO da porta. SpaceshipElectricPort tem _setupType (+20) e _runtimeType (+21), e no EPC_SpaceshipComponent
    /// os dois nascem iguais — no transmitter, DataInOut (3). Quem resolve se a porta e' entrada ou saida e'
    /// SpaceshipPostUpdatePortsRuntimeEndEntities, cujos dois jobs so pegam portas marcadas com a tag
    /// SpaceshipElectricPortUpdateRuntimeEndEntity — e que REMOVEM a tag ao terminar. E' one-shot: se falhar, nada reagenda.
    /// E o UpdateRuntimeEndsJob anda pelo cabo so DENTRO da mesma part (compara o avo de cada celula com a raiz da part e aborta se
    /// mudar); falhando, _runtimeEndEntity fica apontando pra uma celula de cabo, o UpdatePortsDataInOutJob nao acha a porta da outra
    /// ponta e _runtimeType nunca sai de DataInOut. O job do transmitter, ao ver DataInOut, pinta o LED de ambar e retorna. Pra sempre.
    ///
    /// A corrida: joint quebra -> a part e' marcada -> as portas sao marcadas -> o sistema resolve. Se isso rodar enquanto a hierarquia
    /// de parent ainda esta em transicao logo depois do split, o teste do avo falha e a unica chance ja foi gasta.
    ///
    /// Conserto: quando um transmitter aparece com a porta presa em DataInOut, re-marcar a porta com a tag — o caminho oficial do jogo,
    /// o mesmo que o SpaceshipPostCheckConsistency_1 usa. O jogo recalcula no proximo update e tira a tag sozinho. So age no que esta
    /// quebrado, e desiste depois de algumas tentativas (se insistir e' porque o cabo atravessa o joint de verdade — ai e' estrutural
    /// do jogo, nao corrida, e o log diz isso).</summary>
    internal static class Sinal
    {
        public static bool Religar = true;        // [Signal] fix_stuck_ports
        public static int MaxTentativas = 3;
        /// <summary>UIManager.OnSpaceshipLaunch (RadarIcone.AoLancar). So mexe com a simulacao rodando: NA GARAGEM as portas estao
        /// desresolvidas por natureza (o bloco nem esta numa part), entao o mod ia re-marcar tudo e fazer mudanca estrutural no meio
        /// do frame — foi o que travou o jogo na 0.24.0 do dia 10/10.</summary>
        public static bool Lancado;

        const byte DataInOut = 3;
        static TypeIndex tiTag, tiPrefabTag; static bool ok, falhou; static float t = 9f;
        static readonly Dictionary<long, int> presas = new Dictionary<long, int>();
        static readonly Dictionary<long, int> tentativas = new Dictionary<long, int>();
        static readonly HashSet<long> desistiu = new HashSet<long>();

        static bool Preparar()
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiTag = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPortUpdateRuntimeEndEntity>());
                try { tiPrefabTag = TypeManager.GetTypeIndex(Il2CppType.Of<Unity.Entities.Prefab>()); } catch (Exception) { }
                ok = tiTag.Value != 0; falhou = !ok;
                if (falhou) Registro.Aviso("signal: SpaceshipElectricPortUpdateRuntimeEndEntity not available — a transmitter stuck with no signal will not be repaired");
                return ok;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("signal: port tag not available (" + ex.Message + ")"); return false; }
        }

        public static unsafe void Passo(EntityManager em, float dt)
        {
            if (!Religar || !Lancado) { if (!Lancado && (tentativas.Count > 0 || presas.Count > 0)) Esquecer(); return; }
            t += dt; if (t < 1f) return; t = 0f;
            if (!Preparar()) return;
            List<Entity> arrumar = null;
            foreach (var tr in Ecs.Transmitters(em))
            {
                try
                {
                    if (!Ecs.Vivo(em, tr)) continue;
                    if (tiPrefabTag.Value != 0 && em.HasComponent(tr, ComponentType.ReadOnly(tiPrefabTag))) continue;   // entity de prefab, nao do mundo
                    var portas = Ecs.Portas(em, tr); if (portas.Length == 0) continue;
                    var p = portas[0];   // o job do jogo usa so a porta [0]
                    if (!Ecs.Vivo(em, p) || !Ecs.TemC(em, p, Ecs.tiPort)) continue;
                    var b = (byte*)em.GetComponentDataRawRO(p, Ecs.tiPort); if (b == null) continue;
                    long k = Ecs.K(p);
                    if (b[21] != DataInOut) { presas.Remove(k); if (tentativas.Remove(k)) desistiu.Remove(k); continue; }   // resolvida: esquece
                    if (desistiu.Contains(k)) continue;
                    // so age depois de DUAS passadas seguidas presa (1 s): deixa a resolucao normal do jogo acontecer sozinha
                    presas.TryGetValue(k, out int seguidas); seguidas++; presas[k] = seguidas;
                    if (seguidas < 2) continue;
                    tentativas.TryGetValue(k, out int n); n++; tentativas[k] = n;
                    if (n > MaxTentativas)
                    {
                        desistiu.Add(k);
                        if (Direcionar(em, tr, p) || PeloCanal(em, tr, p)) { tCenso = 3f; continue; }   // 0.24.0: pela topologia, ou pelo canal
                        Registro.Aviso("Wireless Transmitter " + tr.Index + ": its port is still undirected after " + MaxTentativas
                            + " retries — the game cannot resolve it on its own." + Laudo(em, tr, p, b));
                        tCenso = 3f;
                        continue;
                    }
                    if (em.HasComponent(p, ComponentType.ReadOnly(tiTag))) continue;   // ja marcada, esperando o jogo
                    (arrumar ?? (arrumar = new List<Entity>())).Add(p);
                    Registro.Info("Wireless Transmitter " + tr.Index + ": port " + p.Index + " stuck with no direction (amber LED) — asking the game to resolve it again (try " + n + "/" + MaxTentativas + ")");
                }
                catch (Exception) { }
            }
            if (tCenso > 0f) { tCenso -= 1f; if (tCenso <= 0f && !censoFeito) { censoFeito = true; Censo(em); } }
            // a mudanca estrutural (AddComponent) so DEPOIS do laco: mexer nos chunks no meio da varredura invalida a iteracao
            if (arrumar == null) return;
            for (int i = 0; i < arrumar.Count; i++)
            {
                try { var p = arrumar[i]; if (Ecs.Vivo(em, p) && !em.HasComponent(p, ComponentType.ReadOnly(tiTag))) em.AddComponent(p, ComponentType.ReadWrite(tiTag)); }
                catch (Exception ex) { Registro.Aviso("signal: could not re-tag the port: " + ex.Message); }
            }
        }


        /// <summary>0.24.0 (laudo do dia 10/10): a porta do transmitter ficava sem direcao porque o cabo dela termina numa porta de um
        /// bloco NOSSO, e as nossas portas sao de proposito SEM TIPO (Unset) — "shaft = motor, dados = servo", 0.15.0. O jogo resolve
        /// um DataInOut andando pelo cabo ate uma porta com tipo definido, e sabe atravessar Frame With Ports; numa porta Unset de um
        /// bloco que nao e' Frame With Ports ele para, e o transmitter fica ambar pra sempre — na mesma part, sem joint nenhum no meio.
        ///
        /// Mas o MOD sabe a direcao: as portas antes da faixa de passagem sao entradas do bloco e as depois dela sao saidas. Se o cabo
        /// chega numa ENTRADA nossa, quem alimenta e' o transmitter => ele e' RECEPTOR (porta DataOutput). Se chega numa SAIDA nossa,
        /// ele e' EMISSOR (DataInput). So isso e' escrito, e so na porta do proprio transmitter: o _runtimeEndEntity o jogo ja resolveu
        /// certo (o laudo mostra a porta certa, na mesma part), entao falta mesmo so esse byte.</summary>
        static unsafe bool Direcionar(EntityManager em, Entity tr, Entity porta)
        {
            try
            {
                var f = Ecs.Fonte(em, porta, out _);
                if (Ecs.Nulo(f) || !Ecs.Vivo(em, f) || !Ecs.TemC(em, f, Ecs.tiPort)) return false;
                var dono = Ecs.Pai(em, f); if (Ecs.Nulo(dono) || !Ecs.Vivo(em, dono)) return false;
                ulong h; try { h = Ecs.Prefab(em, dono); } catch (Exception) { return false; }
                if (!Juntas.TipoDoHash(h, out _, out _, out var nome) || string.IsNullOrEmpty(nome)) return false;   // nao e' bloco nosso
                var ps = Ecs.Portas(em, dono); int i = Ecs.PortaIndice(em, f);
                if (ps == null || ps.Length == 0 || i < 0) return false;
                if (!Anel.Faixa(em, ps, nome, out int ini, out int nl)) return false;
                byte novo; string papel;
                if (i < ini) { novo = 1; papel = "receiver (its port feeds the " + nome + "'s input)"; }
                else if (i >= ini + nl) { novo = 0; papel = "sender (the " + nome + "'s output feeds its port)"; }
                else return false;   // porta de passagem: a direcao depende do outro lado, nao da pra afirmar
                var w = (byte*)em.GetComponentDataRawRW(porta, Ecs.tiPort); if (w == null) return false;
                w[21] = novo;
                Registro.Info("Wireless Transmitter " + tr.Index + ": port " + porta.Index + " had no direction because the cable ends on port " + f.Index
                    + " of " + nome + " " + dono.Index + ", and this mod's ports are deliberately typeless — the game stops there. Setting it to "
                    + Nome(novo) + ": this transmitter is the " + papel);
                return true;
            }
            catch (Exception ex) { Registro.Aviso("signal: could not set the direction: " + ex.Message); return false; }
        }

        /// <summary>0.24.0, 2a via: quando o cabo termina numa porta de PASSAGEM nossa (Slip Ring, por exemplo), a topologia nao diz a
        /// direcao — ela depende do outro lado do anel. Mas o CANAL diz: um canal tem um emissor so. Se ja existe outro transmitter
        /// no mesmo canal resolvido como emissor, este aqui so pode ser receptor. Nao depende de cabo, de anel nem de part.
        /// (So nessa direcao: "nao ha emissor no canal" NAO prova que este e' o emissor — pode ser que o emissor tambem esteja preso.)</summary>
        static unsafe bool PeloCanal(EntityManager em, Entity tr, Entity porta)
        {
            try
            {
                int canal = Ecs.Canal(em, tr); if (canal < 0) return false;
                Entity emissor = Entity.Null; int noCanal = 0;
                foreach (var o in Ecs.Transmitters(em))
                {
                    if (!Ecs.Vivo(em, o) || (o.Index == tr.Index && o.Version == tr.Version)) continue;
                    if (tiPrefabTag.Value != 0 && em.HasComponent(o, ComponentType.ReadOnly(tiPrefabTag))) continue;
                    if (Ecs.Canal(em, o) != canal) continue;
                    noCanal++;
                    var ps = Ecs.Portas(em, o); if (ps.Length == 0 || !Ecs.TemC(em, ps[0], Ecs.tiPort)) continue;
                    var b = (byte*)em.GetComponentDataRawRO(ps[0], Ecs.tiPort); if (b == null) continue;
                    if (b[21] == 0) emissor = o;   // DataInput = emissor
                }
                if (Ecs.Nulo(emissor)) return false;
                var w = (byte*)em.GetComponentDataRawRW(porta, Ecs.tiPort); if (w == null) return false;
                w[21] = 1;   // DataOutput = receptor
                Registro.Info("Wireless Transmitter " + tr.Index + ": port " + porta.Index + " ends on a pass-through port of this mod (the direction depends on the other side of the ring, so the topology cannot say it)"
                    + " — but channel " + canal + " already has a sender (transmitter " + emissor.Index + ") among its " + (noCanal + 1) + " transmitters, so this one can only be the receiver. Setting it to DataOutput");
                return true;
            }
            catch (Exception ex) { Registro.Aviso("signal: could not set the direction by channel: " + ex.Message); return false; }
        }

        /// <summary>" , owned by <bloco> (<prefab do mod, ou 'a game block'>)" — sem isso o laudo nao diz em que peca o cabo termina.</summary>
        static string Dono(EntityManager em, Entity porta)
        {
            try
            {
                if (Ecs.Nulo(porta) || !Ecs.Vivo(em, porta)) return "";
                var d = Ecs.Pai(em, porta); if (Ecs.Nulo(d) || !Ecs.Vivo(em, d)) return " (no owner block)";
                string nome = null; try { if (Juntas.TipoDoHash(Ecs.Prefab(em, d), out _, out _, out var nm)) nome = nm; } catch (Exception) { }
                int i = Ecs.PortaIndice(em, porta); var ps = Ecs.Portas(em, d);
                var sb = new System.Text.StringBuilder(" of block ").Append(d.Index).Append(' ').Append(nome ?? "(a game block)");
                sb.Append(" [port ").Append(i).Append(" of ").Append(ps == null ? 0 : ps.Length).Append(']');
                if (nome != null && ps != null && Anel.Faixa(em, ps, nome, out int ini, out int nl))
                    sb.Append(i < ini ? " = an input of ours" : i >= ini + nl ? " = an output of ours" : " = a pass-through of ours");
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        /// <summary>0.24.0: censo dos transmitters por canal, uma vez alguns segundos depois de mexermos em alguma porta. Mostra, pra
        /// cada canal, quem o jogo considera emissor (DataInput), receptor (DataOutput) e quem continua sem direcao (DataInOut) — e' o
        /// unico jeito de saber se o par pode se achar.</summary>
        static float tCenso = -1f; static bool censoFeito;
        static unsafe void Censo(EntityManager em)
        {
            try
            {
                var porCanal = new Dictionary<int, System.Text.StringBuilder>();
                foreach (var tr in Ecs.Transmitters(em))
                {
                    if (!Ecs.Vivo(em, tr)) continue;
                    if (tiPrefabTag.Value != 0 && em.HasComponent(tr, ComponentType.ReadOnly(tiPrefabTag))) continue;
                    int c = Ecs.Canal(em, tr); if (c < 0) continue;
                    var ps = Ecs.Portas(em, tr); byte rt = 255; float v = 0f;
                    if (ps.Length > 0 && Ecs.TemC(em, ps[0], Ecs.tiPort))
                    {
                        var b = (byte*)em.GetComponentDataRawRO(ps[0], Ecs.tiPort);
                        if (b != null) { rt = b[21]; v = *(float*)(b + 8); }
                    }
                    if (!porCanal.TryGetValue(c, out var sb)) { sb = new System.Text.StringBuilder(); porCanal[c] = sb; }
                    sb.Append(sb.Length > 0 ? ", " : "").Append(tr.Index).Append('=').Append(Nome(rt))
                      .Append(rt == 0 ? "(sender)" : rt == 1 ? "(receiver)" : "(UNRESOLVED)").Append(" value=").Append(v.ToString("F3"));
                }
                if (porCanal.Count == 0) return;
                var o = new System.Text.StringBuilder("signal: wireless census —");
                foreach (var kv in porCanal) o.Append(" channel ").Append(kv.Key).Append(": ").Append(kv.Value).Append(';');
                Registro.Info(o.ToString());
            }
            catch (Exception ex) { Registro.Aviso("signal: census: " + ex.Message); }
        }

        /// <summary>0.24.0: o que exatamente esta impedindo a porta de resolver. Sai uma vez por porta, quando o mod desiste.
        /// (A resolucao do jogo so anda pelo cabo DENTRO da mesma part; o mod sabe andar pelo cabo por conta propria — Ecs.Fonte —
        /// entao comparar os dois diz na hora se a ponta caiu em outra part, se nao ha cabo, ou se e' outra coisa.)</summary>
        static unsafe string Laudo(EntityManager em, Entity tr, Entity porta, byte* b)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                Ecs.PortaCruaPub(em, porta, out var setup, out var valor, out var fim, out var persist);
                sb.Append(" Port ").Append(porta.Index).Append(" of block ").Append(Onde(em, tr)).Append(':');
                sb.Append(" setup=").Append(Nome(setup)).Append(" runtime=").Append(Nome(b[21])).Append(" value=").Append(valor.ToString("F3"));
                sb.Append("; connected to ").Append(Onde(em, persist));
                sb.Append("; the game's end is ").Append(Onde(em, fim));
                var f = Ecs.Fonte(em, porta, out var trilha);
                sb.Append("; walking the cable myself:").Append(trilha.Length > 0 ? trilha : " (nothing)");
                if (!Ecs.Nulo(f))
                {
                    Ecs.PortaCruaPub(em, f, out var setupF, out _, out _, out _);
                    sb.Append(" => ").Append(Onde(em, f)).Append(Dono(em, f)).Append(" setup=").Append(Nome(setupF));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return " (could not inspect the port: " + ex.Message + ")"; }
        }
        static string Nome(byte t) => t == 0 ? "DataInput" : t == 1 ? "DataOutput" : t == 2 ? "Power" : t == 3 ? "DataInOut" : t == 4 ? "Plasma" : t == 5 ? "Unset" : t.ToString();
        /// <summary>"N port on part P" — o que a entity e' e em que part ela esta (a part e' o que a resolucao do jogo compara).</summary>
        static string Onde(EntityManager em, Entity e)
        {
            if (Ecs.Nulo(e)) return "nothing";
            var sb = new System.Text.StringBuilder().Append(e.Index);
            if (!Ecs.Vivo(em, e)) return sb.Append(" (gone)").ToString();
            if (Ecs.EhCabo(em, e)) sb.Append(" (a cable cell)");
            else if (Ecs.TemC(em, e, Ecs.tiPort)) sb.Append(" (a port)");
            var cur = e;
            for (int i = 0; i < 8 && !Ecs.Nulo(cur); i++) { if (Ecs.EhPart(em, cur)) return sb.Append(" on part ").Append(cur.Index).ToString(); cur = Ecs.Pai(em, cur); }
            return sb.Append(" on no part").ToString();
        }

        public static void Esquecer() { tentativas.Clear(); desistiu.Clear(); presas.Clear(); t = 9f; tCenso = -1f; censoFeito = false; }
    }
}
