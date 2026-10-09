using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace Mecanica
{
    /// <summary>0.22.0 — anel coletor (Andre: "eu nao gostei da forma do cabo, dele ficando solto, nunca vai fazer sentido em um objeto
    /// que ta girando 360 o tempo todo ... deixa passar so pelo anel"). A 0.21.x emendava o cabo que CRUZAVA a junta (seguindo as celulas
    /// ate a porta da outra part); saiu. Agora nenhum cabo atravessa: cada junta do mod tem portas de PASSAGEM sem tipo na base (as
    /// ultimas da lista do prefab: 4 no bearing / pistao / recoupler, 1 nos minis, 4 x 4 no turret seat — um conjunto por placa) e a
    /// peca que se move leva um Slip Ring (SC_SlipRing, cubo 0,25 com 4 portas; 0.22.1: o Mini Slip Ring saiu). O plugin liga porta
    /// lateral i da junta <-> porta i do anel:
    ///  - DADOS: do lado onde o cabo vem de uma SAIDA, o valor vai pra entrada do outro lado. Como na 0.21.x: a entrada (ou a porta sem
    ///    tipo de um bloco do mod) ganha _runtimeEndEntity (+12) apontando pra saida de la — o SpaceshipElectricGetInputs do jogo copia
    ///    o _portValue dessa saida. Reescrito todo frame (o job do jogo so refaz quando a nave muda). Saida nos dois lados = conflito (aviso).
    ///  - ENERGIA: cabo de energia nos dois lados = ponte por fantasmas (0.21.4): uma entity so com SCElectricPower em cada circuito;
    ///    o fantasma do lado que recebe "gera" o que aquele lado consome, o do lado que da "consome" isso. Sem cap: acima do _maxPower
    ///    o derretimento do jogo queima o cabo (Andre: "deixa queimar como o jogo faz").
    /// Onde o anel tem que estar: bearing / pistao = no eixo do bloco, na part de cima (no disco; na cabeca do pistao ou logo depois do
    /// Piston End); recoupler = colado no decoupler onde ele engatou; turret = na peca pendurada na placa daquele conjunto.</summary>
    internal static class Anel
    {
        public static bool Ligado = true, LogDetalhe = true;
        /// <summary>Quantas portas de passagem o bloco tem (as ULTIMAS da lista do prefab).</summary>
        public static int NLaterais(string nome) => nome == null ? 0 : nome.StartsWith("SC_TurretSeat") ? 16 : nome.EndsWith("SlipRing") ? 4 : nome.StartsWith("SC_Mini") ? 1 : 4;
        /// <summary>0.23.0: portas DEPOIS das de passagem na lista do prefab (a saida de angulo dos bearings; ficou por ultimo pra nao
        /// mexer no indice das de passagem dos blueprints da 0.22.x).</summary>
        public static int NSaidas(string nome) => nome != null && (nome.StartsWith("SC_Bearing") || nome.StartsWith("SC_GyroBearing") || nome.StartsWith("SC_MiniBearing") || nome.StartsWith("SC_MiniGyroBearing")) ? 1 : 0;
        /// <summary>0.23.1: faixa [ini, ini+n) das portas de passagem na lista do prefab. A saida de angulo (0.23.0) e' a ultima da
        /// lista, entao a faixa nao e' mais "as n ultimas"; o prefab velho (0.22.x, sem a saida) e' reconhecido pelo tipo da ultima porta.</summary>
        public static bool Faixa(EntityManager em, Entity[] ps, string nome, out int ini, out int n)
        {
            ini = 0; n = NLaterais(nome);
            if (n == 0 || ps == null || ps.Length < n) return false;
            int ns = 0;
            if (NSaidas(nome) > 0 && ps.Length > n && Ecs.Porta(em, ps[ps.Length - 1], out var tf, out _, out _) && tf == Ecs.PortaSaida) ns = 1;
            ini = ps.Length - n - ns;
            return ini >= 0;
        }
        /// <summary>A porta e' uma das de passagem do bloco (pelo indice no prefab)? Juntas (servo) e Eixos (shaft) ignoram essas.</summary>
        public static bool EhLateral(EntityManager em, Entity dono, Entity porta, string nome)
        {
            var ps = Ecs.Portas(em, dono);
            if (!Faixa(em, ps, nome, out int ini, out int n) || ps.Length <= n) return false;   // bloco sem portas de passagem (pistoes 2/4/8 antigos)
            int i = Ecs.PortaIndice(em, porta);
            return i >= ini && i < ini + n;
        }

        class Canal
        {
            public Entity pj, pa; public int i;          // porta da junta, porta do anel
            public int sentido;                          // 0 nada, 1 junta -> anel, -1 anel -> junta, 2 conflito (saida dos dois lados), 3 energia
            public Entity fonte, alvo; public int escritas; public float tLog = 99f; public int avisos;
            public Ponte ponte; public string ultimo = "";
        }
        class Liga { public string chave, nome; public Entity junta, anel; public List<Canal> canais = new List<Canal>(); public bool viva; public int conjunto; }
        static readonly Dictionary<string, Liga> ligas = new Dictionary<string, Liga>();
        static float tVarre = 9f; static int avisos; static ulong hAnel, hMini; static bool hashes;
        static readonly string[] PLACAS = { "left", "right", "back", "top" };   // ordem dos conjuntos de portas da base do turret (modelos_v2 TURRET_PORTAS)

        public static void Passo(EntityManager em, float dt)
        {
            if (!Ligado) return;
            tVarre += dt;
            if (tVarre >= 0.5f) { tVarre = 0f; try { Varrer(em); } catch (Exception ex) { if (avisos++ < 3) Registro.Aviso("slip ring: " + ex.Message); } try { TingirTodas(em); } catch (Exception ex) { if (avisosCor++ < 3) Registro.Aviso("pass-through plugs: " + ex.Message); } }
            foreach (var l in ligas.Values) foreach (var c in l.canais) { try { Aplicar(em, l, c, dt); } catch (Exception ex) { if (c.avisos++ < 3) Registro.Aviso("slip ring (" + l.nome + " #" + c.i + "): " + ex.Message); } }
        }

        // ================================================================ procura: junta ativa -> anel na peca que se move
        struct Candidato { public Entity e, part; public float3 pos; }
        static List<Candidato> Aneis(EntityManager em)
        {
            var r = new List<Candidato>();
            if (!hashes) { hAnel = Ecs.HashPrefab("SC_SlipRing"); hMini = Ecs.HashPrefab("SC_MiniSlipRing"); hashes = hAnel != 0; if (!hashes) return r; }
            foreach (var b in Ecs.Componentes(em))
            {
                ulong h = Ecs.Prefab(em, b); if (h == 0 || (h != hAnel && h != hMini)) continue;   // 0.22.4: qualquer anel em qualquer junta
                var part = Ecs.Pai(em, b); if (Ecs.Nulo(part)) continue;
                r.Add(new Candidato { e = b, part = part, pos = Ecs.Posicao(em, b) });
            }
            return r;
        }
        /// <summary>Bearing / pistao: anel no eixo (+Y do bloco), alem do bloco, na part de cima; o mais perto ao longo do eixo.</summary>
        static Entity AnelNoEixo(EntityManager em, Junta j, List<Candidato> aneis, HashSet<long> usados, out string porque)
        {
            porque = ""; Ecs.Eixos(em, j.bloco, out _, out var up, out _); var p0 = Ecs.Posicao(em, j.bloco);
            Entity melhor = Entity.Null; float melhorAlong = float.MaxValue; int foraEixo = 0;
            foreach (var a in aneis)
            {
                if (usados.Contains(Ecs.K(a.e)) || a.part.Index != j.partB.Index) continue;
                var d = Ecs.Sub(a.pos, p0); float along = Ecs.Dot(d, up); var lat = Ecs.Sub(d, Ecs.Mul(up, along)); float lt = Ecs.Comp(lat);
                if (along < 0.3f * j.esc || lt > 0.1f) { foraEixo++; continue; }
                if (along < melhorAlong) { melhorAlong = along; melhor = a.e; }
            }
            if (Ecs.Nulo(melhor) && foraEixo > 0) porque = foraEixo + " ring(s) on the moving piece but off the joint's axis (more than 10 cm sideways)";
            return melhor;
        }
        /// <summary>Recoupler: anel colado no decoupler onde ele engatou (mesma part do decoupler, ate 0,75 m do centro dele).</summary>
        static Entity AnelPerto(EntityManager em, Entity decoupler, List<Candidato> aneis, HashSet<long> usados)
        {
            var part = Ecs.Pai(em, decoupler); var p0 = Ecs.Posicao(em, decoupler);
            Entity melhor = Entity.Null; float md = 0.75f;
            foreach (var a in aneis)
            {
                if (usados.Contains(Ecs.K(a.e)) || a.part.Index != part.Index) continue;
                float d = Ecs.Dist(a.pos, p0); if (d < md) { md = d; melhor = a.e; }
            }
            return melhor;
        }
        /// <summary>Turret: anel na peca pendurada na placa (qualquer lugar da part dela; o mais perto do turret).</summary>
        static Entity AnelNaPart(EntityManager em, Entity part, float3 p0, List<Candidato> aneis, HashSet<long> usados)
        {
            Entity melhor = Entity.Null; float md = float.MaxValue;
            foreach (var a in aneis)
            {
                if (usados.Contains(Ecs.K(a.e)) || a.part.Index != part.Index) continue;
                float d = Ecs.Dist(a.pos, p0); if (d < md) { md = d; melhor = a.e; }
            }
            return melhor;
        }
        static readonly Dictionary<string, string> semAnelLog = new Dictionary<string, string>();
        static void Varrer(EntityManager em)
        {
            foreach (var l in ligas.Values) l.viva = false;
            var aneis = Aneis(em); var usados = new HashSet<long>();
            if (aneis.Count > 0)
            {
                foreach (var j in Juntas.JuntasAtivas())
                {
                    if ((j.tipo != Tipo.Bearing && j.tipo != Tipo.Pistao) || Ecs.Nulo(j.partB)) continue;
                    var a = AnelNoEixo(em, j, aneis, usados, out var porque);
                    string k = Ecs.K(j.bloco) + ":0";
                    if (Ecs.Nulo(a)) { if (porque.Length > 0 && (!semAnelLog.TryGetValue(k, out var ant) || ant != porque)) { semAnelLog[k] = porque; Registro.Aviso(j.nome + " " + j.bloco.Index + ": no slip ring — " + porque); } continue; }
                    semAnelLog.Remove(k);
                    Ligar(em, j.bloco, j.nome, 0, a, usados);
                }
                foreach (var r in Acoplador.Acoplados())
                {
                    if (Ecs.Nulo(r.decoupler) || !Ecs.Vivo(em, r.decoupler)) continue;
                    var a = AnelPerto(em, r.decoupler, aneis, usados); if (Ecs.Nulo(a)) continue;
                    Ligar(em, r.bloco, "SC_Recoupler", 0, a, usados);
                }
                foreach (var t in Juntas.TorresAtivas())
                {
                    var p0 = Ecs.Posicao(em, t.bloco);
                    foreach (var mv in t.movs)
                    {
                        if (Ecs.Nulo(mv.part) || !Ecs.Vivo(em, mv.part)) continue;
                        string face = (mv.faces ?? "").Split('+')[0]; int c = Array.IndexOf(PLACAS, face); if (c < 0) continue;
                        var a = AnelNaPart(em, mv.part, p0, aneis, usados); if (Ecs.Nulo(a)) continue;
                        Ligar(em, t.bloco, t.nome, c, a, usados);
                    }
                }
            }
            Limpar(em);
        }
        static void Ligar(EntityManager em, Entity junta, string nome, int conjunto, Entity anel, HashSet<long> usados)
        {
            usados.Add(Ecs.K(anel));
            string chave = Ecs.K(junta) + ":" + conjunto;
            if (ligas.TryGetValue(chave, out var l) && l.anel.Index == anel.Index && l.anel.Version == anel.Version) { l.viva = true; return; }
            if (l != null) { Desligar(em, l, "the slip ring changed"); ligas.Remove(chave); }
            var ps = Ecs.Portas(em, junta); var pa = Ecs.Portas(em, anel); int n = NLaterais(nome);
            Entity[] lat;
            if (nome != null && nome.StartsWith("SC_TurretSeat")) { if (ps.Length < 16) { Registro.Aviso(nome + " " + junta.Index + ": the prefab has " + ps.Length + " port(s), expected 16 — rebuild the bundle in the Modkit"); return; } lat = new Entity[4]; Array.Copy(ps, ps.Length - 16 + conjunto * 4, lat, 0, 4); }
            else { if (!Faixa(em, ps, nome, out int ini, out n)) { Registro.Aviso(nome + " " + junta.Index + ": the prefab has " + ps.Length + " port(s), expected at least " + NLaterais(nome) + " — rebuild the bundle in the Modkit"); return; } lat = new Entity[n]; Array.Copy(ps, ini, lat, 0, n); }
            int m = Math.Min(lat.Length, pa.Length);
            l = new Liga { chave = chave, junta = junta, anel = anel, conjunto = conjunto, viva = true,
                           nome = nome + " " + junta.Index + (nome != null && nome.StartsWith("SC_TurretSeat") ? " (" + PLACAS[conjunto] + " plate)" : "") + " <-> " + (pa.Length > 0 && Ecs.Prefab(em, anel) == hMini ? "Mini Slip Ring " : "Slip Ring ") + anel.Index };
            for (int i = 0; i < m; i++) l.canais.Add(new Canal { pj = lat[i], pa = pa[i], i = i });
            ligas[chave] = l;
            Registro.Info("slip ring: " + l.nome + " — " + m + " channel(s) (part " + Ecs.Pai(em, junta).Index + " <-> part " + Ecs.Pai(em, anel).Index + ")");
        }
        static void Limpar(EntityManager em)
        {
            var mortas = new List<string>();
            foreach (var kv in ligas) if (!kv.Value.viva || !Ecs.Vivo(em, kv.Value.junta) || !Ecs.Vivo(em, kv.Value.anel)) mortas.Add(kv.Key);
            foreach (var k in mortas) { Desligar(em, ligas[k], "joint gone, ring removed or no longer on the moving piece"); ligas.Remove(k); }
        }
        static void Desligar(EntityManager em, Liga l, string motivo)
        {
            foreach (var c in l.canais) { DesfazerPonte(em, c); }
            Registro.Info("slip ring: " + l.nome + " — gone (" + motivo + ")");
        }

        // ================================================================ por frame: dados (runtimeEnd) ou energia (fantasmas)
        static void Aplicar(EntityManager em, Liga l, Canal c, float dt)
        {
            var fJ = Ecs.Fonte(em, c.pj, out _); var fA = Ecs.Fonte(em, c.pa, out _);
            byte tJ = 255, tA = 255;
            bool okJ = !Ecs.Nulo(fJ) && Ecs.Porta(em, fJ, out tJ, out _, out _), okA = !Ecs.Nulo(fA) && Ecs.Porta(em, fA, out tA, out _, out _);
            if (!okJ) tJ = 255; if (!okA) tA = 255;
            bool enJ = okJ && tJ == Ecs.PortaEnergia, enA = okA && tA == Ecs.PortaEnergia;
            int sentido; string desc;
            if (enJ && enA) { sentido = 3; desc = "power bridge"; }
            else if (enJ || enA) { sentido = 0; desc = "a POWER cable on one side and a data cable (or nothing) on the other — nothing passes"; }
            else
            {
                bool saiJ = okJ && tJ == Ecs.PortaSaida, saiA = okA && tA == Ecs.PortaSaida;
                bool entJ = okJ && (tJ == Ecs.PortaEntrada || tJ == Juntas.PortaSemTipo), entA = okA && (tA == Ecs.PortaEntrada || tA == Juntas.PortaSemTipo);
                if (saiJ && saiA) { sentido = 2; desc = "OUTPUTS on both sides (port " + fJ.Index + " and port " + fA.Index + ") — nothing passes"; }
                else if (saiJ && entA) { sentido = 1; desc = "data joint -> ring: output " + fJ.Index + " (block " + Ecs.Pai(em, fJ).Index + ") -> input " + fA.Index + " (block " + Ecs.Pai(em, fA).Index + ")"; }
                else if (saiA && entJ) { sentido = -1; desc = "data ring -> joint: output " + fA.Index + " (block " + Ecs.Pai(em, fA).Index + ") -> input " + fJ.Index + " (block " + Ecs.Pai(em, fJ).Index + ")"; }
                else { sentido = 0; desc = okJ || okA ? "no output on either side" : "nothing connected"; }
            }
            if (sentido != c.sentido || desc != c.ultimo)
            {
                if (c.sentido == 3 && sentido != 3) DesfazerPonte(em, c);
                c.sentido = sentido; c.ultimo = desc; c.tLog = 99f;
                if (sentido != 0 || desc != "nothing connected") { if (sentido == 2 || desc.Contains("nothing passes")) Registro.Aviso("slip ring: " + l.nome + " #" + c.i + ": " + desc); else Registro.Info("slip ring: " + l.nome + " #" + c.i + ": " + desc); }
            }
            // (0.23.0 trouxe um log da trilha e das mudancas de valor pra caçar um atraso que nao existia — era cabo faltando no
            // circuito do Andre; o Delay do jogo passa certinho pela ponte. Removido na 0.23.1.)
            if (sentido == 1) { if (Escrever(em, fA, fJ)) c.escritas++; }
            else if (sentido == -1) { if (Escrever(em, fJ, fA)) c.escritas++; }
            else if (sentido == 3) Energia(em, l, c, fJ, fA, dt);
        }
        static unsafe bool Escrever(EntityManager em, Entity entrada, Entity saida)
        {
            if (!Ecs.TemC(em, entrada, Ecs.tiPort)) return false;
            var b = (byte*)em.GetComponentDataRawRW(entrada, Ecs.tiPort); if (b == null) return false;
            var fim = (Entity*)(b + 12);
            if (fim->Index == saida.Index && fim->Version == saida.Version) return false;
            *fim = saida; return true;
        }

        // ================================================================ energia (0.21.4, inalterado): fantasmas nos circuitos
        // O jogo monta um circuito por rede de cabos DENTRO da part (ElectricCircuitRoot + buffers ElectricCircuitPort /
        // ElectricCircuitComponent) e refaz esses roots toda hora. O SpaceshipElectricUpdateCircuits (o UNICO que le o buffer de
        // componentes, so pelo SCElectricPower) soma _generatePerSec (+28) e _consumptionPerSec (+32) dos componentes, drena as baterias
        // (_runtimePower +0) pelo deficit e copia (total, real, geracao) no _circuitInfo (+8) de cada componente. Escrever nos blocos do
        // JOGO realimentava (gerador de plasma: 119 mil P/s) — por isso cada lado ganha um FANTASMA: entity nossa so com SCElectricPower,
        // acrescentada ao buffer de componentes do circuito (cabe no capacity; refeita quando o jogo refaz o root).
        class Ponte { public Entity fantJ, fantA, raizJ, raizA; public float maxCabo = 150f; public float q; public int sentido; public float tLog = 99f; public int avisoBuffer; }
        static TypeIndex tiRaiz, tiBufComp, tiBufPorta; static EntityQuery qRaiz; static bool circOk, circFalhou;
        static bool PrepararCircuitos(EntityManager em)
        {
            if (circOk) return true; if (circFalhou) return false;
            try
            {
                tiRaiz = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<ElectricCircuitRoot>());
                tiBufPorta = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<ElectricCircuitPort>());
                tiBufComp = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<ElectricCircuitComponent>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiRaiz);
                qRaiz = em.CreateEntityQuery(a); circOk = true; return true;
            }
            catch (Exception ex) { circFalhou = true; Registro.Aviso("power bridge: circuits not available: " + ex.Message); return false; }
        }
        /// <summary>Menor _maxPower (+12) das celulas do cabo que sai da porta.</summary>
        static unsafe float MaxCabo(EntityManager em, Entity porta)
        {
            float m = float.MaxValue;
            try
            {
                if (!Ecs.PortaCruaPub(em, porta, out _, out _, out _, out var persist)) return 150f;
                var prev = porta; var cur = persist; var visto = new HashSet<long>();
                for (int i = 0; i < 2048 && Ecs.EhCabo(em, cur) && visto.Add(Ecs.K(cur)); i++)
                {
                    var b = (byte*)em.GetComponentDataRawRO(cur, Ecs.tiCabo); if (b == null) break;
                    float mp = *(float*)(b + 12); if (mp > 0f && mp < m) m = mp;
                    var fwd = *(Entity*)(b + 0x10); var bwd = *(Entity*)(b + 0x18);
                    var prox = (fwd.Index == prev.Index && fwd.Version == prev.Version) ? bwd : fwd; prev = cur; cur = prox;
                    if (Ecs.Nulo(cur)) break;
                }
            }
            catch (Exception) { }
            return m == float.MaxValue ? 150f : m;
        }
        /// <summary>Root do circuito que lista a porta (cache; se o jogo refez os roots, procura de novo). A porta de passagem (sem tipo)
        /// pode nao entrar na lista do jogo: tenta ela e, se nao, a porta de energia da outra ponta do cabo.</summary>
        static Entity RaizDaPorta(EntityManager em, Entity porta, Entity fonte, Entity cache)
        {
            if (Ecs.Vivo(em, cache) && Ecs.TemC(em, cache, tiRaiz)) foreach (var pe in Ecs.BufferEntities(em, cache, tiBufPorta)) if ((pe.Index == porta.Index && pe.Version == porta.Version) || (pe.Index == fonte.Index && pe.Version == fonte.Version)) return cache;
            foreach (var r in Ecs.Lista(qRaiz)) foreach (var pe in Ecs.BufferEntities(em, r, tiBufPorta)) if ((pe.Index == porta.Index && pe.Version == porta.Version) || (pe.Index == fonte.Index && pe.Version == fonte.Version)) return r;
            return Entity.Null;
        }
        static unsafe Entity Fantasma(EntityManager em, Entity atual)
        {
            if (Ecs.Vivo(em, atual) && Ecs.TemC(em, atual, Ecs.tiPower)) return atual;
            var tipos = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(1); tipos[0] = ComponentType.ReadWrite(Ecs.tiPower);
            var e = em.CreateEntity(tipos);
            var b = (byte*)em.GetComponentDataRawRW(e, Ecs.tiPower); if (b != null) for (int i = 0; i < 44; i++) b[i] = 0;
            return e;
        }
        /// <summary>Garante o fantasma no buffer ElectricCircuitComponent do root (BufferHeader: Pointer, Length, Capacity; elementos inline
        /// depois do cabecalho quando Pointer == null). Sem capacidade sobrando, nao mexe (avisa).</summary>
        static unsafe bool NoCircuito(EntityManager em, Entity raiz, Entity fant, Ponte pt)
        {
            if (!Ecs.TemC(em, raiz, tiBufComp)) return false;
            var h = (byte*)em.GetComponentDataRawRW(raiz, tiBufComp); if (h == null) return false;
            byte* ptr = *(byte**)h; int len = *(int*)(h + 8), cap = *(int*)(h + 12);
            var dados = (Entity*)(ptr != null ? ptr : h + 16);
            if (len < 0 || len > 4096 || cap < len) return false;
            for (int i = 0; i < len; i++) if (dados[i].Index == fant.Index && dados[i].Version == fant.Version) return true;
            if (len >= cap) { if (pt.avisoBuffer++ < 1) Registro.Aviso("power bridge: the circuit's component buffer is full (" + len + "/" + cap + ") — cannot add the bridge entity"); return false; }
            dados[len] = fant; *(int*)(h + 8) = len + 1; return true;
        }
        static unsafe bool Circuito(EntityManager em, Entity fant, out float consumo, out float real, out float geracao)
        {
            consumo = real = geracao = 0f;
            if (!Ecs.Vivo(em, fant) || !Ecs.TemC(em, fant, Ecs.tiPower)) return false;
            var b = (float*)((byte*)em.GetComponentDataRawRO(fant, Ecs.tiPower) + 8); consumo = b[0]; real = b[1]; geracao = b[2]; return true;
        }
        static unsafe float Guardada(EntityManager em, Entity raiz)
        {
            float e = 0f;
            foreach (var c in Ecs.BufferEntities(em, raiz, tiBufComp))
            {
                if (!Ecs.TemC(em, c, Ecs.tiPower)) continue;
                var b = (byte*)em.GetComponentDataRawRO(c, Ecs.tiPower); if (b != null) e += (float)*(double*)b;
            }
            return e;
        }
        static unsafe void EscreverFantasma(EntityManager em, Entity fant, float gera, float consome)
        {
            if (!Ecs.Vivo(em, fant) || !Ecs.TemC(em, fant, Ecs.tiPower)) return;
            var b = (byte*)em.GetComponentDataRawRW(fant, Ecs.tiPower); if (b == null) return;
            *(float*)(b + 28) = gera; *(float*)(b + 32) = consome;
        }
        /// <summary>Lado J = junta, lado A = anel. fJ / fA = portas de energia nas outras pontas dos cabos.</summary>
        static void Energia(EntityManager em, Liga l, Canal c, Entity fJ, Entity fA, float dt)
        {
            if (!PrepararCircuitos(em)) return;
            var pt = c.ponte;
            if (pt == null) { pt = c.ponte = new Ponte { maxCabo = Math.Min(MaxCabo(em, c.pj), MaxCabo(em, c.pa)) }; Registro.Info("power bridge: " + l.nome + " #" + c.i + " — cables rated " + pt.maxCabo.ToString("F0") + " P/s"); }
            pt.raizJ = RaizDaPorta(em, c.pj, fJ, pt.raizJ); pt.raizA = RaizDaPorta(em, c.pa, fA, pt.raizA);
            if (Ecs.Nulo(pt.raizJ) || Ecs.Nulo(pt.raizA) || pt.raizJ.Index == pt.raizA.Index) return;
            pt.fantJ = Fantasma(em, pt.fantJ); pt.fantA = Fantasma(em, pt.fantA);
            bool okJ = NoCircuito(em, pt.raizJ, pt.fantJ, pt), okA = NoCircuito(em, pt.raizA, pt.fantA, pt);
            if (!okJ || !okA) { EscreverFantasma(em, pt.fantJ, 0f, 0f); EscreverFantasma(em, pt.fantA, 0f, 0f); return; }
            // totais do tick anterior (copiados pelo jogo no fantasma): consumo proprio de cada lado = total menos o que o fantasma de la consome
            if (!Circuito(em, pt.fantJ, out var cJ, out _, out var gJ) || !Circuito(em, pt.fantA, out var cA, out _, out var gA)) return;
            float consJinj = pt.sentido == 1 ? pt.q : 0f, consAinj = pt.sentido == -1 ? pt.q : 0f, gerJinj = pt.sentido == -1 ? pt.q : 0f, gerAinj = pt.sentido == 1 ? pt.q : 0f;
            float cJp = Math.Max(0f, cJ - consJinj), cAp = Math.Max(0f, cA - consAinj), gJp = Math.Max(0f, gJ - gerJinj), gAp = Math.Max(0f, gA - gerAinj);
            float eJ = Guardada(em, pt.raizJ), eA = Guardada(em, pt.raizA);
            float dispJ = eJ + gJp, dispA = eA + gAp;
            int sentido = pt.sentido;
            if (sentido == 0 || (sentido == 1 && dispJ < dispA * 0.9f) || (sentido == -1 && dispA < dispJ * 0.9f)) sentido = dispJ >= dispA ? 1 : -1;
            // o doador cede o que o outro lado consome alem do que ele mesmo gera; so se tem energia (bateria com carga ou geracao). Sem cap.
            float q = 0f;
            if (sentido == 1 && dispJ > 0.01f) q = Math.Max(0f, cAp - gAp);
            else if (sentido == -1 && dispA > 0.01f) q = Math.Max(0f, cJp - gJp);
            if (sentido == 1) { EscreverFantasma(em, pt.fantJ, 0f, q); EscreverFantasma(em, pt.fantA, q, 0f); } else { EscreverFantasma(em, pt.fantJ, q, 0f); EscreverFantasma(em, pt.fantA, 0f, q); }
            pt.q = q; pt.tLog += dt;
            if (sentido != pt.sentido || pt.tLog >= 10f)
            {
                pt.tLog = 0f; pt.sentido = sentido;
                if (LogDetalhe) Registro.Info("power bridge: " + l.nome + " #" + c.i + " — " + (sentido == 1 ? "joint -> ring" : "ring -> joint") + ": joint side stores " + eJ.ToString("F0") + " P (gen " + gJp.ToString("F1") + ", uses " + cJp.ToString("F1") + " P/s), ring side stores " + eA.ToString("F0") + " P (gen " + gAp.ToString("F1") + ", uses " + cAp.ToString("F1") + " P/s) — bridging " + q.ToString("F1") + " P/s" + (q > pt.maxCabo ? " (ABOVE the cables' " + pt.maxCabo.ToString("F0") + " P/s — they will burn)" : ""));
            }
        }
        static void DesfazerPonte(EntityManager em, Canal c)
        {
            var pt = c.ponte; if (pt == null) return;
            try { if (Ecs.Vivo(em, pt.fantJ)) em.DestroyEntity(pt.fantJ); } catch (Exception) { }
            try { if (Ecs.Vivo(em, pt.fantA)) em.DestroyEntity(pt.fantA); } catch (Exception) { }
            c.ponte = null;
        }

        // ================================================================ 0.22.2: plugue das portas de passagem
        // (Andre: "a porta sem tipo e' igual a do Frame Quarter With Ports: sem cor quando desconectado e com cor quando conecta"). O jogo so faz
        // isso pra quem tem SCTypeFrameWithPorts (SCTick_FrameWithPorts): troca a MALHA do renderer do plugue (CRPRendererData, DrawDataMeshReference
        // em +8 = MeshReference._instanceID) entre _portMeshUnset / _portMeshInOut / _portMeshPower / _portMeshPlasma. Aqui o mesmo, pelo cabo
        // ligado em cada porta de passagem: cabo de dados = Port InOut, energia = Port Power, nada = Port Unset. Vale na garagem tambem.
        static TypeIndex tiRendData; static bool rdOk, rdFalhou; static int avisosCor, passesCor;
        static int idUnset, idInOut, idPower; static bool idsOk, idsLogados;
        static readonly Dictionary<long, int> malhaEscrita = new Dictionary<long, int>();   // porta -> instanceID escrito
        static readonly Dictionary<long, Entity> rendDaPorta = new Dictionary<long, Entity>();
        static bool PrepararPlugues()
        {
            if (!rdOk && !rdFalhou)
            {
                try { tiRendData = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<CRPRendererData>()); rdOk = true; }
                catch (Exception ex) { rdFalhou = true; Registro.Aviso("pass-through plugs: CRPRendererData: " + ex.Message); }
            }
            if (!rdOk) return false;
            if (!idsOk)
            {
                var mu = JogoMalhas.Malha("Port Unset"); var mi = JogoMalhas.Malha("Port InOut"); var mp = JogoMalhas.Malha("Port Power");
                if (mu == null || mi == null || mp == null) return false;
                idUnset = new MeshReference(mu)._instanceID; idInOut = new MeshReference(mi)._instanceID; idPower = new MeshReference(mp)._instanceID;
                idsOk = true;
            }
            return true;
        }
        static bool TemPassagem(ulong hash, out string nome, out int n)
        {
            nome = null; n = 0;
            if (!Juntas.TipoDoHash(hash, out var tipo, out _, out nome) || nome == null) return false;
            if (nome.EndsWith("SlipRing")) { n = 4; return true; }
            if (tipo != Tipo.Bearing && tipo != Tipo.Pistao && tipo != Tipo.Recoupler && tipo != Tipo.Torreta) return false;
            n = NLaterais(nome); return n > 0;
        }
        /// <summary>Renderer do plugue: o filho do bloco com CRPRendererData na posicao da porta.</summary>
        static Entity RendererDaPorta(EntityManager em, Entity bloco, Entity porta)
        {
            long k = Ecs.K(porta);
            if (rendDaPorta.TryGetValue(k, out var r) && (Ecs.Nulo(r) || (Ecs.Vivo(em, r) && Ecs.TemC(em, r, tiRendData)))) return r;
            r = Entity.Null;
            if (Ecs.TemC(em, porta, tiRendData)) r = porta;
            else if (Ecs.TemC(em, porta, Ecs.tiLT))
            {
                var pp = Ecs.LT(em, porta).Position;
                foreach (var f in Ecs.Filhos(em, bloco))
                {
                    if (f.Index == porta.Index || !Ecs.TemC(em, f, tiRendData) || !Ecs.TemC(em, f, Ecs.tiLT)) continue;
                    if (Ecs.Dist(Ecs.LT(em, f).Position, pp) < 0.002f) { r = f; break; }
                }
            }
            rendDaPorta[k] = r; return r;
        }
        static readonly Dictionary<long, long> parPorta = new Dictionary<long, long>();   // 0.23.1: porta <-> porta do outro lado da junta

        /// <summary>0.23.1 (Andre: "o frame with ports, quando conecto um cabo de um lado, ja mostra do outro lado a cor do cabo que
        /// vai sair — da pra fazer?"): pares porta da junta <-> porta do anel, pelas poses da GARAGEM (GarageTransform, que fica no
        /// bloco mesmo depois de lancar), pra valer na garagem e na simulacao. Bearing/pistao so: o anel no eixo (+Y do bloco), na
        /// frente, de lado < 5 cm, o mais perto. As ligas da simulacao mandam por cima (sao a ligacao de verdade).</summary>
        static void Pares(EntityManager em, List<Entity> blocos)
        {
            parPorta.Clear();
            var aneis = new List<(Entity e, string nome, RigidTransform g)>();
            var jun = new List<(Entity e, string nome, float alcance, RigidTransform g)>();
            foreach (var b in blocos)
            {
                ulong h; try { h = Ecs.Prefab(em, b); } catch (Exception) { continue; }
                if (h == 0 || !Juntas.TipoDoHash(h, out var tp, out float curso, out var nome) || nome == null) continue;
                if (!Garagem.GT(em, b, out var g)) continue;
                if (nome.EndsWith("SlipRing")) { aneis.Add((b, nome, g)); continue; }
                if (tp == Tipo.Bearing) jun.Add((b, nome, 1.2f, g));
                else if (tp == Tipo.Pistao) jun.Add((b, nome, curso + 1f, g));
            }
            if (aneis.Count == 0 || jun.Count == 0) { ParesDasLigas(); return; }
            var usados = new HashSet<long>();
            foreach (var j in jun)
            {
                var up = Ecs.Rot(j.g.rot, Ecs.UY); float esc = Juntas.EscalaDoNome(j.nome);
                Entity melhor = Entity.Null; string nomeA = null; float melhorAo = 1e9f;
                foreach (var a in aneis)
                {
                    if (usados.Contains(Ecs.K(a.e))) continue;
                    var d = Ecs.Sub(a.g.pos, j.g.pos); float ao = Ecs.Dot(d, up);
                    float lat = Ecs.Comp(Ecs.Sub(d, Ecs.Mul(up, ao)));
                    if (ao < 0.2f * esc || ao > j.alcance || lat > 0.05f) continue;
                    if (ao < melhorAo) { melhorAo = ao; melhor = a.e; nomeA = a.nome; }
                }
                if (Ecs.Nulo(melhor)) continue;
                usados.Add(Ecs.K(melhor));
                var ps = Ecs.Portas(em, j.e); var pa = Ecs.Portas(em, melhor);
                if (!Faixa(em, ps, j.nome, out int i0, out int n0) || !Faixa(em, pa, nomeA, out int i1, out int n1)) continue;
                int m = Math.Min(n0, n1);
                for (int i = 0; i < m; i++) { long kj = Ecs.K(ps[i0 + i]), ka = Ecs.K(pa[i1 + i]); parPorta[kj] = ka; parPorta[ka] = kj; }
            }
            ParesDasLigas();
        }
        static void ParesDasLigas()
        {
            foreach (var l in ligas.Values) foreach (var c in l.canais) { long kj = Ecs.K(c.pj), ka = Ecs.K(c.pa); parPorta[kj] = ka; parPorta[ka] = kj; }
        }

        static unsafe void TingirTodas(EntityManager em)
        {
            if (!Juntas.HashesOk || !PrepararPlugues()) return;
            if (++passesCor % 10 == 0) { malhaEscrita.Clear(); rendDaPorta.Clear(); }   // a cada 5 s confere de novo (o jogo pode refazer a part)
            int semRenderer = 0;
            var blocos = Ecs.Componentes(em); var vistos = new HashSet<long>();
            foreach (var g in Garagem.Blocos(em)) blocos.Add(g);   // 0.22.4: na garagem tambem (o qComps so pega blocos lancados)
            var lista = new List<(Entity bloco, Entity porta, long k)>(); var proprio = new Dictionary<long, int>();
            var unicos = new List<Entity>();
            foreach (var b in blocos)
            {
                if (!vistos.Add(Ecs.K(b))) continue;
                unicos.Add(b);
                ulong h = Ecs.Prefab(em, b); if (h == 0 || !TemPassagem(h, out var nome, out int n)) continue;
                var ps = Ecs.Portas(em, b); if (!Faixa(em, ps, nome, out int i0, out n)) continue;   // 0.23.1: a saida de angulo do bearing e' a ULTIMA porta — nao entra aqui
                for (int i = i0; i < i0 + n; i++)
                {
                    var porta = ps[i]; long k = Ecs.K(porta); int quer = idUnset;
                    if (Ecs.PortaCruaPub(em, porta, out _, out _, out _, out var persist) && Ecs.EhCabo(em, persist))
                        quer = CaboDeEnergia(em, persist) ? idPower : idInOut;
                    proprio[k] = quer; lista.Add((b, porta, k));
                }
            }
            Pares(em, unicos);
            foreach (var it in lista)
            {
                int quer = proprio[it.k];
                // 0.23.1: sem cabo deste lado, mostra a cor do cabo do OUTRO lado da junta (igual o Frame With Ports)
                if (quer == idUnset && parPorta.TryGetValue(it.k, out var kp) && proprio.TryGetValue(kp, out var outro)) quer = outro;
                if (malhaEscrita.TryGetValue(it.k, out var esc) && esc == quer) continue;
                var r = RendererDaPorta(em, it.bloco, it.porta);
                if (Ecs.Nulo(r)) { semRenderer++; malhaEscrita[it.k] = quer; continue; }
                var p = (byte*)em.GetComponentDataRawRW(r, tiRendData); if (p == null) continue;
                int atual = *(int*)(p + 8);
                if (!idsLogados) { idsLogados = true; Registro.Info("pass-through plugs: plug mesh ids — Unset " + idUnset + ", InOut " + idInOut + ", Power " + idPower + "; first plug renderer has " + atual + (atual == idUnset || atual == idInOut || atual == idPower ? " (matches)" : " (does NOT match — the swap may not show)")); }
                if (atual != quer) *(int*)(p + 8) = quer;
                malhaEscrita[it.k] = quer;
            }
            if (semRenderer > 0 && avisosCor++ < 1) Registro.Aviso("pass-through plugs: " + semRenderer + " port(s) without a plug renderer found");
        }
        /// <summary>Tipo do cabo pela celula ligada na porta: SCTypeCableCell._type (+1, SpaceshipCableType: 0 Data, 1 Power, 2 PowerNano, 3 Plasma).</summary>
        static unsafe bool CaboDeEnergia(EntityManager em, Entity cel)
        {
            try { var b = (byte*)em.GetComponentDataRawRO(cel, Ecs.tiCabo); return b != null && (b[1] == 1 || b[1] == 2); } catch (Exception) { return false; }
        }

        /// <summary>Saida do plugin / limpeza com o mundo vivo: destroi os fantasmas.</summary>
        public static void Encerrar(EntityManager em) { foreach (var l in ligas.Values) foreach (var c in l.canais) DesfazerPonte(em, c); ligas.Clear(); semAnelLog.Clear(); malhaEscrita.Clear(); rendDaPorta.Clear(); }
        public static void Esquecer() { ligas.Clear(); semAnelLog.Clear(); hashes = false; malhaEscrita.Clear(); rendDaPorta.Clear(); parPorta.Clear(); }
        public static string Situacao()
        {
            if (ligas.Count == 0) return "no slip rings linked";
            var sb = new System.Text.StringBuilder(); sb.Append(ligas.Count).Append(" slip ring link(s):");
            foreach (var l in ligas.Values)
            {
                sb.Append("\n    ").Append(l.nome).Append(':');
                foreach (var c in l.canais) sb.Append(" #").Append(c.i).Append('=').Append(c.sentido == 1 ? "J>R" : c.sentido == -1 ? "R>J" : c.sentido == 3 ? "power" : c.sentido == 2 ? "CONFLICT" : "-");
            }
            return sb.ToString();
        }
    }
}
