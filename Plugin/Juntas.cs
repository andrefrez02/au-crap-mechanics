using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;

namespace Mecanica
{
    internal enum Tipo { Pistao, Bearing, Motor, Recoupler, Beacon, Alavancas, Torreta, Transmissao, Radio }

    /// <summary>Um pistao ou bearing dentro de uma nave lancada, e o joint de fisica que ele mantem.</summary>
    internal class Junta
    {
        public Entity bloco, vizinho, partA, partB, joint, joint2, joint3;
        public Tipo tipo; public float curso; public string nome;
        public bool espelho;      // 0.12.14: versao espelhada (V): bearing gira ao contrario com o mesmo sinal
        public int estado;        // 0 esperando lancamento, 1 separando, 2 ativo
        public float tEstado;     // segundos no estado atual
        public int idxMotor = -1; public float alvo, alvoAtual;
        public float3 eixo, ancora;
        // 0.7.4: ponto/eixo/perpendicular do encaixe no lado de cima guardados no espaco do BLOCO vizinho (nao da part, que o
        // jogo recria). Assim da pra recriar o joint na mesma pose quando o jogo refaz as parts.
        public float3 bLocP, bLocE, bLocQ, gP, gE, gQ; public long locViz = -1; public int adiamentos;
        // pistao: curso (m) em que os dois frames do joint coincidem. 0 na 1a vez; ao recriar = curso atual. Alvo do motor =
        // -(curso pedido - cursoBase), igual a convencao que ja funciona na 1a vez (frames juntos, -x estica x).
        public float cursoBase; public float tAlinha; public string alinhaLog;
        // pistao com carga reacoplada (0.7.6): joint extra do motor direto no pedaco pendurado pelo Recoupler (o carro), pra
        // nao depender de uma corrente pistao -> topo leve (110 kg) -> carro pesado (7 t), que nao aguentava erguer
        public Entity extra, extraParte; public int idxExtra = -1; public float cursoBaseExtra;
        public float tPartida;   // 0.7.8: segundos que o joint ainda fica travado depois de nascer (partida suave)
        public List<Entity> guias = new List<Entity>();   // 0.7.9: trilhos do pistao (prismaticos paralelos ao eixo)
        // 0.7.12: bearing que nasceu fora do eixo e' puxado pro lugar certo durante a partida (joint fixo cujo frame do lado A
        // anda de onde a roda esta ate o encaixe certo, igual o Recoupler); apA/apB = pontos de apoio extras (local)
        public float angServo = 999f, angLog = 999f, tAngLog, angCont;   // 0.19.1: angCont = angulo sem cortar em 180 (servo de ate 360)   // 0.7.14: servo feito pelo mod (angulo medido, rad)
        public BodyFrame puxA0, puxA1; public float tPux = -1f, durPux; public float3[] apA0, apA1, apB;
        public BodyFrame fa, fb;
        public int modo = -1;     // bearing: 0 livre, 1 motor, 2 servo
        public Entity knob; public float tKnob = 9f; public int knobDica = int.MinValue; public float grausServo = float.NaN, fracaoCurso = 1f;   // 0.19.2: knob no bloco
        public float impulsoModo; public string origemModo;
        public int avisos;
        // joints da nave (SpaceshipSingleton._joints) que a gente forcou a quebrar: indice e _breakForce original, pra devolver depois
        public List<(int idx, Entity a, Entity b, float breakForce)> quebrados = new List<(int, Entity, Entity, float)>();
        public string sinalMotivo; public float sinalLogado = -999f; public float tSinalLog;
        public byte energiaEstado = 254;
        // joints do jogo em volta do bloco (dele e dos blocos colados nele): pra reforcar e pra vigiar o strain
        public List<(int idx, Entity a, Entity b)> vigiados = new List<(int, Entity, Entity)>();
        public float strainMax, strainMaxBf; public int strainMaxIdx = -1; public Entity strainA, strainB;
        // 0.11.0: pistao — renderers separados da haste e da cabeca (filhos do bloco); o mod estica/sobe conforme o curso
        public Entity haste, cabeca; public float visualExt = -1f; public int visualTentativas; public float tVisual, tRecolher; public bool recolhidoLog, garagemLog;
        public int falhasSeparar; public bool avisouPreso;   // 0.11.1: tentativas de separar que nao deram certo / aviso de "preso por outro caminho"
        public bool guia; public bool soldado;                 // 0.12.1: guia = pistao paralelo a outro ligando as mesmas pecas (so desliza); soldado = anel/preso
        public Entity[] leds; public int ledEstado = -2; public float tLeds = 9f; public float3[] ledRepouso;   // 0.17.0: + leds do bearing (travado)   // 0.12.1: leds verde/amarelo/vermelho (renderers extras)
    }

    /// <summary>Motor: le a entrada (-1..1), puxa energia da porta vermelha e publica na saida o sinal efetivo.
    /// O bearing ligado na saida (cabo de dados, pode atravessar Frame With Ports) usa velMax/impulso dele.</summary>
    internal class Motor
    {
        public Entity bloco, entrada, energia, saida; public string nome;
        public Entity freioPorta; public float freio; public bool freioLog;   // 0.11.0: 2a entrada = freio (0..1), trava as rodas
        public float velMax, impulso, consumoMax;
        public float sinal; public bool alimentado; public byte estado = 254;
        public bool portasOk; public int avisos; public string trilha; public float sinalLog = -9f;
        // 0.8.0: motor a combustivel (o bloco e' um EPC_SCPipe: os canos do jogo enchem o FuelData dele)
        public bool comb; public float impulsoBase, rendimento = 1f, combTem = -1f, uso = 0f; public string combLog; public float rendLog = -1f;
        public float impulsoEfetivo => comb ? impulsoBase * System.Math.Max(0.05f, rendimento) : impulso;
        public int bearingsLog = -1;   // 0.15.0
    }

    /// <summary>Descobre os blocos do mod nas naves e mantem os joints de fisica deles.</summary>
    internal static partial class Juntas
    {
        // prefabs do mod: nome -> (tipo, curso do pistao em m | nivel do motor)
        static readonly (string nome, Tipo tipo, float curso)[] PREFABS = {
            ("SC_Pistao2", Tipo.Pistao, 2f), ("SC_Pistao4", Tipo.Pistao, 4f), ("SC_Pistao8", Tipo.Pistao, 8f),
            ("SC_Bearing", Tipo.Bearing, 0f),
            ("SC_MotorP", Tipo.Motor, 0f), ("SC_MotorM", Tipo.Motor, 1f), ("SC_MotorG", Tipo.Motor, 2f),
            ("SC_MotorCombP", Tipo.Motor, 3f), ("SC_MotorCombM", Tipo.Motor, 4f), ("SC_MotorCombG", Tipo.Motor, 5f),
            ("SC_Recoupler", Tipo.Recoupler, 0f), ("SC_Beacon", Tipo.Beacon, 0f), ("SC_Pedal", Tipo.Alavancas, 0f), ("SC_Volante", Tipo.Alavancas, 0f),
            ("SC_TurretSeat", Tipo.Torreta, 0f),
            ("SC_DriveShaft", Tipo.Transmissao, 0f), ("SC_Differential", Tipo.Transmissao, 0f), ("SC_ShaftReverser", Tipo.Transmissao, 0f), ("SC_ShaftTransmitter", Tipo.Transmissao, 0f),
            ("SC_SingleCellBattery", Tipo.Transmissao, 0f), ("SC_MiniElectricThruster", Tipo.Transmissao, 0f), ("SC_LongRangeTransmitter", Tipo.Radio, 0f), ("SC_SolarPanelMini", Tipo.Transmissao, 0f) };   // 0.19.0: painel solar mini (so o jogo)   // 0.15.0 / 0.16.0 / 0.18.0 (bateria e mini propulsor: so o jogo)
        /// <summary>0.12.15: nome (com ou sem " M") e' um bloco deste mod?</summary>
        public static bool EhDoMod(string nome)
        {
            if (nome == null) return false; if (nome.EndsWith(" M")) nome = nome.Substring(0, nome.Length - 2);
            foreach (var p in PREFABS) if (p.nome == nome) return true;
            return nome.StartsWith("SC_Roda") || nome.StartsWith("SC_Tanque");
        }
        static readonly Dictionary<ulong, int> porHash = new Dictionary<ulong, int>();
        static readonly HashSet<ulong> hashEspelho = new HashSet<ulong>();
        static bool hashesOk = false;
        static readonly Dictionary<long, Junta> juntas = new Dictionary<long, Junta>();
        static readonly Dictionary<long, Motor> motores = new Dictionary<long, Motor>();
        /// <summary>0.14.0: cada motor tem ForcaReserva x a forca listada, dividida entre os bearings que ele move (0.15.0: pelos Drive Shafts, Eixos.cs).</summary>
        public static float ForcaReserva = 4f;
        static readonly Dictionary<long, Motor> motorPorSaida = new Dictionary<long, Motor>();   // porta de saida -> motor
        static readonly HashSet<long> ignorados = new HashSet<long>(); static readonly HashSet<ulong> hashesVistos = new HashSet<ulong>();
        static float tDescobrir = 9f;

        // config (preenchida pelo plugin)
        public static float PistaoVel = 1f, PistaoImpulso = 20000f, PistaoConsumoPorM = 2.5f;
        public static float[] MotorVel = { 5f, 8f, 12f }, MotorImpulso = { 1000f, 4000f, 13000f }, MotorConsumo = { 7f, 28f, 70f };
        public static float[] MotorCombVel = { 5f, 8f, 12f }, MotorCombImpulso = { 1500f, 6000f, 19500f };
        public static float CombConsumo = 0.85f, CombEnergia = 5f;
        public static float FreioFator = 3f;   // 0.11.0: impulso do freio = impulso do motor x fator
        public static float MotorRampa = 1f, ServoImpulso = 3000f, ServoGraus = 45f, BearingApoio = 1f, ServoVel = 3f; public static bool ServoInverter = false;
        const float ServoGanho = 8f;   // rad/s de velocidade pedida por rad de erro
        public static int IteracoesSolver = 8;
        public static float ReforcoBase = 20f, PartidaTempo = 1f, PartidaPausa = 0.3f, PistaoGuia = 0.5f; public const float ReforcoTeto = 1e7f;
        public const float ConsumoOcioso = 0.05f;   // P/s parado: mantem o estado de energia do circuito valido
        public static bool LogDetalhe = true;

        /// <summary>0.19.0 (crash ao carregar outro BP, 01/10: o log parava em "paint: ..." depois de fechar o menu de BP): nos primeiros
        /// ciclos depois do menu de BP fechar, cada etapa vai pro log (o log por sessao do Replay grava linha a linha — se o jogo cair,
        /// a ultima linha diz em qual etapa).</summary>
        public static int Trilha;
        public static void T(string etapa) { if (Trilha > 0) Registro.Info("after BP load (" + Trilha + "): " + etapa); }
        public static void Passo(EntityManager em, float dt)
        {
            if (!Ecs.Preparar(em)) return;
            T("cycle start");
            VigiarTick(em);
            if (RewindPedido) { RewindPedido = false; try { Rebobinar(em); RebobinarTorres(em); } catch (Exception ex) { Registro.Erro("time rewind: " + ex); } }
            Bonus.Passo(em, dt); T("bonus");
            Solver(em, dt); T("solver");
            if (!hashesOk)
            {
                hashesOk = true;
                for (int i = 0; i < PREFABS.Length; i++)
                {
                    ulong h = Ecs.HashPrefab(PREFABS[i].nome); if (h != 0) porHash[h] = i;
                    ulong hm = Ecs.HashPrefab(PREFABS[i].nome + " M"); if (hm != 0 && hm != h) { porHash[hm] = i; hashEspelho.Add(hm); }   // 0.12.14: versao espelhada
                    Registro.Info("prefab " + PREFABS[i].nome + " hash=" + h + " (M " + hm + ")");
                }
            }
            tDescobrir += dt;
            if (tDescobrir >= 0.5f) { tDescobrir = 0f; T("discover..."); Descobrir(em); T("discover done"); }
            try { Pintura.Passo(em, dt); } catch (Exception ex) { AvisoEixo("paint: " + ex.Message); }   // 0.17.1
            T("paint done");
            try { Radio.Passo(em); } catch (Exception ex) { AvisoEixo("long range: " + ex.Message); }   // 0.18.0
            T("long range done");

            // motores primeiro (os bearings leem o estado deles)
            var mortos = new List<long>();
            foreach (var kv in motores)
            {
                var m = kv.Value;
                try { if (!Ecs.Vivo(em, m.bloco)) { mortos.Add(kv.Key); continue; } PassoMotor(em, m); }
                catch (Exception ex) { if (m.avisos++ < 5) Registro.Aviso(m.nome + " " + m.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortos) motores.Remove(k);
            if (mortos.Count > 0) RefazerMapaSaidas();
            T("motors done");
            try { MapearEixos(em, dt); } catch (Exception ex) { AvisoEixo("map: " + ex.Message); }
            T("shafts done");   // 0.15.0: quem move quem pelos Drive Shafts

            var mortas = new List<long>();
            foreach (var kv in juntas)
            {
                var j = kv.Value;
                try
                {
                    if (!Ecs.Vivo(em, j.bloco)) { Desfazer(em, j, "block is gone"); mortas.Add(kv.Key); continue; }
                    j.tEstado += dt;
                    try { Knobs.Passo(em, j, dt); } catch (Exception ex) { if (j.avisos++ < 2) Registro.Aviso(j.nome + " knob: " + ex.Message); }   // 0.19.2
                    // 0.19.6 (Andre: "nao recolheu o pistao quando acabou a simulacao"): o jogo refaz as entities da garagem depois do
                    // recolhimento; enquanto o pistao esta fora da simulacao, reconfere a cada 0,5 s
                    if (j.tipo == Tipo.Pistao && j.estado == 0) { j.tRecolher += dt; if (j.tRecolher >= 0.5f) { j.tRecolher = 0f; RecolherNaGaragem(em, j); } }
                    if (j.tipo == Tipo.Pistao) LedsPistao(em, j, dt);
                    switch (j.estado)
                    {
                        case 0: EsperandoTopo(em, j); break;
                        case 1: Separando(em, j); break;
                        case 2: if (j.tipo == Tipo.Bearing) AtivoBearing(em, j, dt); else AtivoPistao(em, j, dt); break;
                    }
                }
                catch (Exception ex) { if (j.avisos++ < 5) Registro.Aviso(j.nome + " " + j.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortas) juntas.Remove(k);
            tReplano += dt; if (tReplano >= 1f) { tReplano = 0f; try { ReplanejarLivres(em); } catch (Exception ex) { Registro.Aviso("replanning rings: " + ex.Message); } }
            T("joints done");
            Fila(em, dt); T("queue");
            Acoplador.Passo(em, dt); T("recoupler/beacon");
            Alavancas.Passo(em, dt); T("levers");
            PassoTorres(em, dt); T("turrets");
        }

        // PhysicsStep.SolverIterationCount (+20): o jogo usa o padrao (4). Com mais iteracoes os joints cedem bem menos sob
        // peso (roda Offroad grande). So sobe (nunca abaixa) e so enquanto tem joint nosso ativo; confere a cada 2 s.
        static TypeIndex tiStep; static EntityQuery qStep; static bool stepOk; static float tStep = 9f; static int stepLogado = -1;
        static unsafe void Solver(EntityManager em, float dt)
        {
            if (IteracoesSolver <= 0) return;
            tStep += dt; if (tStep < 2f) return; tStep = 0f;
            try
            {
                bool temJoint = false; foreach (var j in juntas.Values) if (j.estado == 2) { temJoint = true; break; }
                if (!temJoint) return;
                if (!stepOk) { tiStep = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<PhysicsStep>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiStep); qStep = em.CreateEntityQuery(a); stepOk = true; }
                foreach (var e in Ecs.Lista(qStep))
                {
                    var ps = (Ecs.TemC(em, e, tiStep) ? (PhysicsStep*)em.GetComponentDataRawRW(e, tiStep) : null); if (ps == null) continue;
                    if (ps->SolverIterationCount < IteracoesSolver)
                    {
                        int antes = ps->SolverIterationCount; ps->SolverIterationCount = IteracoesSolver;
                        if (stepLogado != antes) { stepLogado = antes; Registro.Info("physics: solver iterations " + antes + " -> " + IteracoesSolver + " (stiffer joints)"); }
                    }
                }
            }
            catch (Exception ex) { IteracoesSolver = 0; Registro.Aviso("physics: could not change PhysicsStep: " + ex.Message); }
        }

        static void Descobrir(EntityManager em)
        {
            bool novoMotor = false;
            foreach (var e in Ecs.Componentes(em))
            {
                long key = Ecs.K(e);
                if (juntas.ContainsKey(key) || motores.ContainsKey(key) || Acoplador.Tem(key) || Alavancas.Tem(key) || TemTorre(key) || Radio.Tem(key) || ignorados.Contains(key)) continue;   // 0.12.3: TemTorre (a torre era recriada a cada varredura: a mira voltava pro inicio)
                ulong h = Ecs.Prefab(em, e);
                if (!porHash.TryGetValue(h, out var i)) { ignorados.Add(key); continue; }
                bool esp = hashEspelho.Contains(h); string nomeP = PREFABS[i].nome + (esp ? " M" : "");
                if (PREFABS[i].tipo != Tipo.Motor && Ecs.EhDecouplerDoJogo(em, e)) { ignorados.Add(key); continue; }
                if (PREFABS[i].tipo == Tipo.Transmissao) { ignorados.Add(key); continue; }   // 0.15.0: so lidos pelo MapearEixos
                if (PREFABS[i].tipo == Tipo.Radio) { Radio.Novo(e); continue; }   // 0.18.0
                if (PREFABS[i].tipo == Tipo.Recoupler) { Acoplador.NovoRecoupler(e); continue; }
                if (PREFABS[i].tipo == Tipo.Beacon) { Acoplador.NovoBeacon(e); continue; }
                if (PREFABS[i].tipo == Tipo.Alavancas) { Alavancas.Novo(e, PREFABS[i].nome); continue; }
                if (PREFABS[i].tipo == Tipo.Torreta) { NovaTorre(e, PREFABS[i].nome); continue; }   // 0.12.0: turret seat (0.12.2: sem gatilhos)
                if (PREFABS[i].tipo == Tipo.Motor)
                {
                    int n = (int)PREFABS[i].curso; bool comb = n >= 3; n %= 3;
                    motores[key] = comb ? new Motor { bloco = e, nome = nomeP, comb = true, velMax = MotorCombVel[n], impulso = MotorCombImpulso[n], impulsoBase = MotorCombImpulso[n], consumoMax = CombEnergia }
                                        : new Motor { bloco = e, nome = nomeP, velMax = MotorVel[n], impulso = MotorImpulso[n], consumoMax = MotorConsumo[n] };
                    novoMotor = true;
                    Registro.Info(nomeP + " found: block " + e.Index + ":" + e.Version);
                    continue;
                }
                var j = new Junta { bloco = e, tipo = PREFABS[i].tipo, curso = PREFABS[i].curso, nome = nomeP, estado = 0, espelho = esp };
                juntas[key] = j;
                Registro.Info(j.nome + " found: block " + e.Index + ":" + e.Version);
                if (j.tipo == Tipo.Pistao) RecolherNaGaragem(em, j);   // 0.19.5 (Andre: "pistao estendido fora da simulacao")
            }
            if (novoMotor) RefazerMapaSaidas();
        }

        static void RefazerMapaSaidas() { motorPorSaida.Clear(); foreach (var m in motores.Values) if (!Ecs.Nulo(m.saida)) motorPorSaida[Ecs.K(m.saida)] = m; }

        // ================================================================ motor
        static void PassoMotor(EntityManager em, Motor m)
        {
            if (!m.portasOk)
            {
                m.entrada = Ecs.PortaDoTipo(em, m.bloco, Ecs.PortaEntrada); m.saida = Ecs.PortaDoTipo(em, m.bloco, PortaEixo);   // 0.15.0: saida de Drive Shaft (porta plasma) m.energia = Ecs.PortaDoTipo(em, m.bloco, Ecs.PortaEnergia);
                if (Ecs.Nulo(m.entrada) || Ecs.Nulo(m.saida)) { if (m.avisos++ < 2) Registro.Aviso(m.nome + " " + m.bloco.Index + ": ports not found (" + Ecs.Portas(em, m.bloco).Length + " in buffer)"); return; }
                m.portasOk = true; RefazerMapaSaidas();
                // 0.11.0: freio = a entrada com o maior _index (4a porta do prefab); a entrada de sinal = a de menor _index
                Entity ent = Entity.Null, fre = Entity.Null; int iEnt = 999, iFre = -1; var desc = new System.Text.StringBuilder();
                foreach (var p in Ecs.Portas(em, m.bloco))
                {
                    if (!Ecs.Porta(em, p, out var t, out _, out _)) continue;
                    int ix = Ecs.PortaIndice(em, p); desc.Append(' ').Append(p.Index).Append("(t").Append(t).Append(" i").Append(ix).Append(')');
                    if (t != Ecs.PortaEntrada) continue;
                    if (ix < iEnt) { iEnt = ix; ent = p; }
                    if (ix > iFre) { iFre = ix; fre = p; }
                }
                if (!Ecs.Nulo(ent) && !Ecs.Nulo(fre) && (fre.Index != ent.Index || fre.Version != ent.Version)) { m.entrada = ent; m.freioPorta = fre; }
                if (LogDetalhe) Registro.Info(m.nome + " " + m.bloco.Index + ": ports input=" + m.entrada.Index + " power=" + m.energia.Index + " output=" + m.saida.Index + (Ecs.Nulo(m.freioPorta) ? " (no brake port)" : " brake=" + m.freioPorta.Index) + " |" + desc);
            }
            if (!Ecs.EhPart(em, Ecs.Pai(em, m.bloco))) return;   // garagem
            float s = 0f;
            if (Ecs.LerEntrada(em, m.entrada, out var v, out var fonte, out var trilha)) s = System.Math.Max(-1f, System.Math.Min(1f, v));
            if (LogDetalhe && trilha != m.trilha) { m.trilha = trilha; Registro.Info(m.nome + " " + m.bloco.Index + ": input" + trilha); }
            // 0.11.0: freio (0..1, so o lado positivo): com freio o motor nao acelera nem gasta (os bearings travam a roda)
            float fr = 0f;
            if (!Ecs.Nulo(m.freioPorta) && Ecs.LerEntrada(em, m.freioPorta, out var vf, out _, out _)) fr = System.Math.Max(0f, System.Math.Min(1f, vf));
            if ((fr > 0.02f) != m.freioLog) { m.freioLog = fr > 0.02f; Registro.Info(m.nome + " " + m.bloco.Index + ": brake " + (m.freioLog ? "ENGAGED (" + fr.ToString("F2") + ")" : "released")); }
            m.freio = fr;
            if (fr > 0.02f) s = 0f;
            if (LogDetalhe && System.Math.Abs(s - m.sinalLog) >= 0.1f) { m.sinalLog = s; Registro.Info(m.nome + " " + m.bloco.Index + ": signal=" + s.ToString("F2") + (m.alimentado ? "" : " (no power)")); }
            m.alimentado = Ecs.TemEnergia(em, m.bloco, out var estado);
            if (m.comb) s = Combustivel(em, m, s, UnityEngine.Time.deltaTime);   // sinal efetivo = o que o combustivel deu
            m.sinal = s;
            Ecs.Consumo(em, m.bloco, System.Math.Max(ConsumoOcioso, System.Math.Abs(s) * m.consumoMax));
            // 0.15.0: a saida e' de eixo (plasma): o mod leva o sinal pelos Drive Shafts, nao escreve valor na porta
            if (LogDetalhe && estado != m.estado) { m.estado = estado; Registro.Info(m.nome + " " + m.bloco.Index + ": power " + NomeEstado(estado)); }
        }
        /// <summary>Motor a combustivel (igual o SCTick_Thruster_Fuel do jogo): quer CombConsumo x |sinal| x dt do FuelData do
        /// bloco; tira o que tiver (as 3 partes da mistura na mesma proporcao); uso01 = o que pegou / o que gastaria com sinal 1;
        /// rendimento = FuelProperties.GetFuelPerformance(o que pegou, uso01, FuelComboSingleton). Sinal efetivo = sinal limitado
        /// ao uso01 (sem combustivel = 0, freia); forca = impulso x rendimento (arredondado de 5 em 5% pra nao refazer o joint
        /// todo frame). Sem sinal nao gasta nada e segura com a forca do ultimo rendimento.</summary>
        static TypeIndex tiFuel, tiCombo; static bool fuelOk, perfFalhou; static EntityQuery qCombo;
        static unsafe float Combustivel(EntityManager em, Motor m, float s, float dt)
        {
            try
            {
                if (!fuelOk)
                {
                    fuelOk = true;
                    tiFuel = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<FuelData>());
                    tiCombo = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<FuelComboSingleton>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiCombo); qCombo = em.CreateEntityQuery(a);
                }
                if (!em.HasComponent(m.bloco, ComponentType.ReadOnly(tiFuel))) { if (m.combLog != "no FuelData") { m.combLog = "no FuelData"; Registro.Aviso(m.nome + " " + m.bloco.Index + ": block has no FuelData (the game didn't connect it to the pipes?)"); } return 0f; }
                var fd = (Ecs.TemC(em, m.bloco, tiFuel) ? (byte*)em.GetComponentDataRawRW(m.bloco, tiFuel) : null);
                double* cur = (double*)(fd + 24);
                double soma = System.Math.Max(0.0, cur[0]) + System.Math.Max(0.0, cur[1]) + System.Math.Max(0.0, cur[2]);
                m.combTem = (float)soma;
                float abs = System.Math.Abs(s);
                if (abs < 1e-4f || dt <= 0f) { LogComb(m, soma); return 0f; }
                double quer = CombConsumo * abs * dt, pega = System.Math.Min(quer, soma);
                float uso = CombConsumo <= 0f ? abs : (float)(pega / (CombConsumo * dt));
                if (pega > 0.0 && soma > 0.0)
                {
                    // Fuel do jogo = 3 doubles (24 B), mas o proxy do Il2CppInterop so tem 8 B (buffer fixo vira 1 campo): monto
                    // os 24 B na mao e chamo o metodo do jogo direto (il2cpp_runtime_invoke) com ponteiro pra eles
                    double* t = stackalloc double[3]; double f = pega / soma;
                    for (int i = 0; i < 3; i++) { double v = System.Math.Max(0.0, cur[i]); t[i] = v * f; cur[i] = v - t[i]; }
                    float perf = 1f;
                    if (!perfFalhou)
                    {
                        try
                        {
                            var lista = Ecs.Lista(qCombo);
                            if (lista.Count > 0) { var combo = *(Ecs.TemC(em, lista[0], tiCombo) ? (FuelComboSingleton*)em.GetComponentDataRawRO(lista[0], tiCombo) : null); perf = Rendimento(t, System.Math.Min(1f, uso), &combo); }
                        }
                        catch (Exception ex) { perfFalhou = true; Registro.Aviso("fuel motor: the game's efficiency calculation failed (using 100%): " + ex.Message); }
                    }
                    if (float.IsNaN(perf) || perf < 0f) perf = 0f;
                    m.rendimento = (float)System.Math.Round(System.Math.Min(2f, perf) * 20f) / 20f;
                }
                m.uso = uso;
                LogComb(m, soma - pega);
                return System.Math.Sign(s) * System.Math.Min(abs, uso);
            }
            catch (Exception ex) { if (m.avisos++ < 5) Registro.Aviso(m.nome + " " + m.bloco.Index + ": fuel: " + ex.Message); return 0f; }
        }
        static IntPtr miPerf;
        static unsafe float Rendimento(double* fuel24, float uso01, FuelComboSingleton* combo)
        {
            if (miPerf == IntPtr.Zero)
            {
                var fi = typeof(FuelProperties).GetField("NativeMethodInfoPtr_GetFuelPerformance_Public_Static_Single_Fuel_Single_byref_FuelComboSingleton_0", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (fi == null) throw new Exception("GetFuelPerformance method not found in the proxy");
                miPerf = (IntPtr)fi.GetValue(null);
                if (miPerf == IntPtr.Zero) throw new Exception("GetFuelPerformance has no pointer");
            }
            float u = uso01;
            IntPtr* args = stackalloc IntPtr[3];
            args[0] = (IntPtr)fuel24; args[1] = (IntPtr)(&u); args[2] = (IntPtr)combo;
            IntPtr exc = IntPtr.Zero;
            IntPtr r = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(miPerf, IntPtr.Zero, (void**)args, ref exc);
            if (exc != IntPtr.Zero || r == IntPtr.Zero) throw new Exception("exception in GetFuelPerformance");
            return *(float*)Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(r);
        }

        static void LogComb(Motor m, double sobra)
        {
            if (!LogDetalhe) return;
            string st = sobra <= 1e-6 ? "out of fuel" : "has fuel";
            if (st != m.combLog) { m.combLog = st; Registro.Info(m.nome + " " + m.bloco.Index + ": " + st + (sobra > 1e-6 ? " (" + sobra.ToString("F2") + " in the motor)" : " — connect it to a tank with a pipe")); }
            if (System.Math.Abs(m.rendimento - m.rendLog) >= 0.1f) { m.rendLog = m.rendimento; Registro.Info(m.nome + " " + m.bloco.Index + ": mixture efficiency " + (m.rendimento * 100f).ToString("F0") + "% -> force " + m.impulsoEfetivo.ToString("F0")); }
        }

        public static string NomeEstado(byte e) => e == 0 ? "no power" : e == 1 ? "insufficient power" : e == 2 ? "powered" : e == 255 ? "no SCElectricPower" : "?" + e;

        // ================================================================ pistao e bearing: a face de cima solta (igual o decoupler)
        static unsafe void EsperandoTopo(EntityManager em, Junta j)
        {
            var pa = Ecs.Pai(em, j.bloco);
            if (!Ecs.EhPart(em, pa)) return;   // ainda na garagem
            if (j.tEstado < 0f) return;         // espera depois de uma falha
            Ecs.Eixos(em, j.bloco, out var ex, out var ey, out var ez);
            var pb = Ecs.Posicao(em, j.bloco);
            j.eixo = ey; j.ancora = Ecs.Add(pb, Ecs.Mul(ey, 0.25f));
            var joints = Ecs.Joints(em, out int n);
            if (joints == null) { if (j.avisos++ < 3) Registro.Aviso("SpaceshipSingleton._joints not available"); j.tEstado = -1f; return; }
            int quebrados = 0; Entity viz = Entity.Null; float melhor = 99f;
            j.quebrados.Clear();
            var meus = JointsDeCima(em, j.bloco, joints, n, out viz);
            if (meus.Count == 0 || Ecs.Nulo(viz))
            {
                if (j.avisos++ < 3) Registro.Aviso(j.nome + " " + j.bloco.Index + ": nothing attached to the top face — nothing to do (retrying in 30 s). Its joints:" + ListarJoints(em, j, joints, n, pb, ey));
                j.tEstado = -30f; return;
            }
            // 0.11.1: se o bloco de cima continua ligado a base por OUTRO caminho (peca colada dos dois lados, ou a estrutura
            // fecha um anel em volta do pistao), quebrar os joints nao separa nada — a nave nao vira duas parts, e cada
            // tentativa (a cada 5 s) fazia o jogo refazer as parts inteiras, bagunçando os outros pistoes/bearings. Os joints
            // das faces de cima de TODAS as articulacoes do mod nesta part contam como "vao quebrar" (pistoes lado a lado).
            var vaoQuebrar = new HashSet<int>(meus);
            foreach (var o in juntas.Values)
                if (o != j && o.estado == 0 && Ecs.Vivo(em, o.bloco) && Igual(Ecs.Pai(em, o.bloco), pa))
                    foreach (var k in JointsDeCima(em, o.bloco, joints, n, out _)) vaoQuebrar.Add(k);
            if (LigadoPorOutroCaminho(em, joints, n, viz, j.bloco, vaoQuebrar, out int viaN))
            {
                j.soldado = true;
                if (!j.avisouPreso) { j.avisouPreso = true; Registro.Aviso(j.nome + " " + j.bloco.Index + ": the top block (" + viz.Index + ") is still attached to the base through another path (" + viaN + " joints on the path) — not separating. Detach the top part from the rest of the structure (only the " + (j.tipo == Tipo.Pistao ? "piston" : "bearing") + " may connect the two sides). Retrying in 30 s."); }
                j.tEstado = -30f; return;
            }
            // 0.11.2: sem ANEL de articulacoes. Andre lancou 4 pistoes ligando a mesma base ao mesmo topo por dois caminhos
            // (base -> P1 -> A -> P2 -> topo <- P3 <- B <- P4 <- base): a fisica nao fecha o anel (cada pistao puxa pro seu
            // lado), em 2 s os encaixes estavam a metros/km de distancia e o jogo refez as parts 6 vezes. Regra: as articulacoes
            // do mod formam uma ARVORE — quem fecharia um anel fica soldada (nao vira junta) e avisa.
            PlanejarAneis(em, joints, n);
            j.guia = viraGuia.Contains(Ecs.K(j.bloco));
            if (j.guia && !j.avisouPreso) Registro.Info(j.nome + " " + j.bloco.Index + ": closes a RING of joints — " + (j.tipo == Tipo.Pistao ? "keeps pushing with its own signal, with rails (yellow LED)" : "stays FREE (only spins)"));
            if (fechaAnel.Contains(Ecs.K(j.bloco)))
            {
                j.soldado = true;
                if (!j.avisouPreso) { j.avisouPreso = true; Registro.Aviso(j.nome + " " + j.bloco.Index + ": this " + (j.tipo == Tipo.Pistao ? "piston" : "bearing") + " would close a RING of joints (the top block " + viz.Index + " is already connected to the base by other pistons/bearings) — stays WELDED, it won't become a joint. The mod only links parts as a tree: each part attached to the base by ONE path of joints. Retrying in 30 s."); }
                j.tEstado = -30f; return;
            }
            j.avisouPreso = false; j.soldado = false;
            foreach (var k in meus) { Quebrar(j, &joints[k], k); quebrados++; }
            j.vizinho = viz; j.partA = pa; j.estado = 1; j.tEstado = 0f;
            Registro.Info(j.nome + " " + j.bloco.Index + ": separating (" + quebrados + " joint(s), neighbor " + viz.Index + ")");
        }

        // ---- 0.12.2: com a nave andando os aneis abrem e fecham (recoupler pega/solta o carro): 1x por segundo refaz quem fica livre.
        // Grafo de PARTS: recouplers travados e turrets primeiro, depois as articulacoes ativas (as que ja empurram primeiro, depois por
        // indice); a que liga duas parts ja ligadas fecha um anel -> livre. Quem mudou tem o joint refeito na hora, na pose atual.
        static float tReplano;
        static readonly Dictionary<long, long> ufParte = new Dictionary<long, long>();
        static long RaizP(long k) { while (ufParte.TryGetValue(k, out var p) && p != k) k = p; return k; }
        static bool UnirP(long a, long b) { a = RaizP(a); b = RaizP(b); if (!ufParte.ContainsKey(a)) ufParte[a] = a; if (!ufParte.ContainsKey(b)) ufParte[b] = b; if (a == b) return false; ufParte[a] = b; return true; }
        static void ReplanejarLivres(EntityManager em)
        {
            ufParte.Clear();
            foreach (var r in Acoplador.Travados()) if (!Ecs.Nulo(r.partA) && !Ecs.Nulo(r.partB)) UnirP(Ecs.K(r.partA), Ecs.K(r.partB));
            foreach (var (ta, tb) in LigacoesTorres()) UnirP(Ecs.K(ta), Ecs.K(tb));
            var ativas = new List<Junta>();
            foreach (var j in juntas.Values) if (j.estado == 2 && (j.tipo == Tipo.Pistao || j.tipo == Tipo.Bearing) && !Ecs.Nulo(j.partA) && !Ecs.Nulo(j.partB) && j.tPartida <= 0f) ativas.Add(j);
            ativas.Sort((a, b) => a.guia != b.guia ? (a.guia ? 1 : -1) : a.bloco.Index.CompareTo(b.bloco.Index));
            foreach (var j in ativas)
            {
                bool livre = !UnirP(Ecs.K(j.partA), Ecs.K(j.partB));
                if (livre == j.guia) continue;
                j.guia = livre;
                Registro.Info(j.nome + " " + j.bloco.Index + ": " + (j.tipo == Tipo.Pistao ? (livre ? "the ring closed (yellow LED, keeps pushing)" : "the ring opened (green LED)") : (livre ? "the ring closed — stays FREE" : "the ring opened — pushing again")));
                if (j.tipo != Tipo.Pistao) RefazerJoint(em, j);   // 0.12.3: pistao do anel so troca o led
            }
        }
        static void RefazerJoint(EntityManager em, Junta j)
        {
            try { if (Ecs.Vivo(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
            try { if (Ecs.Vivo(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
            try { if (Ecs.Vivo(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
            j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; ApagarGuias(em, j);
            if (j.tipo == Tipo.Bearing) { j.modo = -1; j.impulsoModo = 0f; }
            adiado = false;
            if (!Criar(em, j, true)) { Desfazer(em, j, "rebuilding the joint (ring) failed"); j.estado = 0; j.tEstado = -1f; }
        }

        // ---- 0.17.0: led vermelho do bearing (paineis +Z/-Z da base, x = +-0,12): escondido 2 cm pra dentro; sai quando a rede de eixo
        // dele esta TRAVADA (sinais opostos)
        const float BLedX = 0.12f, BLedY = -0.16f, BLedRepZ = 0.224f, BLedSai = 0.023f;
        static unsafe void LedBearing(EntityManager em, Junta j, bool aceso, float dt)
        {
            try
            {
                j.tLeds += dt;
                if (j.leds == null || j.tLeds >= 5f)
                {
                    j.tLeds = 0f; var l = new List<Entity>(); var r = new List<float3>();
                    foreach (var f in Ecs.Filhos(em, j.bloco))
                    {
                        var p = Ecs.LT(em, f).Position;
                        if (System.Math.Abs(System.Math.Abs(p.x) - BLedX) > 0.003f || System.Math.Abs(p.y - BLedY) > 0.003f) continue;
                        float az = System.Math.Abs(p.z);
                        if (System.Math.Abs(az - BLedRepZ) > 0.003f && System.Math.Abs(az - BLedRepZ - BLedSai) > 0.003f) continue;
                        l.Add(f); r.Add(new float3 { x = p.x, y = p.y, z = p.z < 0f ? -BLedRepZ : BLedRepZ });
                    }
                    bool primeira = j.leds == null; j.leds = l.ToArray(); j.ledRepouso = r.ToArray();
                    if (primeira) { j.ledEstado = -2; if (l.Count == 0 && aceso) Registro.Aviso(j.nome + " " + j.bloco.Index + ": bearing LEDs not found (rebuild the bundle in the Modkit)"); }
                }
                int est = aceso ? 1 : 0; if (est == j.ledEstado) return;
                j.ledEstado = est;
                for (int i = 0; i < j.leds.Length; i++)
                {
                    var e = j.leds[i]; if (!Ecs.Vivo(em, e)) continue;
                    var lt = (Ecs.TemC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) continue;
                    var p = j.ledRepouso[i];
                    lt->Position = new float3 { x = p.x, y = p.y, z = aceso ? p.z + (p.z < 0f ? -BLedSai : BLedSai) : p.z };
                }
            }
            catch (Exception ex) { if (j.avisos++ < 3) Registro.Aviso(j.nome + " " + j.bloco.Index + ": LED: " + ex.Message); }
        }

        // ---- 0.12.1: leds de estado do pistao (renderers extras escondidos no painel +Z; o do estado sai 2,3 cm)
        const float LedY = -0.135f, LedZ = 0.224f, LedSai = 0.023f;
        static readonly float[] LedXs = { -0.09f, 0f, 0.09f };   // verde, amarelo, vermelho
        static unsafe void LedsPistao(EntityManager em, Junta j, float dt)
        {
            try
            {
                bool naNave = Ecs.EhPart(em, Ecs.Pai(em, j.bloco));
                int est = !naNave ? -1 : j.estado == 2 ? (j.guia ? 1 : 0) : j.soldado ? 2 : -1;
                j.tLeds += dt;
                if (j.leds == null || j.tLeds >= 5f)
                {
                    j.tLeds = 0f; var l = new Entity[3]; int achou = 0;
                    foreach (var f in Ecs.Filhos(em, j.bloco))
                    {
                        var p = Ecs.LT(em, f).Position;
                        if (System.Math.Abs(p.y - LedY) > 0.003f || (System.Math.Abs(p.z - LedZ) > 0.003f && System.Math.Abs(p.z - LedZ - LedSai) > 0.003f)) continue;
                        for (int i = 0; i < 3; i++) if (System.Math.Abs(p.x - LedXs[i]) < 0.003f) { l[i] = f; achou++; }
                    }
                    bool primeira = j.leds == null; j.leds = l;
                    if (primeira && achou < 3 && naNave) Registro.Aviso(j.nome + " " + j.bloco.Index + ": piston LEDs not found (" + achou + "/3) — rebuild the bundle in the Modkit");
                    if (primeira) j.ledEstado = -2;
                }
                if (est == j.ledEstado) return;
                j.ledEstado = est;
                for (int i = 0; i < 3; i++)
                {
                    var e = j.leds[i]; if (Ecs.Nulo(e) || !Ecs.Vivo(em, e)) continue;
                    var lt = (Ecs.TemC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) continue;
                    lt->Position = new float3 { x = LedXs[i], y = LedY, z = LedZ + (i == est ? LedSai : 0f) };
                }
                if (naNave) Registro.Info(j.nome + " " + j.bloco.Index + ": LED " + (est == 0 ? "GREEN (active)" : est == 1 ? "YELLOW (guided)" : est == 2 ? "RED (welded)" : "off"));
            }
            catch (Exception ex) { if (j.avisos++ < 3) Registro.Aviso(j.nome + " " + j.bloco.Index + ": LEDs: " + ex.Message); }
        }

        // ---- plano de aneis (0.11.2), uma vez por frame: grupos rigidos = blocos ligados pelos joints do jogo SEM os joints de
        // cima das articulacoes candidatas (estado 0, em voo); cada articulacao e' uma aresta entre o grupo do bloco dela e o
        // grupo do bloco de cima; Kruskal (as ja separadas primeiro, depois as candidatas por indice de entity): a candidata
        // cuja aresta liga dois grupos que JA estao ligados fecharia um anel -> fica soldada.
        static int frameAnel = -1; static readonly HashSet<long> fechaAnel = new HashSet<long>(), viraGuia = new HashSet<long>();
        static readonly Dictionary<long, long> ufBloco = new Dictionary<long, long>();
        static long RaizB(long k) { while (ufBloco.TryGetValue(k, out var p) && p != k) k = p; return k; }
        static void UnirB(long a, long b) { a = RaizB(a); b = RaizB(b); if (!ufBloco.ContainsKey(a)) ufBloco[a] = a; if (!ufBloco.ContainsKey(b)) ufBloco[b] = b; if (a != b) ufBloco[a] = b; }
        static unsafe void PlanejarAneis(EntityManager em, SpaceshipJoint* joints, int n)
        {
            if (frameAnel == UnityEngine.Time.frameCount) return; frameAnel = UnityEngine.Time.frameCount;
            fechaAnel.Clear(); viraGuia.Clear(); ufBloco.Clear();
            var cand = new List<(Junta j, Entity viz)>(); var pular = new HashSet<int>();
            var feitas = new List<Junta>();
            foreach (var o in juntas.Values)
            {
                if (!Ecs.Vivo(em, o.bloco) || !Ecs.EhPart(em, Ecs.Pai(em, o.bloco))) continue;
                if (o.estado == 0) { var ks = JointsDeCima(em, o.bloco, joints, n, out var v); if (ks.Count > 0 && !Ecs.Nulo(v)) { cand.Add((o, v)); foreach (var k in ks) pular.Add(k); } }
                else if (!Ecs.Nulo(o.vizinho) && Ecs.Vivo(em, o.vizinho)) feitas.Add(o);
            }
            if (cand.Count == 0) return;
            for (int k = 0; k < n; k++)
            {
                if (pular.Contains(k)) continue;
                var sj = &joints[k];
                if (sj->_breakForce < 0f) continue;   // ja quebrado por nos
                UnirB(Ecs.K(sj->_a), Ecs.K(sj->_b));
            }
            // 0.12.1: grupos rigidos ANTES das articulacoes (pra achar pistoes paralelos entre as mesmas duas pecas)
            var g0 = new Dictionary<long, (long a, long b)>();
            foreach (var o in feitas) g0[Ecs.K(o.bloco)] = (RaizB(Ecs.K(o.bloco)), RaizB(Ecs.K(o.vizinho)));
            foreach (var (o, v) in cand) g0[Ecs.K(o.bloco)] = (RaizB(Ecs.K(o.bloco)), RaizB(Ecs.K(v)));
            var aceitas = new List<Junta>();
            feitas.Sort((a, b) => a.bloco.Index.CompareTo(b.bloco.Index));
            cand.Sort((a, b) => a.j.bloco.Index.CompareTo(b.j.bloco.Index));
            foreach (var o in feitas) { UnirB(Ecs.K(o.bloco), Ecs.K(o.vizinho)); if (!o.guia) aceitas.Add(o); }
            foreach (var (o, v) in cand)
            {
                long ga = RaizB(Ecs.K(o.bloco)), gb = RaizB(Ecs.K(v));
                if (ga != gb) { UnirB(ga, gb); aceitas.Add(o); continue; }
                // fecharia um anel: se for um pistao PARALELO (mesmo sentido) a outro que liga as MESMAS duas pecas, vira guia
                bool guia = false;
                if (o.tipo == Tipo.Pistao && g0.TryGetValue(Ecs.K(o.bloco), out var go))
                {
                    Ecs.Eixos(em, o.bloco, out _, out var eyO, out _);
                    foreach (var a in aceitas)
                    {
                        if (a.tipo != Tipo.Pistao || !g0.TryGetValue(Ecs.K(a.bloco), out var ga0)) continue;
                        bool mesmas = (ga0.a == go.a && ga0.b == go.b);
                        if (!mesmas) continue;
                        Ecs.Eixos(em, a.bloco, out _, out var eyA, out _);
                        if (Ecs.Dot(eyO, eyA) > 0.98f && System.Math.Abs(a.curso - o.curso) < 0.01f) { guia = true; break; }
                    }
                }
                // 0.12.2 (Andre, guincho em anel): em vez de soldar, a articulacao que fecha o anel fica LIVRE — pistao vira guia (so desliza),
                // bearing fica solto (so gira). Os outros do anel empurram; nada briga e nada trava.
                viraGuia.Add(Ecs.K(o.bloco));
            }
        }

        /// <summary>Indices dos joints do jogo entre o bloco e o que esta colado na face de cima dele (+Y do bloco); viz = o
        /// bloco de cima mais perto.</summary>
        static unsafe List<int> JointsDeCima(EntityManager em, Entity bloco, SpaceshipJoint* joints, int n, out Entity viz)
        {
            var r = new List<int>(); viz = Entity.Null; float melhor = 99f;
            Ecs.Eixos(em, bloco, out _, out var ey, out _); var pb = Ecs.Posicao(em, bloco);
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k];
                bool a = Igual(sj->_a, bloco), b = Igual(sj->_b, bloco);
                if (!a && !b) continue;
                var outro = a ? sj->_b : sj->_a;
                float3 po; try { if (!Ecs.Vivo(em, outro)) continue; po = Ecs.Posicao(em, outro); } catch (Exception) { continue; }
                var d = Ecs.Sub(po, pb); float comp = Ecs.Comp(d); if (comp < 1e-4f) continue;
                if (Ecs.Dot(d, ey) / comp < 0.5f) continue;   // joint de outra face
                r.Add(k);
                if (comp < melhor) { melhor = comp; viz = outro; }
            }
            return r;
        }

        /// <summary>Busca em largura pelos joints do jogo (menos os que vao quebrar): da pra ir do bloco 'de' ate 'ate'?</summary>
        static unsafe bool LigadoPorOutroCaminho(EntityManager em, SpaceshipJoint* joints, int n, Entity de, Entity ate, HashSet<int> pular, out int passos)
        {
            passos = 0;
            var viz = new Dictionary<long, List<long>>();
            for (int k = 0; k < n; k++)
            {
                if (pular.Contains(k)) continue;
                var sj = &joints[k];
                if (sj->_breakForce < 0f) continue;   // ja quebrado (por nos)
                long ka = Ecs.K(sj->_a), kb = Ecs.K(sj->_b);
                if (!viz.TryGetValue(ka, out var la)) viz[ka] = la = new List<long>(); la.Add(kb);
                if (!viz.TryGetValue(kb, out var lb)) viz[kb] = lb = new List<long>(); lb.Add(ka);
            }
            long alvo = Ecs.K(ate); var fila = new Queue<(long, int)>(); var visto = new HashSet<long> { Ecs.K(de) };
            fila.Enqueue((Ecs.K(de), 0));
            while (fila.Count > 0)
            {
                var (cur, d) = fila.Dequeue();
                if (cur == alvo) { passos = d; return true; }
                if (!viz.TryGetValue(cur, out var ls)) continue;
                foreach (var nx in ls) if (visto.Add(nx)) fila.Enqueue((nx, d + 1));
            }
            return false;
        }

        static unsafe void AtivoPistao(EntityManager em, Junta j, float dt)
        {
            Vigiar(em, j);
            if (!Valido(em, j)) { if (Reconstruir(em, j)) return; Desfazer(em, j, "part/joint changed (time rewind, load or break)"); j.estado = 0; j.tEstado = -1f; return; }
            Alinhamento(em, j, dt);
            try { VisualPistao(em, j, dt); } catch (Exception ex) { if (j.avisos++ < 3) Registro.Aviso(j.nome + " " + j.bloco.Index + ": rod visual: " + ex.Message); }
            // 0.12.3 (Andre: "o amarelo ta fraco e nao atualiza junto"): o pistao que fecha o anel tambem empurra com o proprio sinal
            if (j.tPartida > 0f) return;   // partida suave: segura onde nasceu ate a fila liberar (0.7.12)
            Carga(em, j);
            float sinal = 0f; string motivo = null; bool tem = false;
            var pe = Ecs.PortaDoTipo(em, j.bloco, Ecs.PortaEntrada);
            if (Ecs.Nulo(pe)) motivo = "no input port";
            else { tem = Ecs.LerEntrada(em, pe, out sinal, out _, out var trilha); if (!tem) motivo = "nothing connected to the input"; if (LogDetalhe && trilha != j.origemModo) { j.origemModo = trilha; Registro.Info(j.nome + " " + j.bloco.Index + ": input" + trilha); } }
            LogSinal(em, j, tem, sinal, motivo, dt);
            bool energia = Ecs.TemEnergia(em, j.bloco, out var estado);
            if (LogDetalhe && estado != j.energiaEstado) { j.energiaEstado = estado; Registro.Info(j.nome + " " + j.bloco.Index + ": power " + NomeEstado(estado)); }
            float pedido = tem ? System.Math.Max(0f, System.Math.Min(1f, sinal)) * j.curso * j.fracaoCurso : j.alvoAtual;   // sem sinal: fica onde esta (0.19.2: x knob do curso)
            bool quer = System.Math.Abs(pedido - j.alvoAtual) > 1e-4f;
            Ecs.Consumo(em, j.bloco, quer ? PistaoConsumoPorM * j.curso : ConsumoOcioso);
            if (!quer || !energia) return;   // sem energia: segura onde esta
            float passo = PistaoVel * dt;
            float alvo = pedido > j.alvoAtual + passo ? j.alvoAtual + passo : pedido < j.alvoAtual - passo ? j.alvoAtual - passo : pedido;
            j.alvoAtual = alvo;
            var pj = (Ecs.TemC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null);
            Constraint* c = Restricao(pj, j.idxMotor);
            c->Target.x = -(alvo - j.cursoBase);   // motor de posicao: -x estica x a partir de onde os frames coincidem
            pj->m_Version++;
            if (j.idxExtra >= 0 && Ecs.Vivo(em, j.extra))
            {
                var px = (Ecs.TemC(em, j.extra, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.extra, Ecs.tiJoint) : null);
                Restricao(px, j.idxExtra)->Target.x = -(alvo - j.cursoBaseExtra);
                px->m_Version++;
            }
        }

        // 0.11.0: haste que estica e cabeca que sobe. No prefab a cabeca e a haste sao EPC_Renderers separados (filhos do bloco,
        // viram entities com LocalTransform relativo ao bloco): cabeca em (0, 0,225, 0); haste = cilindro de 1 m em (0, 0,105, 0)
        // com escala y 0,095 (PostTransformMatrix). Aqui: cabeca.y = 0,225 + curso, haste escala y = 0,095 + curso.
        const float CabecaY = 0.225f, HasteY = 0.105f, HasteRepouso = 0.095f;
        static unsafe void VisualPistao(EntityManager em, Junta j, float dt)
        {
            if (Ecs.Nulo(j.cabeca) || !Ecs.Vivo(em, j.cabeca) || Ecs.Nulo(j.haste) || !Ecs.Vivo(em, j.haste))
            {
                j.tVisual += dt;
                if (j.visualTentativas >= 5 || (j.visualTentativas > 0 && j.tVisual < 2f)) return;
                j.visualTentativas++; j.tVisual = 0f; j.cabeca = Entity.Null; j.haste = Entity.Null;
                var sb = new System.Text.StringBuilder();
                foreach (var f in Ecs.Filhos(em, j.bloco))
                {
                    var lt = Ecs.LT(em, f); var p = lt.Position;
                    if (LogDetalhe) sb.Append("\n    ").Append(f.Index).Append(" at (").Append(Ecs.F3(p)).Append(") scale ").Append(lt.Scale.ToString("F3")).Append(Ecs.TemPTM(em, f) ? " [PTM]" : "").Append(": ").Append(Ecs.Componentes(em, f));
                    if (System.Math.Abs(p.x) < 1e-3f && System.Math.Abs(p.z) < 1e-3f)
                    {
                        // 0.19.5: a cabeca pode estar levantada (ficou esticada de uma simulacao anterior): aceita qualquer altura acima do repouso
                        if (p.y >= CabecaY - 1e-3f && p.y <= CabecaY + j.curso + 0.6f && !Ecs.TemPTM(em, f)) j.cabeca = f;
                        else if (System.Math.Abs(p.y - HasteY) < 1e-3f) j.haste = f;
                    }
                }
                if (Ecs.Nulo(j.cabeca) || Ecs.Nulo(j.haste))
                {
                    if (j.visualTentativas == 1 || j.visualTentativas == 5) Registro.Aviso(j.nome + " " + j.bloco.Index + ": head/rod renderers not found (attempt " + j.visualTentativas + ") — block children:" + (sb.Length > 0 ? sb.ToString() : " none"));
                    return;
                }
                if (!Ecs.TemPTM(em, j.haste))
                {
                    Registro.Aviso(j.nome + " " + j.bloco.Index + ": the rod has no PostTransformMatrix — it can't be stretched (only the head moves). Children:" + sb);
                }
                else Registro.Info(j.nome + " " + j.bloco.Index + ": renderers found — head " + j.cabeca.Index + ", rod " + j.haste.Index + (LogDetalhe ? ". Block children:" + sb : ""));
                j.visualExt = -1f;
            }
            Encaixes(em, j, out var pA, out var eA, out _, out var pB, out _, out _);
            float ext = Ecs.Dot(Ecs.Sub(pB, pA), eA);
            if (ext < 0f) ext = 0f; if (ext > j.curso + 0.5f) ext = j.curso + 0.5f;
            if (System.Math.Abs(ext - j.visualExt) < 0.002f) return;
            j.visualExt = ext;
            var ltc = (Ecs.TemC(em, j.cabeca, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(j.cabeca, Ecs.tiLT) : null);
            if (ltc != null) { ltc->Position = new float3 { x = 0f, y = CabecaY + ext, z = 0f }; }
            if (Ecs.TemPTM(em, j.haste)) Ecs.EscalaY(em, j.haste, HasteRepouso + ext);
        }

        /// <summary>Se um Recoupler travou algo no topo deste pistao (o decoupler/recoupler do topo esta na partB dele), cria um
        /// segundo motor de posicao direto entre a base do pistao e o pedaco travado, na pose atual. Solta junto.</summary>
        static unsafe void Carga(EntityManager em, Junta j)
        {
            Entity alvoParte = Entity.Null;
            foreach (var r in Acoplador.Travados())
            {
                if (r.partB.Index == j.partB.Index && r.partA.Index != j.partA.Index) { alvoParte = r.partA; break; }
                if (r.partA.Index == j.partB.Index && r.partB.Index != j.partA.Index) { alvoParte = r.partB; break; }
            }
            if (Ecs.Nulo(alvoParte))
            {
                if (!Ecs.Nulo(j.extra)) { SoltarCarga(em, j, "recoupler released"); }
                return;
            }
            if (!Ecs.Nulo(j.extra) && j.extraParte.Index == alvoParte.Index && Ecs.Vivo(em, j.extra)) return;
            if (!Ecs.Nulo(j.extra)) SoltarCarga(em, j, "piece changed");
            try
            {
                Encaixes(em, j, out var pA, out var eA, out var qA, out var pB, out _, out _);
                var fa = Ecs.Frame(em, j.partA, pA, eA, qA); var fc = Ecs.Frame(em, alvoParte, pA, eA, qA);
                var pj = PhysicsJoint.CreatePositionMotor(fa, fc, 0f, PistaoImpulso <= 0f ? float.PositiveInfinity : PistaoImpulso);
                j.idxExtra = AcharMotor(pj, ConstraintType.PositionMotor); if (j.idxExtra < 0) return;
                j.cursoBaseExtra = Ecs.Dot(Ecs.Sub(pB, pA), eA);
                Restricao(&pj, j.idxExtra)->Target.x = -(j.alvoAtual - j.cursoBaseExtra);
                var par = new PhysicsConstrainedBodyPair(); par.Entities = new EntityPair { EntityA = j.partA, EntityB = alvoParte }; par.EnableCollision = 0;
                var tipos = new Il2CppStructArray<ComponentType>(3);
                tipos[0] = ComponentType.ReadWrite(Ecs.tiPar); tipos[1] = ComponentType.ReadWrite(Ecs.tiJoint); tipos[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                var e = em.CreateEntity(tipos);
                *(Ecs.TemC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = par;
                *(Ecs.TemC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = pj;
                j.extra = e; j.extraParte = alvoParte;
                Registro.Info(j.nome + " " + j.bloco.Index + ": load recoupled — extra motor directly on part " + alvoParte.Index + " (" + Ecs.Massa(em, alvoParte).ToString("F0") + " kg), stroke " + j.cursoBaseExtra.ToString("F2") + " m");
            }
            catch (Exception ex) { if (j.avisos++ < 5) Registro.Aviso(j.nome + " " + j.bloco.Index + ": extra load motor failed: " + ex.Message); }
        }

        // ================================================================ 0.10.0: time rewind
        /// <summary>O Load do time rewind (UITimebackMenu.OnClick_Load -> Bursted.LoadTimepoint) cria parts NOVAS no lugar e
        /// velocidade do momento salvo, mantem os mesmos blocos (so troca o Parent), destroi as parts velhas e restaura a lista
        /// de joints do jogo. Nossos PhysicsJoint ficavam apontando pra parts mortas. Aqui: recria cada articulacao nas parts
        /// novas, NA POSE DO MOMENTO SALVO (pistao mede o curso, servo mede o angulo, rodas mantem o giro salvo). Se o momento e'
        /// de antes da separacao (mesma part), volta pro comeco (separa de novo). Detecta pelo patch do botao Load (Aba.cs) e,
        /// de reserva, pelo tick de fisica (UniverseCoreSingleton._physicsTickID) andando pra tras.</summary>
        public static bool RewindPedido; static int ultimoTick = -1; static TypeIndex tiUniv; static EntityQuery qUniv; static bool univOk, univFalhou;
        static unsafe void VigiarTick(EntityManager em)
        {
            if (univFalhou) return;
            try
            {
                if (!univOk) { univOk = true; tiUniv = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<UniverseCoreSingleton>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiUniv); qUniv = em.CreateEntityQuery(a); }
                var l = Ecs.Lista(qUniv); if (l.Count == 0) { ultimoTick = -1; return; }
                int tick = ((Ecs.TemC(em, l[0], tiUniv) ? (UniverseCoreSingleton*)em.GetComponentDataRawRO(l[0], tiUniv) : null))->_physicsTickID;
                if (ultimoTick >= 0 && tick < ultimoTick - 5 && !RewindPedido) { RewindPedido = true; Registro.Info("time rewind: physics tick went back " + ultimoTick + " -> " + tick); }
                ultimoTick = tick;
            }
            catch (Exception ex) { univFalhou = true; Registro.Aviso("time rewind: can't read the physics tick (" + ex.Message + ") — only the Load button is detected"); }
        }

        static void Rebobinar(EntityManager em)
        {
            int recriadas = 0, desde0 = 0, adiadas = 0;
            foreach (var j in juntas.Values)
            {
                if (j.estado == 1) { j.quebrados.Clear(); j.estado = 0; j.tEstado = 0f; desde0++; continue; }   // estava separando: recomeca
                if (j.estado != 2) continue;
                try { if (Ecs.Vivo(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
                try { if (Ecs.Vivo(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
                try { if (Ecs.Vivo(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
                j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; ApagarGuias(em, j); SoltarCarga(em, j, "time rewind");
                j.quebrados.Clear(); j.adiamentos = 0; j.tPux = -1f; j.alinhaLog = null; j.angServo = 999f; j.angLog = 999f;
                var pa = Ecs.Pai(em, j.bloco); var pb = Ecs.Pai(em, j.vizinho);
                if (Ecs.EhPart(em, pa) && Ecs.EhPart(em, pb) && pa.Index != pb.Index)
                {
                    j.partA = pa; j.partB = pb; adiado = false;
                    if (Criar(em, j, true))
                    {
                        Reforcar(em, j); recriadas++;
                        if (LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": time rewind — recreated on the new parts (" + pa.Index + " / " + pb.Index + ")" + (j.tipo == Tipo.Pistao ? ", stroke " + j.alvoAtual.ToString("F2") + " m" : ", mode " + (j.modo == 1 ? "motor" : j.modo == 2 ? "servo" : "free")));
                    }
                    else if (adiado) adiadas++;   // Reconstruir tenta de novo nos proximos frames
                    else { Desfazer(em, j, "time rewind: recreating failed"); j.estado = 0; j.tEstado = -1f; desde0++; }
                }
                else { Desfazer(em, j, "time rewind to before separating"); j.estado = 0; j.tEstado = 0f; desde0++; }
            }
            Acoplador.Rebobinou(em);
            Registro.Aviso("===== TIME REWIND: " + recriadas + " joint(s) recreated in the saved pose" + (adiadas > 0 ? ", " + adiadas + " waiting for the game" : "") + (desde0 > 0 ? ", " + desde0 + " back to the start (will separate again)" : "") + " =====");
        }

        // ================================================================ 0.7.12: partida em fila
        /// <summary>Todas as articulacoes nascem travadas (fixas; pistao segura onde esta). Depois de PartidaTempo sem nenhuma
        /// nova nascer, destrava UMA por vez a cada PartidaPausa s: primeiro as presas direto no chassi (nivel 0 = a base nao e'
        /// o topo de nenhuma outra articulacao), depois as de cima delas (ex.: bearing da roda preso na manga da direcao); no
        /// mesmo nivel, a de peca mais leve primeiro. Assim o solver da fisica nao tem que resolver tudo de uma vez.</summary>
        static float tFila, tProxFila;
        static void Fila(EntityManager em, float dt)
        {
            Junta melhor = null; int nMelhor = 0; float mMelhor = 0f; int travadas = 0; bool puxando = false;
            foreach (var j in juntas.Values)
            {
                if (j.estado != 2 || j.tPartida <= 0f) continue;
                travadas++;
                if (j.tPux >= 0f) { puxando = true; continue; }
            }
            ColisaoPartida(em, dt, travadas);
            if (travadas == 0) { tFila = 0f; tProxFila = 0f; return; }
            tFila += dt;
            if (tFila < PartidaTempo) return;
            if (PartidaPausa <= 0f)
            {
                if (puxando) return;
                foreach (var j in juntas.Values) if (j.estado == 2 && j.tPartida > 0f) Liberar(em, j, Nivel(j), 0);
                return;
            }
            tProxFila -= dt; if (tProxFila > 0f) return;
            foreach (var j in juntas.Values)
            {
                if (j.estado != 2 || j.tPartida <= 0f || j.tPux >= 0f) continue;
                // 0.7.13: bearings (rodas) primeiro, pistoes por ultimo — o carro assenta antes dos pistoes mexerem
                int n = Nivel(j); float m = 0f; try { m = Ecs.Massa(em, j.partB); } catch (Exception) { }
                int g = j.tipo == Tipo.Pistao ? 1 : 0, gM = melhor == null ? 9 : melhor.tipo == Tipo.Pistao ? 1 : 0;
                if (melhor == null || g < gM || g == gM && (n < nMelhor || n == nMelhor && (m < mMelhor - 0.5f || System.Math.Abs(m - mMelhor) <= 0.5f && j.bloco.Index < melhor.bloco.Index))) { melhor = j; nMelhor = n; mMelhor = m; }
            }
            if (melhor == null) return;   // so sobrou quem ainda esta sendo puxado pro eixo
            Liberar(em, melhor, nMelhor, travadas - 1);
            tProxFila = PartidaPausa;
        }

        static void Liberar(EntityManager em, Junta j, int nivel, int faltam)
        {
            j.tPartida = 0f;
            if (j.tipo == Tipo.Bearing) { j.modo = -1; j.impulsoModo = -1f; }   // proximo frame reescreve o joint no modo certo
            if (LogDetalhe)
            {
                string m = "?"; try { m = Ecs.Massa(em, j.partB).ToString("F0"); } catch (Exception) { }
                Registro.Info(j.nome + " " + j.bloco.Index + ": staggered start — released (level " + nivel + ", top part " + j.partB.Index + " " + m + " kg" + (faltam > 0 ? ", remaining " + faltam : ", last") + ")");
            }
        }

        /// <summary>Bearing: distancia entre os dois lados do encaixe, separada em ao longo do eixo (+ = roda empurrada pra fora,
        /// pro lado do bloco de cima) e de lado; angulo entre os eixos.</summary>
        static void Medir(EntityManager em, Junta j, out float total, out float axial, out float lateral, out float ang)
        {
            Encaixes(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
            var d = Ecs.Sub(pB, pA); total = Ecs.Comp(d); axial = Ecs.Dot(d, eA); lateral = Ecs.Comp(Ecs.Sub(d, Ecs.Mul(eA, axial)));
            ang = (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)))) * 180.0 / System.Math.PI);
        }
        static string TextoEixo(float axial, float lateral) => "along the axis " + (axial >= 0f ? "+" : "") + (axial * 100f).ToString("F0") + " cm, sideways " + (lateral * 100f).ToString("F0") + " cm";

        // ================================================================ 0.7.13: sem colisao roda x chassi so na partida
        /// <summary>Enquanto a fila de partida roda (e SemColisaoPartida s depois), a roda nao colide com as parts ACIMA dela na
        /// corrente de bearings (ex.: roda da direcao x chassi; a manga ja nao colide por ser o par do proprio joint). Depois
        /// religa e loga onde cada roda estava antes e 1 s depois — se ela so sair do eixo quando a colisao volta, a causa e' o
        /// pneu encostando no chassi. Joint "vazio" = LimitedDistance 0..1e6 m (nao segura nada) com EnableCollision = 0.</summary>
        public static float SemColisaoPartida = 3f;
        class SemCol { public Entity e, roda, acima; public Junta j; }
        static readonly List<SemCol> semCol = new List<SemCol>();
        static float tDepoisFila; static readonly List<Junta> religadas = new List<Junta>(); static float tReligou = -1f;
        static unsafe void ColisaoPartida(EntityManager em, float dt, int travadas)
        {
            // pares que ficaram velhos (parts refeitas): tira
            for (int i = semCol.Count - 1; i >= 0; i--)
            {
                var c = semCol[i];
                bool ok = Ecs.Vivo(em, c.e) && Ecs.EhPart(em, c.roda) && Ecs.EhPart(em, c.acima) && c.j.estado == 2 && c.j.partB.Index == c.roda.Index;
                if (!ok) { try { if (Ecs.Vivo(em, c.e)) em.DestroyEntity(c.e); } catch (Exception) { } semCol.RemoveAt(i); }
            }
            if (tReligou >= 0f)
            {
                tReligou += dt;
                if (tReligou >= 1f)
                {
                    tReligou = -1f;
                    foreach (var j in religadas) { try { Medir(em, j, out var t, out var ax, out var la, out var an); Registro.Info(j.nome + " " + j.bloco.Index + ": 1 s after re-enabling collision — wheel at " + (t * 100f).ToString("F0") + " cm (" + TextoEixo(ax, la) + "), tilted " + an.ToString("F1") + " degrees"); } catch (Exception) { } }
                    religadas.Clear();
                }
            }
            if (SemColisaoPartida <= 0f) { if (semCol.Count > 0) Religar(em); return; }
            if (travadas > 0)
            {
                tDepoisFila = 0f;
                foreach (var j in juntas.Values)
                {
                    if (j.tipo != Tipo.Bearing || j.estado != 2 || Ecs.Nulo(j.partB)) continue;
                    var cur = j; int guarda = 0;
                    while (guarda++ < 10)
                    {
                        Junta pai = null;
                        foreach (var o in juntas.Values) if (o != cur && o.tipo == Tipo.Bearing && o.estado == 2 && !Ecs.Nulo(o.partB) && o.partB.Index == cur.partA.Index) { pai = o; break; }
                        if (pai == null) break;
                        var acima = pai.partA;
                        if (acima.Index != j.partA.Index && acima.Index != j.partB.Index && !semCol.Exists(c => c.roda.Index == j.partB.Index && c.acima.Index == acima.Index))
                        {
                            try
                            {
                                var par = new PhysicsConstrainedBodyPair(); par.Entities = new EntityPair { EntityA = acima, EntityB = j.partB }; par.EnableCollision = 0;
                                var pj = PhysicsJoint.CreateLimitedDistance(float3.zero, float3.zero, new Unity.Physics.Math.FloatRange { Min = 0f, Max = 1e6f });
                                var tipos = new Il2CppStructArray<ComponentType>(3);
                                tipos[0] = ComponentType.ReadWrite(Ecs.tiPar); tipos[1] = ComponentType.ReadWrite(Ecs.tiJoint); tipos[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                                var e = em.CreateEntity(tipos);
                                *(Ecs.TemC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = par;
                                *(Ecs.TemC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = pj;
                                semCol.Add(new SemCol { e = e, roda = j.partB, acima = acima, j = j });
                                if (LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": start — wheel (part " + j.partB.Index + ") without collision with part " + acima.Index + " (" + Ecs.Massa(em, acima).ToString("F0") + " kg) above it until " + SemColisaoPartida.ToString("F0") + " s after the queue");
                            }
                            catch (Exception ex) { Registro.Aviso(j.nome + " " + j.bloco.Index + ": disabling collision at start failed: " + ex.Message); }
                        }
                        cur = pai;
                    }
                }
                return;
            }
            if (semCol.Count == 0) return;
            tDepoisFila += dt;
            if (tDepoisFila >= SemColisaoPartida) Religar(em);
        }

        static void Religar(EntityManager em)
        {
            religadas.Clear();
            foreach (var c in semCol)
            {
                if (!religadas.Contains(c.j))
                {
                    religadas.Add(c.j);
                    try { Medir(em, c.j, out var t, out var ax, out var la, out var an); Registro.Info(c.j.nome + " " + c.j.bloco.Index + ": re-enabling collision wheel x part " + c.acima.Index + " — the wheel is now at " + (t * 100f).ToString("F0") + " cm (" + TextoEixo(ax, la) + "), tilted " + an.ToString("F1") + " degrees"); } catch (Exception) { }
                }
                try { if (Ecs.Vivo(em, c.e)) em.DestroyEntity(c.e); } catch (Exception) { }
            }
            Registro.Info("start: wheel x chassis collision re-enabled (" + semCol.Count + " pair(s))");
            semCol.Clear(); tReligou = 0f;
        }

        /// <summary>Quantas articulacoes do mod tem entre esta e o chassi (0 = a base dela nao e' o topo de outra).</summary>
        static int Nivel(Junta j)
        {
            int n = 0; var cur = j;
            while (n < 10)
            {
                Junta pai = null;
                foreach (var o in juntas.Values) if (o != cur && o.estado == 2 && !Ecs.Nulo(o.partB) && o.partB.Index == cur.partA.Index) { pai = o; break; }
                if (pai == null) break;
                n++; cur = pai;
            }
            return n;
        }

        /// <summary>Bearing que nasceu fora do eixo: anda o frame do lado A (fixo) de onde a roda esta ate o encaixe certo.</summary>
        static unsafe void Puxando(EntityManager em, Junta j, float dt)
        {
            if (j.tPux < 0f) return;
            try
            {
                j.tPux += dt;
                float t = j.durPux <= 0f ? 1f : System.Math.Min(1f, j.tPux / j.durPux); t = t * t * (3f - 2f * t);
                var fa = new BodyFrame();
                fa.Position = Ecs.Add(Ecs.Mul(j.puxA0.Position, 1f - t), Ecs.Mul(j.puxA1.Position, t));
                var ax = Ecs.Norm(Ecs.Add(Ecs.Mul(j.puxA0.Axis, 1f - t), Ecs.Mul(j.puxA1.Axis, t)));
                var pp = Ecs.Add(Ecs.Mul(j.puxA0.PerpendicularAxis, 1f - t), Ecs.Mul(j.puxA1.PerpendicularAxis, t));
                pp = Ecs.Norm(Ecs.Sub(pp, Ecs.Mul(ax, Ecs.Dot(pp, ax))));
                fa.Axis = ax; fa.PerpendicularAxis = pp;
                var pj = (Ecs.TemC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null);
                int v = pj->m_Version; var novo = PhysicsJoint.CreateFixed(fa, j.fb); novo.m_Version = (byte)(v + 1); *pj = novo;
                if (j.apA0 != null)
                {
                    var ex = new[] { j.joint2, j.joint3 };
                    for (int i = 0; i < 2; i++)
                    {
                        if (!Ecs.Vivo(em, ex[i])) continue;
                        var pb = (Ecs.TemC(em, ex[i], Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(ex[i], Ecs.tiJoint) : null); int vb = pb->m_Version;
                        var bs = PhysicsJoint.CreateBallAndSocket(Ecs.Add(Ecs.Mul(j.apA0[i], 1f - t), Ecs.Mul(j.apA1[i], t)), j.apB[i]); bs.m_Version = (byte)(vb + 1); *pb = bs;
                    }
                }
                if (t < 1f) return;
                j.tPux = -1f;
                if (LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": wheel pulled onto the correct axis in " + j.durPux.ToString("F1") + " s");
            }
            catch (Exception ex) { j.tPux = -1f; Registro.Aviso(j.nome + " " + j.bloco.Index + ": pulling onto the axis failed: " + ex.Message); }
        }

        /// <summary>Foto do estado de todas as articulacoes, pra marca de erro (F8).</summary>
        public static string Estado(EntityManager em)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var j in juntas.Values)
            {
                sb.Append("  ").Append(j.nome).Append(' ').Append(j.bloco.Index).Append(": state ").Append(j.estado == 0 ? "waiting" : j.estado == 1 ? "separating" : "active");
                if (j.estado == 2)
                {
                    try
                    {
                        sb.Append(j.tPartida > 0f ? " LOCKED (start" + (j.tPux >= 0f ? ", pulling onto the axis" : "") + ")" : "");
                        sb.Append(j.tipo == Tipo.Bearing ? ", mode " + (j.modo == 1 ? "motor" : j.modo == 2 ? "servo" : j.modo == 0 ? "free" : "?") + " target " + j.alvo.ToString("F2") : ", requested stroke " + j.alvoAtual.ToString("F2") + " m");
                        sb.Append(", level ").Append(Nivel(j));
                        sb.Append(", part ").Append(j.partA.Index).Append(' ').Append(Ecs.Massa(em, j.partA).ToString("F0")).Append(" kg -> part ").Append(j.partB.Index).Append(' ').Append(Ecs.Massa(em, j.partB).ToString("F0")).Append(" kg");
                        Encaixes(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
                        var d = Ecs.Sub(pB, pA); float ax = Ecs.Dot(d, eA); float lat = j.tipo == Tipo.Pistao ? Ecs.Comp(Ecs.Sub(d, Ecs.Mul(eA, ax))) : Ecs.Comp(d);
                        float ang = (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)))) * 180.0 / System.Math.PI);
                        sb.Append(", off-axis ").Append((lat * 100f).ToString("F1")).Append(" cm");
                        if (j.tipo == Tipo.Bearing) { var perp = Ecs.Comp(Ecs.Sub(d, Ecs.Mul(eA, ax))); sb.Append(" (").Append(TextoEixo(ax, perp)).Append(')'); }
                        sb.Append(", tilted ").Append(ang.ToString("F1")).Append(" degrees");
                        if (j.tipo == Tipo.Pistao) sb.Append(", actual stroke ").Append(ax.ToString("F2")).Append(" m");
                        unsafe
                        {
                            var va = (Ecs.TemC(em, j.partA, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partA, Ecs.tiVel) : null); var vb = (Ecs.TemC(em, j.partB, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partB, Ecs.tiVel) : null);
                            if (va != null && vb != null) sb.Append(", vel A ").Append(Ecs.Comp(va->Linear).ToString("F1")).Append(" m/s spin ").Append(Ecs.Comp(va->Angular).ToString("F1")).Append(" | vel B ").Append(Ecs.Comp(vb->Linear).ToString("F1")).Append(" m/s spin ").Append(Ecs.Comp(vb->Angular).ToString("F1")).Append(" rad/s");
                        }
                    }
                    catch (Exception ex) { sb.Append(" (read error: ").Append(ex.Message).Append(')'); }
                }
                sb.Append('\n');
            }
            foreach (var m in motores.Values) sb.Append("  ").Append(m.nome).Append(' ').Append(m.bloco.Index).Append(": signal ").Append(m.sinal.ToString("F2")).Append(m.alimentado ? ", powered" : ", NO power").Append(m.comb ? ", fuel in motor " + m.combTem.ToString("F2") + ", usage " + (m.uso * 100f).ToString("F0") + "%, mixture efficiency " + (m.rendimento * 100f).ToString("F0") + "%" : "").Append('\n');
            sb.Append(EstadoTorres());
            return sb.ToString();
        }

        /// <summary>Pares de parts ligados por joints do mod (pra saber o que e' o mesmo veiculo).</summary>
        public static IEnumerable<(Entity a, Entity b)> Ligacoes() { foreach (var j in juntas.Values) if (j.estado == 2 && !Ecs.Nulo(j.partA) && !Ecs.Nulo(j.partB)) yield return (j.partA, j.partB); }
        /// <summary>Se a part e' o topo de um pistao ativo, a part da base dele (a nave); senao Entity.Null.</summary>
        public static Entity BaseDoPistao(Entity parte) { foreach (var j in juntas.Values) if (j.estado == 2 && j.tipo == Tipo.Pistao && j.partB.Index == parte.Index) return j.partA; return Entity.Null; }

        static void ApagarGuias(EntityManager em, Junta j)
        {
            foreach (var g in j.guias) { try { if (Ecs.Vivo(em, g)) em.DestroyEntity(g); } catch (Exception) { } }
            j.guias.Clear();
        }

        static void SoltarCarga(EntityManager em, Junta j, string motivo)
        {
            try { if (Ecs.Vivo(em, j.extra)) em.DestroyEntity(j.extra); } catch (Exception) { }
            if (!Ecs.Nulo(j.extra) && LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": extra load motor removed (" + motivo + ")");
            j.extra = Entity.Null; j.extraParte = Entity.Null; j.idxExtra = -1;
        }

        /// <summary>Diagnostico (0.7.5): quanto o topo do pistao saiu do eixo. Curso real (ao longo do eixo), desvio lateral e
        /// angulo entre o eixo do bloco e o do vizinho. Loga no maximo 1x/s e so quando passa de 5 cm / 3 graus ou muda bastante.</summary>
        /// <summary>0.7.11: quanto a roda saiu do eixo do bearing (os dois pontos do encaixe deviam ser o mesmo, e os eixos
        /// paralelos). Loga "TORTO" igual ao pistao, 1x/s no maximo e so quando muda de faixa.</summary>
        static void AlinhamentoBearing(EntityManager em, Junta j, float dt)
        {
            if (!LogDetalhe) return;
            j.tAlinha += dt; if (j.tAlinha < 1f) return; j.tAlinha = 0f;
            try
            {
                Medir(em, j, out float d, out float axial, out float lateral, out float ang);
                bool ruim = d > 0.05f || ang > 3f;
                string chave = ruim ? "bad " + ((int)(d * 10f)) + " " + ((int)(ang / 5f)) : "ok";
                if (chave != j.alinhaLog)
                {
                    j.alinhaLog = chave;
                    string s = "wheel off-axis " + (d * 100f).ToString("F0") + " cm (" + TextoEixo(axial, lateral) + "), tilted " + ang.ToString("F1") + " degrees (mode " + (j.modo == 1 ? "motor" : j.modo == 2 ? "servo" : "free") + ", part " + j.partA.Index + " " + Ecs.Massa(em, j.partA).ToString("F0") + " kg -> part " + j.partB.Index + " " + Ecs.Massa(em, j.partB).ToString("F0") + " kg)";
                    if (ruim) Registro.Aviso(j.nome + " " + j.bloco.Index + ": MISALIGNED — " + s); else Registro.Info(j.nome + " " + j.bloco.Index + ": aligned — " + s);
                }
            }
            catch (Exception) { }
        }

        static void Alinhamento(EntityManager em, Junta j, float dt)
        {
            if (!LogDetalhe) return;
            j.tAlinha += dt; if (j.tAlinha < 1f) return; j.tAlinha = 0f;
            try
            {
                Encaixes(em, j, out var pA, out var eA, out _, out var pB, out var eB, out _);
                var d = Ecs.Sub(pB, pA); float ax = Ecs.Dot(d, eA); float lat = Ecs.Comp(Ecs.Sub(d, Ecs.Mul(eA, ax)));
                float cos = System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB))); float ang = (float)(System.Math.Acos(cos) * 180.0 / System.Math.PI);
                bool ruim = lat > 0.05f || ang > 3f;
                string s = "actual stroke " + ax.ToString("F2") + " m (requested " + j.alvoAtual.ToString("F2") + "), off-axis " + (lat * 100f).ToString("F0") + " cm, tilted " + ang.ToString("F1") + " degrees";
                string chave = ruim ? "bad " + (lat * 10f).ToString("F0") + " " + ((int)(ang / 3f)) : "ok";
                if (chave != j.alinhaLog) { j.alinhaLog = chave; if (ruim) Registro.Aviso(j.nome + " " + j.bloco.Index + ": MISALIGNED — " + s); else Registro.Info(j.nome + " " + j.bloco.Index + ": aligned — " + s); }
            }
            catch (Exception) { }
        }

        // ================================================================ bearing (quarter 0,5 m: face de baixo cola, face de cima solta e gira)
        static unsafe void AtivoBearing(EntityManager em, Junta j, float dt)
        {
            Vigiar(em, j);
            if (!Valido(em, j)) { if (Reconstruir(em, j)) return; Desfazer(em, j, "part/joint changed (time rewind, load or break)"); j.estado = 0; j.tEstado = -1f; return; }
            AlinhamentoBearing(em, j, dt);
            if (j.tPartida > 0f) { Puxando(em, j, dt); return; }   // partida suave: travado (fixo) ate a fila liberar (0.7.12)
            if (j.guia) { if (j.modo != 0) { var pjg = (Ecs.TemC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null); int vg = pjg->m_Version; var ng = PhysicsJoint.CreateHinge(j.fa, j.fb); ng.m_Version = (byte)(vg + 1); *pjg = ng; j.modo = 0; j.idxMotor = -1; j.impulsoModo = 0f; Registro.Info(j.nome + " " + j.bloco.Index + ": FREE (closes a ring)"); } return; }   // 0.12.2
            // 0.15.0: quem comanda: o motor cujo Drive Shaft chega neste bearing (MapearEixos) > sinal de dados numa entrada (servo) > nada (livre).
            // Com os dois: o eixo manda enquanto gira; com o eixo em 0 (ou sem energia) vale o servo.
            Motor mot = null; bool servo = false; float valServo = 0f; string origem = "nothing connected", origemServo = "";
            float valMotor = 0f; Rede rede = null;
            // 0.17.0: rede de motores (somam); valor = media pesada pela forca; conflito de sinais = travado
            if (acionados.TryGetValue(Ecs.K(j.bloco), out var ac) && ac.rede != null && ac.rede.rep != null) { rede = ac.rede; mot = rede.rep; valMotor = ac.valor; origem = ac.caminho + (rede.motores.Count > 1 ? " [" + rede.nome + "]" : ""); }
            foreach (var p in Ecs.Portas(em, j.bloco))
            {
                if (!Ecs.Porta(em, p, out var t, out _, out _) || (t != Ecs.PortaEntrada && t != PortaSemTipo)) continue;
                if (PortaNoEixoCache(em, p)) continue;
                if (!Ecs.LerEntrada(em, p, out var v, out _, out var trilha)) continue;
                if (!servo) { servo = true; valServo = v; origemServo = "direct signal, port " + p.Index + trilha; }
            }
            bool travadoEixo = rede != null && rede.conflito;
            bool eixoGirando = mot != null && (travadoEixo || (rede.alimentado && System.Math.Abs(valMotor) > 0.001f && mot.freio <= 0.02f));
            if (servo && !eixoGirando && mot != null) { mot = null; origem = origemServo + " (shaft at 0)"; }
            else if (servo && mot == null) origem = origemServo;
            else if (eixoGirando) servo = false;
            int modo; float alvo, impulso;
            // 0.11.2 (Andre: "aumenta mais a forca dos freios"): freio MECANICO — funciona sem energia/combustivel e trava TODAS as
            // rodas do veiculo (bearings sem sinal direto de servo) quando qualquer motor do mesmo veiculo recebe freio; a forca
            // e' a do motor que freia x Motor.freio_fator (30). Bearing com sinal direto (direcao) nao trava.
            var freiando = servo ? null : MotorFreiando(em, j, mot);
            if (travadoEixo)
            {
                // 0.17.0 (Andre: "se entrar sinais invertidos trava os bearings e mostra visualmente o bearing travado")
                modo = 3; impulso = -4f; alvo = 0f;
                origem = "LOCKED by the shaft: " + rede.conflitoLog + " [" + rede.nome + "]";
            }
            else if (freiando != null && freiando.freio >= 0.9f)
            {
                // 0.12.1 (Andre: "mesmo freiado ele continua girando e desce o morro"): freio no fundo = roda TRAVADA (joint fixo na pose atual)
                modo = 3; impulso = -3f; alvo = 0f;
                origem = "brake LOCKED by " + freiando.nome + " " + freiando.bloco.Index + (freiando == mot ? "" : " (same vehicle)");
            }
            else if (freiando != null)
            {
                modo = 1; impulso = System.Math.Max(freiando.impulso, freiando.impulsoBase) * (1f + (FreioFator - 1f) * freiando.freio); alvo = 0f;
                origem = "brake from " + freiando.nome + " " + freiando.bloco.Index + (freiando == mot ? "" : " (same vehicle)");
            }
            else if (mot != null)
            {
                if (rede.alimentado)
                {
                    modo = 1; impulso = rede.forcaBearing;
                    float pedido = System.Math.Max(-1f, System.Math.Min(1f, valMotor)) * rede.velMax * (j.espelho ? -1f : 1f), passo = MotorRampa <= 0f ? 999f : rede.velMax / MotorRampa * dt;
                    alvo = pedido > j.alvoAtual + passo ? j.alvoAtual + passo : pedido < j.alvoAtual - passo ? j.alvoAtual - passo : pedido;
                }
                else { modo = 0; alvo = 0f; impulso = 0f; origem += " no power"; }
            }
            else if (servo)
            {
                modo = 2; impulso = ServoImpulso; alvo = System.Math.Max(-1f, System.Math.Min(1f, valServo)) * (float.IsNaN(j.grausServo) ? ServoGraus : j.grausServo) * (float)(System.Math.PI / 180.0) * (j.espelho ? -1f : 1f);   // 0.19.2: knob do bloco manda; sem knob = config
                if (LogDetalhe && System.Math.Abs(valServo - j.sinalLogado) >= 0.05f) { j.sinalLogado = valServo; Registro.Info(j.nome + " " + j.bloco.Index + ": servo signal=" + valServo.ToString("F2")); }
            }
            else { modo = 0; alvo = 0f; impulso = 0f; }

            var pj = (Ecs.TemC(em, j.joint, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(j.joint, Ecs.tiJoint) : null);
            if (modo == j.modo && modo == 1 && impulso != j.impulsoModo && j.idxMotor >= 0 && impulso > 0f && j.impulsoModo > 0f)
            {
                // 0.8.0: motor a combustivel muda a forca com a mistura — so troca o limite do motor, sem refazer o joint
                Restricao(pj, j.idxMotor)->MaxImpulse = new float3 { x = impulso, y = impulso, z = impulso }; pj->m_Version++; j.impulsoModo = impulso;
            }
            if (modo != j.modo || impulso != j.impulsoModo)
            {
                int versao = pj->m_Version;
                PhysicsJoint novo;
                if (modo == 3)
                {
                    Encaixes(em, j, out var pAt, out var eAt, out var qAt, out _, out _, out _);
                    novo = PhysicsJoint.CreateFixed(j.fa, Ecs.Frame(em, j.partB, pAt, eAt, qAt)); j.idxMotor = -1;
                }
                else novo = Montar(j, modo, alvo, impulso, out j.idxMotor);
                novo.m_Version = (byte)(versao + 1);
                *pj = novo;
                if (modo != j.modo || origem != j.origemModo) Registro.Info(j.nome + " " + j.bloco.Index + ": mode " + (modo == 0 ? "free" : modo == 1 ? "motor" : modo == 3 ? "LOCKED" : "servo") + " (" + origem + ")");
                j.modo = modo; j.impulsoModo = impulso; j.origemModo = origem; j.alvoAtual = modo == 1 ? alvo : 0f; j.alvo = alvo; j.angServo = 999f;
                return;
            }
            LedBearing(em, j, travadoEixo, dt);
            if (modo == 0 || modo == 3) return;
            if (modo == 2) { ServoPasso(em, j, pj, alvo, dt); return; }
            if (modo == 1) j.alvoAtual = alvo;
            if (System.Math.Abs(alvo - j.alvo) < 1e-5f) return;
            j.alvo = alvo;
            Constraint* c = Restricao(pj, j.idxMotor);
            c->Target.x = alvo;
            pj->m_Version++;
        }

        // ---- veiculo = parts ligadas por articulacoes do mod (juntas ativas) e recouplers travados; recalculado 1x por frame
        static readonly Dictionary<long, long> grupoPart = new Dictionary<long, long>(); static int frameGrupo = -1;
        static long Raiz(long k) { while (grupoPart.TryGetValue(k, out var p) && p != k) k = p; return k; }
        static void Unir(long a, long b) { a = Raiz(a); b = Raiz(b); if (!grupoPart.ContainsKey(a)) grupoPart[a] = a; if (!grupoPart.ContainsKey(b)) grupoPart[b] = b; if (a != b) grupoPart[a] = b; }
        static void AtualizarGrupos(EntityManager em)
        {
            if (frameGrupo == UnityEngine.Time.frameCount) return; frameGrupo = UnityEngine.Time.frameCount;
            grupoPart.Clear();
            foreach (var o in juntas.Values) if (o.estado == 2 && !Ecs.Nulo(o.partA) && !Ecs.Nulo(o.partB)) Unir(Ecs.K(o.partA), Ecs.K(o.partB));
            foreach (var (ta, tb) in LigacoesTorres()) Unir(Ecs.K(ta), Ecs.K(tb));
            foreach (var r in Acoplador.Travados()) if (!Ecs.Nulo(r.partA) && !Ecs.Nulo(r.partB)) Unir(Ecs.K(r.partA), Ecs.K(r.partB));
        }
        /// <summary>Motor com freio acionado que vale pra este bearing: o proprio motor dele, ou qualquer motor no mesmo veiculo.</summary>
        static Motor MotorFreiando(EntityManager em, Junta j, Motor proprio)
        {
            if (proprio != null && proprio.freio > 0.02f) return proprio;
            bool algum = false; foreach (var m in motores.Values) if (m.freio > 0.02f) { algum = true; break; }
            if (!algum) return null;
            AtualizarGrupos(em);
            long ga = Raiz(Ecs.K(j.partA)), gb = Ecs.Nulo(j.partB) ? ga : Raiz(Ecs.K(j.partB));
            Motor melhor = null;
            foreach (var m in motores.Values)
            {
                if (m.freio <= 0.02f || !Ecs.Vivo(em, m.bloco)) continue;
                var pm = Ecs.Pai(em, m.bloco); if (!Ecs.EhPart(em, pm)) continue;
                long gm = Raiz(Ecs.K(pm));
                if (gm != ga && gm != gb) continue;
                if (melhor == null || m.freio > melhor.freio) melhor = m;
            }
            return melhor;
        }

        /// <summary>Angulo (rad, -pi..pi) do lado de cima do bearing em volta do eixo, em relacao ao lado de baixo: perpendicular
        /// do bloco (lado A) x perpendicular guardada no bloco vizinho (lado B). 0 = como estava na garagem.</summary>
        static float AnguloServo(EntityManager em, Junta j)
        {
            Encaixes(em, j, out _, out var eA, out var qA, out _, out _, out var qB);
            var qBp = Ecs.Norm(Ecs.Sub(qB, Ecs.Mul(eA, Ecs.Dot(qB, eA))));
            return (float)System.Math.Atan2(Ecs.Dot(Ecs.Cross(qA, qBp), eA), Ecs.Dot(qA, qBp));
        }

        /// <summary>Servo do mod: mede o angulo e pede ao motor de velocidade Ganho x erro (limitado a ServoVel). O motor de
        /// velocidade forca (wA - wB)·eixo = alvo, e o angulo medido cresce com (wB - wA)·eixo -> alvo do motor = -velocidade.
        /// Sentido igual ao RotationMotor antigo (ele levava o angulo medido pra -alvo); Bearing.servo_inverter troca.</summary>
        static unsafe void ServoPasso(EntityManager em, Junta j, PhysicsJoint* pj, float alvo, float dt)
        {
            j.alvo = alvo;
            float th; try { th = AnguloServo(em, j); } catch (Exception) { return; }
            float thAlvo = ServoInverter ? alvo : -alvo;
            // 0.19.1 (Andre: "o modo servo pode ser 360?"): o angulo medido vem cortado em -180..180; aqui ele e' somado volta a volta
            // (angCont), entao um alvo de ate 360 graus gira a volta inteira sem pular. Ao (re)comecar, a volta escolhida e' a mais perto do alvo.
            const float PI2 = 2f * (float)System.Math.PI;
            if (j.angServo > 900f) { j.angCont = th; while (j.angCont - thAlvo > (float)System.Math.PI) j.angCont -= PI2; while (thAlvo - j.angCont > (float)System.Math.PI) j.angCont += PI2; }
            else
            {
                float d = th - j.angServo; if (d > (float)System.Math.PI) d -= PI2; else if (d < -(float)System.Math.PI) d += PI2;
                if (System.Math.Abs(d) > 0.5f && LogDetalhe) Registro.Aviso(j.nome + " " + j.bloco.Index + ": servo — angle jumped " + (d * 57.2958f).ToString("F0") + " degrees in one frame");
                j.angCont += d;
            }
            j.angServo = th;
            float err = thAlvo - j.angCont;
            float w = System.Math.Max(-ServoVel, System.Math.Min(ServoVel, ServoGanho * err));
            float alvoMotor = -w;
            if (System.Math.Abs(alvoMotor - j.alvoAtual) > 1e-3f && j.idxMotor >= 0)
            {
                Restricao(pj, j.idxMotor)->Target.x = alvoMotor; pj->m_Version++; j.alvoAtual = alvoMotor;
            }
            j.tAngLog += dt;
            if (LogDetalhe && j.tAngLog >= 0.5f && System.Math.Abs(th - j.angLog) >= 5f / 57.2958f)
            {
                j.tAngLog = 0f; j.angLog = th;
                Registro.Info(j.nome + " " + j.bloco.Index + ": servo angle " + (j.angCont * 57.2958f).ToString("F0") + " degrees (requested " + (thAlvo * 57.2958f).ToString("F0") + ")");
            }
        }

        /// <summary>Sobe pelo circuito a partir de uma porta de saida: se ela e' a saida de um Motor, achou; senao olha as
        /// entradas do componente dono dela (Data Router, inversor, math...) e sobe por cada uma (ate 8 niveis).</summary>
        static Motor MotorAcima(EntityManager em, Entity saida, int nivel, HashSet<long> vistos, out string via)
        {
            via = "";
            if (Ecs.Nulo(saida) || nivel > 8) return null;
            if (motorPorSaida.TryGetValue(Ecs.K(saida), out var m)) return m;
            var dono = Ecs.Pai(em, saida); if (Ecs.Nulo(dono) || !Ecs.Vivo(em, dono)) return null;
            vistos ??= new HashSet<long>(); if (!vistos.Add(Ecs.K(dono))) return null;
            // Wireless Transmitter recebendo: o sinal vem de OUTRO transmitter no mesmo canal (o que esta ligado numa saida)
            int canal = Ecs.Canal(em, dono);
            if (canal >= 0)
            {
                foreach (var t in Ecs.Transmitters(em))
                {
                    if (t.Index == dono.Index && t.Version == dono.Version) continue;
                    if (Ecs.Canal(em, t) != canal || !vistos.Add(Ecs.K(t))) continue;
                    foreach (var p in Ecs.Portas(em, t))
                    {
                        var f = Ecs.Fonte(em, p, out _); if (Ecs.Nulo(f) || Ecs.Canal(em, Ecs.Pai(em, f)) >= 0) continue;
                        var r = MotorAcima(em, f, nivel + 1, vistos, out var v2);
                        if (r != null) { via = " via wireless channel " + canal + " (" + dono.Index + "<-" + t.Index + ")" + v2; return r; }
                    }
                }
                return null;
            }
            foreach (var p in Ecs.Portas(em, dono))
            {
                if (p.Index == saida.Index && p.Version == saida.Version) continue;
                if (!Ecs.Porta(em, p, out var t, out _, out _) || (t != Ecs.PortaEntrada && t != 3)) continue;
                var f = Ecs.Fonte(em, p, out _); if (Ecs.Nulo(f)) continue;
                var r = MotorAcima(em, f, nivel + 1, vistos, out var v2);
                if (r != null) { via = " via " + dono.Index + v2; return r; }
            }
            return null;
        }

        static PhysicsJoint Montar(Junta j, int modo, float alvo, float impulso, out int idx)
        {
            float imp = impulso <= 0f ? float.PositiveInfinity : impulso;
            PhysicsJoint pj; ConstraintType t;
            if (modo == 1) { pj = PhysicsJoint.CreateAngularVelocityMotor(j.fa, j.fb, alvo, imp); t = ConstraintType.AngularVelocityMotor; }
            // 0.7.14: servo = motor de VELOCIDADE com o angulo medido pelo mod (ver ServoPasso). O RotationMotor do Unity Physics
            // le o angulo com Quaternion.ToAngleAxis (0..360): quando a rotacao vem com o sinal trocado (q e -q sao a mesma), ele
            // le 359 em vez de -1 e da uma volta inteira — a manga + roda davam um 360 na partida e atravessavam o chassi
            else if (modo == 2) { pj = PhysicsJoint.CreateAngularVelocityMotor(j.fa, j.fb, 0f, imp); t = ConstraintType.AngularVelocityMotor; }
            else { idx = -1; return PhysicsJoint.CreateHinge(j.fa, j.fb); }
            idx = AcharMotor(pj, t);
            if (idx < 0) Registro.Erro(j.nome + ": joint has no motor (" + pj.m_Constraints.A.Type + "," + pj.m_Constraints.B.Type + "," + pj.m_Constraints.C.Type + ")");
            return pj;
        }

        static unsafe string ListarJoints(EntityManager em, Junta j, SpaceshipJoint* joints, int n, float3 pb, float3 ey)
        {
            var sb = new System.Text.StringBuilder(); int c = 0;
            for (int k = 0; k < n && c < 8; k++)
            {
                var sj = &joints[k];
                bool a = Igual(sj->_a, j.bloco), b = Igual(sj->_b, j.bloco); if (!a && !b) continue;
                var o = a ? sj->_b : sj->_a; c++;
                string lado = "?"; try { var d = Ecs.Sub(Ecs.Posicao(em, o), pb); lado = (Ecs.Dot(d, ey) / System.Math.Max(1e-4f, Ecs.Comp(d))).ToString("F2"); } catch (Exception) { }
                sb.Append(" [").Append(k).Append(" other=").Append(o.Index).Append(" above=").Append(lado).Append(" breakForce=").Append(sj->_breakForce.ToString("F0")).Append("]");
            }
            return c == 0 ? " none" : sb.ToString();
        }

        // ================================================================ comum
        static bool Igual(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;

        /// <summary>Forca um joint da nave a quebrar no proximo tick. O SCTick_Decoupler escreve _currentStrain=1e9, mas ele roda
        /// DENTRO do tick de fisica (depois do SpaceshipPhysicsFrameInit, que zera _currentStrain, e antes do SpaceshipCheckJoints).
        /// A gente roda no Update: baixa o _breakForce pra -1 (a checagem e' "_currentStrain >= _breakForce") e devolve depois.</summary>
        static unsafe void Quebrar(Junta j, SpaceshipJoint* sj, int k)
        {
            if (sj->_breakForce > 0f) j.quebrados.Add((k, sj->_a, sj->_b, sj->_breakForce));
            sj->_breakForce = -1f; sj->_currentStrain = 1e9f;
        }

        static bool Valido(EntityManager em, Junta j) =>
            Ecs.Vivo(em, j.joint) && Ecs.EhPart(em, j.partA) && Ecs.EhPart(em, j.partB) && Ecs.Pai(em, j.bloco).Index == j.partA.Index && Ecs.Pai(em, j.vizinho).Index == j.partB.Index;

        static unsafe Constraint* Restricao(PhysicsJoint* pj, int idx) => idx == 0 ? &pj->m_Constraints.A : idx == 1 ? &pj->m_Constraints.B : &pj->m_Constraints.C;
        static int AcharMotor(PhysicsJoint pj, ConstraintType t)
        {
            var cs = pj.m_Constraints;
            return cs.Length > 0 && cs.A.Type == t ? 0 : cs.Length > 1 && cs.B.Type == t ? 1 : cs.Length > 2 && cs.C.Type == t ? 2 : -1;
        }

        static void LogSinal(EntityManager em, Junta j, bool tem, float sinal, string motivo, float dt)
        {
            if (!LogDetalhe) return;
            if (j.sinalLogado == -999f) { Registro.Info(j.nome + " " + j.bloco.Index + ": port -> " + Ecs.SinalDiagnostico(em, j.bloco)); j.sinalLogado = tem ? sinal : -998f; }
            if (!tem && motivo != j.sinalMotivo) { j.sinalMotivo = motivo; Registro.Aviso(j.nome + " " + j.bloco.Index + ": no signal (" + motivo + ")"); }
            j.tSinalLog += dt;
            if (tem && j.tSinalLog >= 0.5f && System.Math.Abs(sinal - j.sinalLogado) >= 0.05f) { j.tSinalLog = 0f; j.sinalLogado = sinal; Registro.Info(j.nome + " " + j.bloco.Index + ": signal=" + sinal.ToString("F3") + " target=" + j.alvoAtual.ToString("F3")); }
        }

        // ---------------------------------------------------------------- estado 1: espera a nave virar dois parts, cria o joint
        static void Separando(EntityManager em, Junta j)
        {
            var pa = Ecs.Pai(em, j.bloco); var pb = Ecs.Pai(em, j.vizinho);
            if (Ecs.EhPart(em, pa) && Ecs.EhPart(em, pb) && pa.Index != pb.Index)
            {
                RestaurarBreakForce(em, j);   // ja separou: devolve o _breakForce original
                j.partA = pa; j.partB = pb; j.falhasSeparar = 0;
                adiado = false;
                if (Criar(em, j)) { j.estado = 2; j.tEstado = 0f; Reforcar(em, j); }
                else if (adiado) { j.partA = Entity.Null; j.partB = Entity.Null; return; }   // fica em "separating" e tenta no proximo frame
                else { j.estado = 0; j.tEstado = -5f; }
                return;
            }
            if (j.tEstado > 3f)
            {
                RestaurarBreakForce(em, j);
                j.falhasSeparar++;
                bool desiste = j.falhasSeparar >= 3;   // 0.11.1: 3 tentativas e para por 2 min (cada quebra fazia o jogo refazer as parts da nave toda)
                Registro.Aviso(j.nome + " " + j.bloco.Index + ": the ship did not separate within 3 s (partA=" + pa.Index + " partB=" + pb.Index + ") — the top block must also be attached to the ship through another path; " + (desiste ? "3 attempts, pausing for 2 min" : "retrying"));
                j.estado = 0; j.tEstado = desiste ? -120f : -2f; if (desiste) j.falhasSeparar = 0;
            }
        }

        /// <summary>O bloco (bearing/pistao) fica preso na base por joints do jogo (breakForce ~3300). Multiplica o breakForce
        /// dos joints que sobraram no bloco por ReforcoBase (igual a 0.7.1). 0.7.1 multiplicava de novo a cada recriacao
        /// (66000 -> 1320000): agora lembra o valor que escreveu e nao acumula. (0.7.2 tentou reforcar os vizinhos x1000 e
        /// desligar a colisao roda/chassi: bearings ficaram bugados -> revertido na 0.7.3.)</summary>
        static readonly Dictionary<(long, long), float> escritos = new Dictionary<(long, long), float>();
        static unsafe void Reforcar(EntityManager em, Junta j)
        {
            j.vigiados.Clear(); j.strainMax = 0f; j.strainMaxIdx = -1;
            try
            {
                var joints = Ecs.Joints(em, out int n); if (joints == null) return;
                int c = 0, ja = 0; float antes = 0f, depois = 0f;
                for (int k = 0; k < n; k++)
                {
                    var sj = &joints[k];
                    if (!Igual(sj->_a, j.bloco) && !Igual(sj->_b, j.bloco)) continue;
                    if (sj->_breakForce <= 0f) continue;
                    j.vigiados.Add((k, sj->_a, sj->_b));
                    if (ReforcoBase <= 1f) continue;
                    var chave = (Ecs.K(sj->_a), Ecs.K(sj->_b));
                    if (escritos.TryGetValue(chave, out var meu) && sj->_breakForce == meu) { ja++; continue; }
                    float alvo = System.Math.Min(sj->_breakForce * ReforcoBase, ReforcoTeto);
                    if (sj->_breakForce >= alvo) continue;
                    antes = sj->_breakForce; sj->_breakForce = alvo; depois = alvo; escritos[chave] = alvo; c++;
                }
                if (LogDetalhe && (c > 0 || ja > 0)) Registro.Info(j.nome + " " + j.bloco.Index + ": base joints" + (c > 0 ? " " + c + " reinforced (" + antes.ToString("F0") + " -> " + depois.ToString("F0") + ")" : "") + (ja > 0 ? " " + ja + " already reinforced" : ""));
            }
            catch (Exception ex) { if (j.avisos++ < 5) Registro.Aviso(j.nome + " " + j.bloco.Index + ": reinforcing the base failed: " + ex.Message); }
        }

        /// <summary>Guarda o maior strain (strain/breakForce) visto nos joints em volta; loga quando o joint desfaz, pra saber o que quebrou.</summary>
        static unsafe void Vigiar(EntityManager em, Junta j)
        {
            if (j.vigiados.Count == 0) return;
            try
            {
                var joints = Ecs.Joints(em, out int n); if (joints == null) return;
                foreach (var v in j.vigiados)
                {
                    if (v.idx >= n) continue;
                    var sj = &joints[v.idx];
                    if (sj->_a.Index != v.a.Index || sj->_b.Index != v.b.Index) continue;
                    float bf = sj->_breakForce, st = sj->_currentStrain; if (bf <= 0f || st <= 0f || st > 1e8f) continue;
                    if (st / bf > (j.strainMaxBf > 0f ? j.strainMax / j.strainMaxBf : 0f)) { j.strainMax = st; j.strainMaxBf = bf; j.strainMaxIdx = v.idx; j.strainA = v.a; j.strainB = v.b; }
                }
            }
            catch (Exception) { }
        }

        static unsafe void RestaurarBreakForce(EntityManager em, Junta j)
        {
            if (j.quebrados.Count == 0) return;
            try
            {
                var joints = Ecs.Joints(em, out int n);
                int ok = 0;
                if (joints != null)
                    foreach (var q in j.quebrados)
                    {
                        if (q.idx < 0 || q.idx >= n) continue;
                        var sj = &joints[q.idx];
                        if (sj->_a.Index != q.a.Index || sj->_b.Index != q.b.Index || sj->_breakForce != -1f) continue;
                        sj->_breakForce = q.breakForce; sj->_currentStrain = 0f; ok++;
                    }
                if (LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": _breakForce restored on " + ok + "/" + j.quebrados.Count + " joint(s)");
            }
            catch (Exception ex) { if (j.avisos++ < 5) Registro.Aviso(j.nome + " " + j.bloco.Index + ": restoring _breakForce failed: " + ex.Message); }
            j.quebrados.Clear();
        }

        static float3 ParaLocal(float3 v, float3 ex, float3 ey, float3 ez) => new float3 { x = Ecs.Dot(v, ex), y = Ecs.Dot(v, ey), z = Ecs.Dot(v, ez) };
        static float3 ParaMundo(float3 l, float3 ex, float3 ey, float3 ez) => Ecs.Add(Ecs.Add(Ecs.Mul(ex, l.x), Ecs.Mul(ey, l.y)), Ecs.Mul(ez, l.z));

        /// <summary>Encaixe dos dois lados, no mundo, AGORA. Lado A = face de cima do bloco (centro + 0,25 no +Y dele). Lado B =
        /// o mesmo ponto, mas preso no bloco vizinho (guardado no espaco dele na 1a vez): se o pistao esta esticado ou o
        /// bearing girado, o lado B vem deslocado/girado junto, e o joint recriado segura a pose atual.</summary>
        static TypeIndex tiGT; static bool tiGTok;
        static unsafe void Encaixes(EntityManager em, Junta j, out float3 pA, out float3 eA, out float3 qA, out float3 pB, out float3 eB, out float3 qB)
        {
            // lado A: face de cima do bloco, pela pose fisica (LocalTransform), nao pelo LocalToWorld
            Ecs.Pose(em, j.bloco, out var posA, out var rotA);
            eA = Ecs.Rot(rotA, Ecs.UY); qA = Ecs.Rot(rotA, Ecs.UX); pA = Ecs.Add(posA, Ecs.Mul(eA, 0.25f));
            Ecs.Pose(em, j.vizinho, out var posV, out var rotV); var qiV = Ecs.Inv(rotV);
            // 0.7.10: recalcula SEMPRE que der pela garagem (antes guardava por bloco vizinho: se voce mexia na nave/carro na
            // garagem, os blocos continuavam os mesmos entities e o encaixe velho puxava a roda pro lugar antigo)
            if (true)
            {
                // 0.7.9: o "zero" do lado B vem da GARAGEM (GarageTransform: posicao na grade, exata) — nao de onde as pecas
                // estao no frame em que o joint nasce
                bool ok = false;
                try
                {
                    if (!tiGTok) { tiGTok = true; tiGT = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<GarageTransform>()); }
                    if (em.HasComponent(j.bloco, ComponentType.ReadOnly(tiGT)) && em.HasComponent(j.vizinho, ComponentType.ReadOnly(tiGT)))
                    {
                        var gb = *(Ecs.TemC(em, j.bloco, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(j.bloco, tiGT) : null); var gv = *(Ecs.TemC(em, j.vizinho, tiGT) ? (RigidTransform*)em.GetComponentDataRawRO(j.vizinho, tiGT) : null);
                        var qi = Ecs.Inv(gv.rot);
                        var eyG = Ecs.Rot(gb.rot, Ecs.UY);
                        j.bLocP = Ecs.Rot(qi, Ecs.Sub(Ecs.Add(gb.pos, Ecs.Mul(eyG, 0.25f)), gv.pos));
                        j.bLocE = Ecs.Rot(qi, eyG); j.bLocQ = Ecs.Rot(qi, Ecs.Rot(gb.rot, Ecs.UX));
                        ok = true;
                    }
                }
                catch (Exception) { }
                if (!ok) { j.bLocP = Ecs.Rot(qiV, Ecs.Sub(pA, posV)); j.bLocE = Ecs.Rot(qiV, eA); j.bLocQ = Ecs.Rot(qiV, qA); }
                if (!ok && j.locViz == Ecs.K(j.vizinho)) { j.bLocP = j.gP; j.bLocE = j.gE; j.bLocQ = j.gQ; }   // sem garagem: mantem o da 1a vez
                else if (!ok) { j.gP = j.bLocP; j.gE = j.bLocE; j.gQ = j.bLocQ; }
                if (j.locViz != Ecs.K(j.vizinho) && LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": top-side fit " + (ok ? "from the garage" : "from the current pose (no GarageTransform)"));
                j.locViz = Ecs.K(j.vizinho);
            }
            pB = Ecs.Add(posV, Ecs.Rot(rotV, j.bLocP)); eB = Ecs.Norm(Ecs.Rot(rotV, j.bLocE)); qB = Ecs.Norm(Ecs.Rot(rotV, j.bLocQ));
        }

        /// <summary>O jogo refez as parts (decoupler soltou, algo quebrou, raspou...), mas o bloco e o vizinho continuam em parts
        /// diferentes: recria o joint JA, na pose atual, mantendo modo/alvo. Antes (ate 0.7.3) desfazia e passava pelo caminho
        /// lento (varios frames com a roda/topo do pistao soltos -> caia, batia, quebrava mais coisa -> cascata de explosoes).</summary>
        static bool Reconstruir(EntityManager em, Junta j)
        {
            try
            {
                if (!Ecs.Vivo(em, j.bloco) || !Ecs.Vivo(em, j.vizinho)) return false;
                var pa = Ecs.Pai(em, j.bloco); var pb = Ecs.Pai(em, j.vizinho);
                if (!Ecs.EhPart(em, pa) || !Ecs.EhPart(em, pb) || pa.Index == pb.Index) return false;
                string antes = j.partA.Index + "/" + j.partB.Index;
                try { if (Ecs.Vivo(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
                try { if (Ecs.Vivo(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
                try { if (Ecs.Vivo(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
                j.joint = Entity.Null; j.joint2 = Entity.Null; j.joint3 = Entity.Null; ApagarGuias(em, j);
                SoltarCarga(em, j, "parts rebuilt");   // Carga() recria no proximo frame se o recoupler ainda estiver travado
                j.partA = pa; j.partB = pb;
                adiado = false;
                if (!Criar(em, j, true)) return adiado;   // adiado: tenta de novo no proximo frame (Valido continua falso)
                Reforcar(em, j);
                Registro.Info(j.nome + " " + j.bloco.Index + ": parts rebuilt by the game (" + antes + " -> " + pa.Index + " " + Ecs.Massa(em, pa).ToString("F0") + " kg / " + pb.Index + " " + Ecs.Massa(em, pb).ToString("F0") + " kg), joint recreated immediately in the same pose" + (j.tipo == Tipo.Pistao ? " (current stroke " + j.alvoAtual.ToString("F2") + " m)" : " (mode " + j.modo + ")"));
                return true;
            }
            catch (Exception ex) { Registro.Aviso(j.nome + " " + j.bloco.Index + ": immediate recreation failed: " + ex.Message); return false; }
        }

        static bool adiado;
        static unsafe bool Criar(EntityManager em, Junta j, bool continuar = false)
        {
            try
            {
                Encaixes(em, j, out var pA, out var eA, out var qA, out var pB, out var eB, out var qB);
                // 0.7.10: os dois lados tem que estar juntos (bearing: mesmo ponto; pistao: no mesmo eixo). Se nao estao, e' porque
                // o jogo acabou de refazer as parts e as posicoes ainda nao batem (roda fugia do eixo depois de reacoplar ou de
                // mudar peso perto): espera ate 5 frames. Se continuar, cria na pose atual (sem puxar a roda pra longe).
                var dAB = Ecs.Sub(pB, pA); float lateral = j.tipo == Tipo.Pistao ? Ecs.Comp(Ecs.Sub(dAB, Ecs.Mul(eA, Ecs.Dot(dAB, eA)))) : Ecs.Comp(dAB);
                bool coincidir = false, puxar = false;
                float angE = (float)(System.Math.Acos(System.Math.Max(-1f, System.Math.Min(1f, Ecs.Dot(eA, eB)))) * 180.0 / System.Math.PI);
                // 0.7.12: bearing fora do eixo (ate 1 m) e' puxado devagar pro lugar certo com a roda travada, em vez de nascer torto
                // 0.7.13: so ajuste pequeno (ate 10 cm / 10 graus) e so quando nasce — na 0.7.12 puxava 44-71 cm / 50 graus
                // depois de uma quebra no meio da simulacao e o tranco espalhava a explosao
                bool podePuxar = j.tipo == Tipo.Bearing && !continuar && PartidaTempo > 0f && lateral <= 0.1f && angE <= 10f;
                if (lateral > 0.1f)
                {
                    if (j.adiamentos < 5) { j.adiamentos++; if (j.adiamentos == 1 && LogDetalhe) Registro.Info(j.nome + " " + j.bloco.Index + ": fit off by " + (lateral * 100f).ToString("F0") + " cm — waiting for the game to finish rebuilding the parts"); adiado = true; return false; }
                    { coincidir = true; Registro.Aviso(j.nome + " " + j.bloco.Index + ": fit still off by " + (lateral * 100f).ToString("F0") + " cm after 5 frames — creating in the current pose"); }
                }
                else if (podePuxar && (lateral > 0.02f || angE > 2f)) puxar = true;
                if (LogDetalhe && j.tipo == Tipo.Bearing) Registro.Info(j.nome + " " + j.bloco.Index + ": " + (continuar ? "recreating" : "spawning") + " with the wheel " + (lateral * 100f).ToString("F1") + " cm / " + angE.ToString("F1") + " degrees from the correct fit" + (puxar ? " — will pull it into place" : ""));
                j.adiamentos = 0;
                if (coincidir) { pB = pA; }
                j.eixo = eA; j.ancora = pA;
                j.fa = Ecs.Frame(em, j.partA, pA, eA, qA);
                // 0.7.4 recriava o pistao com o pivo de B deslocado (no "zero" do bloco vizinho) e alvo -curso: depois do
                // decoupler o topo saia voando/fazia curva. Agora o pivo de B nasce EM CIMA do pivo de A (igual a 1a vez, que
                // funciona) e o alvo e' relativo (cursoBase). Bearing continua com B pelo bloco vizinho (funcionou).
                j.fb = j.tipo == Tipo.Pistao ? Ecs.Frame(em, j.partB, pA, eB, qB) : Ecs.Frame(em, j.partB, pB, eB, qB);
                PhysicsJoint joint;
                if (false)   // 0.12.3: pistao do anel = pistao normal (motor + trilhos); so o led fica amarelo
                {
                    joint = PhysicsJoint.CreatePrismatic(j.fa, j.fb, new Unity.Physics.Math.FloatRange { Min = -1000f, Max = 1000f }); j.idxMotor = -1;
                    float cursoAgora = Ecs.Dot(Ecs.Sub(pB, pA), eA); j.alvoAtual = cursoAgora; j.alvo = cursoAgora; j.cursoBase = cursoAgora;
                }
                else if (j.tipo == Tipo.Pistao)
                {
                    joint = PhysicsJoint.CreatePositionMotor(j.fa, j.fb, 0f, PistaoImpulso <= 0f ? float.PositiveInfinity : PistaoImpulso);
                    j.idxMotor = AcharMotor(joint, ConstraintType.PositionMotor);
                    if (j.idxMotor < 0) { Registro.Erro(j.nome + ": the joint came without a motor"); return false; }
                    // curso atual medido na geometria (0 na 1a vez); o motor comeca segurando onde esta, nao volta pro 0
                    float cursoAgora = Ecs.Dot(Ecs.Sub(pB, pA), eA);
                    j.alvoAtual = cursoAgora; j.alvo = cursoAgora; j.cursoBase = cursoAgora;   // alvo relativo = 0: segura onde esta
                }
                else if (puxar)
                {
                    j.puxA0 = Ecs.Frame(em, j.partA, pB, eB, qB);   // comeca exatamente onde a roda esta (sem tranco)
                    var ppA = Ecs.Norm(Ecs.Sub(qB, Ecs.Mul(eA, Ecs.Dot(qB, eA))));   // nao gira a roda em volta do eixo
                    j.puxA1 = Ecs.Frame(em, j.partA, pA, eA, ppA);
                    j.tPux = 0f; j.durPux = 0.5f + System.Math.Min(1.5f, lateral * 3f + angE / 20f);
                    joint = PhysicsJoint.CreateFixed(j.puxA0, j.fb); j.idxMotor = -1;
                    if (!continuar) { j.modo = 0; j.impulsoModo = 0f; j.origemModo = "nothing connected"; j.alvo = 0f; j.alvoAtual = 0f; }
                    if (j.tPartida <= 0f) j.tPartida = System.Math.Max(0.01f, PartidaTempo);   // trava ate a fila liberar
                    tFila = 0f; tProxFila = 0f;
                }
                else if (!continuar && PartidaTempo > 0f || continuar && j.tPartida > 0f) { joint = PhysicsJoint.CreateFixed(j.fa, j.fb); j.idxMotor = -1; if (!continuar) { j.modo = 0; j.impulsoModo = 0f; j.origemModo = "nothing connected"; j.alvo = 0f; j.alvoAtual = 0f; } }
                else if (continuar && j.modo >= 0) { joint = Montar(j, j.modo, j.alvo, j.impulsoModo, out j.idxMotor); if (j.modo == 2) { j.alvoAtual = 0f; j.angServo = 999f; } if (j.modo == 3) j.modo = -1; }   // 0.12.1: travado -> refaz a trava no proximo frame
                else { joint = PhysicsJoint.CreateHinge(j.fa, j.fb); j.idxMotor = -1; j.modo = 0; j.impulsoModo = 0f; j.origemModo = "nothing connected"; j.alvo = 0f; j.alvoAtual = 0f; }
                var par = new PhysicsConstrainedBodyPair(); par.Entities = new EntityPair { EntityA = j.partA, EntityB = j.partB }; par.EnableCollision = 0;
                var tipos = new Il2CppStructArray<ComponentType>(3);
                tipos[0] = ComponentType.ReadWrite(Ecs.tiPar); tipos[1] = ComponentType.ReadWrite(Ecs.tiJoint); tipos[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                var e = em.CreateEntity(tipos);
                *(Ecs.TemC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = par;
                *(Ecs.TemC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = joint;
                j.joint = e;
                // pistao: 4 trilhos paralelos ao eixo a PistaoGuia m (prismatico = so desliza reto). Com um ponto so, qualquer
                // forca de lado fazia alavanca no topo (log: ate 18 cm fora do eixo / 6 graus)
                ApagarGuias(em, j);
                if (j.tipo == Tipo.Pistao && PistaoGuia > 0f)
                {
                    var eZ = Ecs.Norm(Ecs.Cross(eA, qA));
                    foreach (var off in new[] { Ecs.Mul(qA, PistaoGuia), Ecs.Mul(qA, -PistaoGuia), Ecs.Mul(eZ, PistaoGuia), Ecs.Mul(eZ, -PistaoGuia) })
                    {
                        var pg = Ecs.Add(pA, off);
                        var ga = Ecs.Frame(em, j.partA, pg, eA, qA); var gbf = Ecs.Frame(em, j.partB, pg, eB, qB);
                        var pr = PhysicsJoint.CreatePrismatic(ga, gbf, new Unity.Physics.Math.FloatRange { Min = -1000f, Max = 1000f });
                        var eg = em.CreateEntity(tipos);
                        *(Ecs.TemC(em, eg, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(eg, Ecs.tiPar) : null) = par;
                        *(Ecs.TemC(em, eg, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(eg, Ecs.tiJoint) : null) = pr;
                        j.guias.Add(eg);
                    }
                }
                // bearing: dois pontos extras no eixo (bola-e-soquete), um de cada lado da face, contra a roda tombar
                if (j.tipo == Tipo.Bearing && BearingApoio > 0f)
                {
                    var extras = new List<Entity>();
                    j.apA0 = new float3[2]; j.apA1 = new float3[2]; j.apB = new float3[2]; int ia = 0;
                    foreach (var sinalD in new[] { 1f, -1f })
                    {
                        var p2A = Ecs.Add(pA, Ecs.Mul(eA, sinalD * BearingApoio)); var p2B = Ecs.Add(pB, Ecs.Mul(eB, sinalD * BearingApoio));
                        var a2 = Ecs.Frame(em, j.partA, p2A, eA, qA).Position; var b2 = Ecs.Frame(em, j.partB, p2B, eB, qB).Position;
                        j.apA1[ia] = a2; j.apA0[ia] = Ecs.Frame(em, j.partA, p2B, eA, qA).Position; j.apB[ia] = b2; ia++;
                        var bs = PhysicsJoint.CreateBallAndSocket(puxar ? j.apA0[ia - 1] : a2, b2);
                        var e2 = em.CreateEntity(tipos);
                        *(Ecs.TemC(em, e2, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e2, Ecs.tiPar) : null) = par;
                        *(Ecs.TemC(em, e2, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e2, Ecs.tiJoint) : null) = bs;
                        extras.Add(e2);
                    }
                    j.joint2 = extras[0]; j.joint3 = extras[1];
                }
                if (!puxar) j.tPux = -1f;
                if (!continuar)
                {
                    if (!puxar) j.tPartida = PartidaTempo;
                    tFila = 0f; tProxFila = 0f;   // a fila espera PartidaTempo depois da ultima que nasceu
                    // a peca que acabou de soltar nasce com a velocidade da base (sem giro): o tranco da separacao nao vira rotacao
                    try
                    {
                        var va = (Ecs.TemC(em, j.partA, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(j.partA, Ecs.tiVel) : null); var vb = (Ecs.TemC(em, j.partB, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(j.partB, Ecs.tiVel) : null);
                        if (va != null && vb != null) { vb->Linear = va->Linear; vb->Angular = float3.zero; }
                    }
                    catch (Exception) { }
                }
                if (!continuar) Registro.Info(j.nome + " " + j.bloco.Index + ": joint created (" + e.Index + ") between part " + j.partA.Index + " (" + Ecs.Massa(em, j.partA).ToString("F0") + " kg) and part " + j.partB.Index + " (" + Ecs.Massa(em, j.partB).ToString("F0") + " kg)" + (j.tipo == Tipo.Bearing ? ", locked until the start queue releases it" : ", motor at index " + j.idxMotor + ", current stroke " + j.alvoAtual.ToString("F2") + " m"));
                return true;
            }
            catch (Exception ex) { Registro.Erro(j.nome + " " + j.bloco.Index + ": creating the joint failed: " + ex); return false; }
        }

        static void Desfazer(EntityManager em, Junta j, string motivo)
        {
            try { if (Ecs.Vivo(em, j.joint)) em.DestroyEntity(j.joint); } catch (Exception) { }
            try { if (Ecs.Vivo(em, j.joint2)) em.DestroyEntity(j.joint2); } catch (Exception) { }
            try { if (Ecs.Vivo(em, j.joint3)) em.DestroyEntity(j.joint3); } catch (Exception) { }
            j.joint2 = Entity.Null; j.joint3 = Entity.Null;
            if (j.estado == 2) Registro.Info(j.nome + " " + j.bloco.Index + ": joint removed (" + motivo + ")" + (j.strainMaxIdx >= 0 ? " — highest strain seen around it: " + j.strainMax.ToString("F0") + " of " + j.strainMaxBf.ToString("F0") + " (" + (100f * j.strainMax / j.strainMaxBf).ToString("F0") + "%, joint " + j.strainMaxIdx + " between " + j.strainA.Index + " e " + j.strainB.Index + ")" : ""));
            j.vigiados.Clear(); j.strainMax = 0f; j.strainMaxBf = 0f; j.strainMaxIdx = -1;
            SoltarCarga(em, j, motivo); ApagarGuias(em, j);
            j.joint = Entity.Null; j.idxMotor = -1; j.partA = Entity.Null; j.partB = Entity.Null; j.sinalLogado = -999f; j.sinalMotivo = null; j.modo = -1; j.energiaEstado = 254;
            VisualRepouso(em, j);
        }

        /// <summary>0.19.5: pistao recem-descoberto (garagem, depois de uma simulacao): se a cabeca ficou levantada, devolve haste e
        /// cabeca pro repouso. Antes isso so acontecia no Desfazer — e o EsquecerTudo da descarga nao passa por ele.</summary>
        static unsafe void RecolherNaGaragem(EntityManager em, Junta j)
        {
            try
            {
                foreach (var f in Ecs.Filhos(em, j.bloco))
                {
                    var p = Ecs.LT(em, f).Position;
                    if (System.Math.Abs(p.x) > 1e-3f || System.Math.Abs(p.z) > 1e-3f) continue;
                    if (p.y >= CabecaY - 1e-3f && p.y <= CabecaY + j.curso + 0.6f && !Ecs.TemPTM(em, f)) j.cabeca = f;
                    else if (System.Math.Abs(p.y - HasteY) < 1e-3f) j.haste = f;
                }
                // 0.19.10 (Andre: "pistao extendido no fim da simulacao -> continua extendido na garagem"): diagnostico 1x por bloco fora da
                // simulacao — quantos filhos o mod ve e onde esta a cabeca (se nao acha, o log diz por que nao recolhe)
                bool naGaragem = !Ecs.EhPart(em, Ecs.Pai(em, j.bloco));
                if (naGaragem && !j.garagemLog) { j.garagemLog = true; int nf = Ecs.Filhos(em, j.bloco).Count; Registro.Info(j.nome + " " + j.bloco.Index + ": garage check — " + nf + " child(ren) seen, head " + (Ecs.Nulo(j.cabeca) ? "NOT found" : "entity " + j.cabeca.Index + " at y=" + Ecs.LT(em, j.cabeca).Position.y.ToString("F3")) + ", rod " + (Ecs.Nulo(j.haste) ? "NOT found" : "entity " + j.haste.Index)); }
                if (Ecs.Nulo(j.cabeca)) return;
                float y = Ecs.LT(em, j.cabeca).Position.y;
                if (y > CabecaY + 1e-3f) { j.visualExt = y - CabecaY; VisualRepouso(em, j); if (!j.recolhidoLog) { j.recolhidoLog = true; Registro.Info(j.nome + " " + j.bloco.Index + ": rod/head were left extended (" + (y - CabecaY).ToString("F2") + " m) — back to rest" + (naGaragem ? " (in the garage)" : "")); } }
            }
            catch (Exception) { }
        }
        /// <summary>0.11.0: cabeca e haste do pistao de volta no repouso (o bloco vai voltar a ser um so / garagem).</summary>
        static unsafe void VisualRepouso(EntityManager em, Junta j)
        {
            try
            {
                if (j.tipo != Tipo.Pistao || j.visualExt <= 0f) return;
                if (Ecs.Vivo(em, j.cabeca)) { var lt = (Ecs.TemC(em, j.cabeca, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(j.cabeca, Ecs.tiLT) : null); if (lt != null) lt->Position = new float3 { x = 0f, y = CabecaY, z = 0f }; }
                if (Ecs.Vivo(em, j.haste) && Ecs.TemPTM(em, j.haste)) Ecs.EscalaY(em, j.haste, HasteRepouso);
            }
            catch (Exception) { }
            j.visualExt = -1f;
        }

        /// <summary>01/10: depois da descarga do mundo, esquece tudo SEM tocar nas entities (ja nao existem) — a proxima varredura refaz.</summary>
        public static int Quantas() => juntas.Count;   // 0.19.12
        public static void EsquecerTudo()
        {
            juntas.Clear(); motores.Clear(); motorPorSaida.Clear(); ignorados.Clear(); semCol.Clear(); religadas.Clear(); tReligou = -1f;
            try { Acoplador.Esquecer(); } catch (Exception) { }
            try { Radio.Esquecer(); } catch (Exception) { }
            try { Alavancas.Esquecer(); } catch (Exception) { }
            try { EsquecerTorres(); } catch (Exception) { }
            Registro.Info("after world unload: mod state cleared (joints, motors, beacons, radios, levers, turrets)");
        }
        public static void Limpar(EntityManager em)
        {
            foreach (var j in juntas.Values) Desfazer(em, j, "cleaning up");
            LimparTorres(em);
            foreach (var c in semCol) { try { if (Ecs.Vivo(em, c.e)) em.DestroyEntity(c.e); } catch (Exception) { } }
            semCol.Clear(); religadas.Clear(); tReligou = -1f;
            Acoplador.Limpar(em);
            juntas.Clear(); motores.Clear(); motorPorSaida.Clear(); ignorados.Clear();
        }
    }
}
