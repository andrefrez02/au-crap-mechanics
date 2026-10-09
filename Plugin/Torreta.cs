using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Mecanica
{
    /// <summary>0.12.8: o mod Replay le isto por reflexao — entities cujo DESENHO a Mecanica girou neste frame (turret seat): o Replay grava a pose
    /// visual (LocalToWorld) so delas. (A 0.3.5 do Replay comparava todo filho e gravava lixo nos que andam com a fisica.)</summary>
    public static class PonteReplay { public static readonly HashSet<long> Girados = new HashSet<long>(); }

    /// <summary>Um pedaco colado nas placas de um Turret Seat: vira uma part propria presa por bola-e-soquete no pivo, e o mod
    /// gira ela junto com a mira (servo por velocidade angular, inercia infinita = nao pende com peso).</summary>
    internal class TorreMov
    {
        public Entity viz, part, joint;
        public quaternion Rn0 = quaternion.identity;   // rotacao do bloco colado relativa ao turret (garagem)
        public float3 inerciaOriginal; public bool inerciaGuardada;
        public bool elo; public string faces = "";   // 0.12.11: estrutura presa em 2+ faces do turret (placa+placa, placa+base...) = ELO
    }

    /// <summary>Turret Seat (0.12.1): o BLOCO fica preso na nave (base e saida paradas — cabos nao quebram). O que gira: (1) o desenho
    /// (seat do jogo, anel, placas, gatilhos) — o mod gira os LocalTransform dos renderers filhos em volta do pivo logo antes do jogo
    /// calcular os LocalToWorld e desfaz logo depois (o jogo continua vendo tudo no lugar); (2) o que esta colado nas placas — pecas de
    /// fisica separadas, presas no pivo, que seguem a mira.</summary>
    internal class Torre
    {
        public Entity bloco, partBase;
        public int estado;            // 0 esperando lancamento, 1 separando, 2 ativo, 3 soldado (algo nas placas tambem preso na nave)
        public float tEstado; public int avisos, falhasSeparar; public bool avisouPreso, alturaLogada;
        public List<(int idx, Entity a, Entity b, float breakForce)> quebrados = new List<(int, Entity, Entity, float)>();
        public List<Entity> vizPlacas = new List<Entity>();
        public Dictionary<long, string> faceDe = new Dictionary<long, string>();   // 0.12.11: bloco vizinho -> face do turret onde ele cola
        public bool naveToda; public float3 cima0, frente0;   // 0.12.11: turret + estrutura sao uma coisa so -> a nave inteira gira pra mira
        public float rumoCont, rumoUlt; public bool rumoOk;   // 0.19.6: rumo da nave CONTINUO (sem dobrar em +-180) no modo nave inteira
        public List<TorreMov> movs = new List<TorreMov>();
        public float yaw, pitch;                            // mira (graus)
        public quaternion Qvis = quaternion.identity;       // rotacao do desenho (no espaco do bloco)
        public float tLog, errLog = 999f; public string nome = "SC_TurretSeat";
        public bool sentado;
        public Entity[] moveis; public float tMoveis = 9f;  // renderers filhos que giram
        public LocalTransform[] salvos; public bool girado;
    }

    internal static partial class Juntas
    {
        static readonly Dictionary<long, Torre> torres = new Dictionary<long, Torre>();
        public static readonly float3 TorretaPivo = new float3 { x = 0f, y = 0f, z = 0f };   // = modelos_v2.TURRET_PIVO (0.12.5: centro do bloco 2 x 2 x 1,5)
        public static readonly float3 TorretaPorta = new float3 { x = 99f, y = 99f, z = 99f };   // 0.12.2: sem saida
        // config (o plugin preenche)
        public static float TorretaSens = 0.1f, TorretaGanho = 12f, TorretaVelMax = 720f, TorretaPitchMin = -30f, TorretaPitchMax = 75f, TorretaEloAcel = 360f;
        public static bool TorretaInercia = true, TorretaCamera = true; public static Key TorretaTeclaCursor = Key.LeftCtrl;
        public static bool CursorLivre;
        public static Torre TorreSentada;
        public static EntityManager EmAtual; public static bool EmOk;
        public static bool MenuBP;   // 0.12.6: menu de blueprint aberto (MecanicaPlugin.MenuBP)
        public static string Contagens() => juntas.Count + " joints, " + motores.Count + " motors, " + torres.Count + " turrets, " + turretsVistos.Count + " turrets in world, " + Ecs.TotalFilhos + " children in world";
        static TypeIndex tiSeatSing, tiPCD, tiGT2; static EntityQuery qSeatSing, qMeu; static bool seatOk, seatFalhou, gt2Ok;
        static Entity camEnt; static float tCam = 9f; static int errosCam, errosGiro;
        public static int PatchesVistos;

        static void NovaTorre(Entity e, string nome)
        {
            torres[Ecs.K(e)] = new Torre { bloco = e, nome = nome, estado = 0 };
            Registro.Info(nome + " found: block " + e.Index + ":" + e.Version + " (turret seat)");
        }
        public static bool TemTorre(long k) => torres.ContainsKey(k);
        /// <summary>0.22.0: torres ativas (pra Anel.cs: anel na peca pendurada em cada placa).</summary>
        internal static IEnumerable<Torre> TorresAtivas() { foreach (var t in torres.Values) if (t.estado == 2 || t.estado == 3) yield return t; }

        static bool PrepararSeat(EntityManager em)
        {
            if (seatOk) return true; if (seatFalhou) return false;
            try
            {
                tiSeatSing = TypeManager.GetTypeIndex(Il2CppType.Of<SeatBindingsUpdateSystem.Singleton>());
                tiPCD = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>());
                var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiSeatSing); qSeatSing = em.CreateEntityQuery(a);
                var m = new Il2CppStructArray<ComponentType>(2); m[0] = ComponentType.ReadOnly(TypeManager.GetTypeIndex(Il2CppType.Of<MyPlayerController>())); m[1] = ComponentType.ReadOnly(tiPCD);
                qMeu = em.CreateEntityQuery(m);
                seatOk = true; return true;
            }
            catch (Exception ex) { seatFalhou = true; Registro.Erro("turret: seat/player types: " + ex.Message); return false; }
        }
        static unsafe Entity CadeiraAtual(EntityManager em)
        {
            try
            {
                if (qSeatSing.CalculateEntityCountWithoutFiltering() != 1) return Entity.Null;
                var eSing = qSeatSing.GetSingletonEntity(); var s = Ecs.TemC(em, eSing, tiSeatSing) ? (SeatBindingsUpdateSystem.Singleton*)em.GetComponentDataRawRO(eSing, tiSeatSing) : null;
                return s == null ? Entity.Null : s->_currentSeat;
            }
            catch (Exception) { return Entity.Null; }
        }
        static bool EhOBloco(EntityManager em, Entity cadeira, Entity bloco)
        {
            if (Ecs.Nulo(cadeira)) return false;
            var e = cadeira;
            for (int i = 0; i < 3 && !Ecs.Nulo(e); i++) { if (e.Index == bloco.Index && e.Version == bloco.Version) return true; e = Ecs.Pai(em, e); }
            return false;
        }

        // ================================================================ ciclo (Juntas.Passo)
        static void PassoTorres(EntityManager em, float dt)
        {
            EmAtual = em; EmOk = true;
            if (torres.Count == 0) { TorreSentada = null; PassoAjuste(em); return; }
            if (!PrepararSeat(em)) return;
            var cadeira = CadeiraAtual(em);
            Torre sentada = null;
            var mortas = new List<long>();
            foreach (var kv in torres)
            {
                var t = kv.Value;
                try
                {
                    if (!Ecs.Vivo(em, t.bloco)) { DesfazerTorre(em, t, "block is gone"); mortas.Add(kv.Key); continue; }
                    t.tEstado += dt;
                    t.sentado = EhOBloco(em, cadeira, t.bloco);
                    if (t.sentado) sentada = t;
                    if (!Ecs.EhPart(em, Ecs.Pai(em, t.bloco))) { if (t.estado != 0) { DesfazerTorre(em, t, "back to the garage"); t.estado = 0; } t.yaw = 0f; t.pitch = 0f; t.rumoOk = false; t.Qvis = quaternion.identity; continue; }
                    switch (t.estado)
                    {
                        case 0: TorreEsperando(em, t); break;
                        case 1: TorreSeparando(em, t); break;
                        case 2: TorreAtiva(em, t, dt); break;
                        case 3: if (t.tEstado >= 0f) { t.estado = 0; } break;
                    }
                }
                catch (Exception ex) { if (t.avisos++ < 5) Registro.Aviso(t.nome + " " + t.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortas) torres.Remove(k);
            if (sentada != TorreSentada)
            {
                if (sentada != null)
                {
                    // 0.12.1 (Andre): sentou = cursor some na hora e ja mira; o Ctrl alterna dali pra frente
                    CursorLivre = false;
                    try { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } catch (Exception) { }
                    Registro.Info("turret seat " + sentada.bloco.Index + ": sat down (state " + NomeTorre(sentada) + "; aiming with the mouse, " + TorretaTeclaCursor + " shows the cursor; input: " + Entrada.Modo + ")");
                }
                else Registro.Info("turret seat: stood up");
                TorreSentada = sentada;
            }
            if (sentada != null && Entrada.Apertou(TorretaTeclaCursor)) SoltarCursor(!CursorLivre);
            PassoAjuste(em);   // 0.12.6: modo ajuste (Num/) e banco invisivel (versao A)
        }
        static string NomeTorre(Torre t) => t.estado == 0 ? "waiting for launch" : t.estado == 1 ? "separating" : t.estado == 2 ? "active" : "WELDED";

        static void SoltarCursor(bool livre)
        {
            CursorLivre = livre;
            try { if (livre) { Cursor.lockState = CursorLockMode.Confined; Cursor.visible = true; } else { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } }
            catch (Exception ex) { Registro.Aviso("turret: cursor: " + ex.Message); }
            Registro.Info("turret seat: cursor " + (livre ? "FREE (aim paused)" : "hidden (aiming)"));
        }

        // ---- estado 0: lancou -> quebra os joints das PLACAS (tudo menos a base) pra o que esta colado nelas virar outra(s) part(s)
        static unsafe void TorreEsperando(EntityManager em, Torre t)
        {
            if (t.tEstado < 0f) return;
            var joints = Ecs.Joints(em, out int n);
            if (joints == null) { if (t.avisos++ < 3) Registro.Aviso("SpaceshipSingleton._joints not available"); t.tEstado = -1f; return; }
            var placas = JointsDasPlacas(em, t.bloco, joints, n, out var vizs);
            t.partBase = Ecs.Pai(em, t.bloco); t.movs.Clear(); t.quebrados.Clear(); t.vizPlacas = vizs;
            if (placas.Count == 0)
            {
                t.estado = 2; t.tEstado = 0f;
                Registro.Info(t.nome + " " + t.bloco.Index + ": active with nothing on the plates (only the seat turns)");
                return;
            }
            var pular = new HashSet<int>(placas);
            // 0.12.11 (Andre: "cada elo gira conforme o turret"): estrutura colada numa placa E na base do turret nao fica mais SOLDADA —
            // os joints da base que levam a essa mesma estrutura (sem passar pelo turret) tambem soltam, e ela gira junto com a mira.
            t.faceDe.Clear();
            foreach (var v in vizs) t.faceDe[Ecs.K(v)] = FaceDe(em, t.bloco, v);
            var baseDaEstrutura = JointsDaBaseLigados(em, t.bloco, joints, n, vizs, out var vizsBase, out int outrosBase);
            t.naveToda = false;
            if (baseDaEstrutura.Count > 0 && outrosBase == 0)
            {
                // a base do turret so esta nessa mesma estrutura: turret + estrutura sao uma coisa so (o tanque). Nada solta: a nave inteira
                // gira pra mira (so no eixo de cima, forca limitada) e o banco inclina so no desenho.
                t.naveToda = true; t.partBase = Ecs.Pai(em, t.bloco); t.movs.Clear();
                Ecs.Eixos(em, t.bloco, out _, out t.cima0, out t.frente0); t.yaw = 0f; t.pitch = 0f; t.rumoOk = false;
                t.estado = 2; t.tEstado = 0f;
                Registro.Info(t.nome + " " + t.bloco.Index + ": the same structure is on the plates and the whole base — the WHOLE SHIP turns with the aim (link, top axis only, " + Ecs.Massa(em, t.partBase).ToString("F0") + " kg)");
                return;
            }
            foreach (var k in baseDaEstrutura) { placas.Add(k); pular.Add(k); }
            foreach (var w in vizsBase) { t.faceDe[Ecs.K(w)] = "base"; if (!vizs.Contains(w)) vizs.Add(w); }
            if (baseDaEstrutura.Count > 0) Registro.Info(t.nome + " " + t.bloco.Index + ": structure attached to a plate and to the base — also releases " + baseDaEstrutura.Count + " base joint(s) (" + vizsBase.Count + " block(s)); it turns along (link)");
            foreach (var v in vizs)
            {
                if (LigadoPorOutroCaminho(em, joints, n, v, t.bloco, pular, out int viaN))
                {
                    if (!t.avisouPreso) { t.avisouPreso = true; Registro.Aviso(t.nome + " " + t.bloco.Index + ": what is attached to the plates (block " + v.Index + ") is also attached to the ship through another path (" + viaN + " joints) — it can't turn like that: stays WELDED. Attach to the plates only what should turn with them. Retrying in 30 s."); }
                    t.estado = 3; t.tEstado = -30f; return;
                }
            }
            t.avisouPreso = false;
            foreach (var k in placas) { var sj = &joints[k]; if (sj->_breakForce > 0f) t.quebrados.Add((k, sj->_a, sj->_b, sj->_breakForce)); sj->_breakForce = -1f; sj->_currentStrain = 1e9f; }
            t.estado = 1; t.tEstado = 0f;
            Registro.Info(t.nome + " " + t.bloco.Index + ": releasing what is on the plates (" + placas.Count + " joint(s), " + vizs.Count + " block(s))");
        }

        /// <summary>0.12.11: face do turret onde o vizinho cola (pelo lado dominante da posicao dele no bloco).</summary>
        static string FaceDe(EntityManager em, Entity bloco, Entity viz)
        {
            try
            {
                Ecs.Eixos(em, bloco, out var ex, out var ey, out var ez);
                var d = Ecs.Norm(Ecs.Sub(Ecs.Posicao(em, viz), Ecs.Posicao(em, bloco)));
                float dx = Ecs.Dot(d, ex), dy = Ecs.Dot(d, ey), dz = Ecs.Dot(d, ez);
                float ax = System.Math.Abs(dx), ay = System.Math.Abs(dy), az = System.Math.Abs(dz);
                if (ax >= ay && ax >= az) return dx > 0 ? "right" : "left";
                if (ay >= az) return dy > 0 ? "top" : "base";
                return dz > 0 ? "front" : "back";
            }
            catch (Exception) { return "?"; }
        }
        /// <summary>0.12.11: joints da BASE do turret cujo vizinho pertence a mesma estrutura de algum bloco das placas (ligados sem passar
        /// pelo proprio turret).</summary>
        static unsafe List<int> JointsDaBaseLigados(EntityManager em, Entity bloco, SpaceshipJoint* joints, int n, List<Entity> vizsPlacas, out List<Entity> vizsBase, out int outrosBase)
        {
            var r = new List<int>(); vizsBase = new List<Entity>(); outrosBase = 0;
            if (vizsPlacas.Count == 0) return r;
            long kb = Ecs.K(bloco);
            var viz = new Dictionary<long, List<long>>();
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k]; if (sj->_breakForce < 0f) continue;
                long ka = Ecs.K(sj->_a), kc = Ecs.K(sj->_b); if (ka == kb || kc == kb) continue;   // sem passar pelo turret
                if (!viz.TryGetValue(ka, out var la)) viz[ka] = la = new List<long>(); la.Add(kc);
                if (!viz.TryGetValue(kc, out var lc)) viz[kc] = lc = new List<long>(); lc.Add(ka);
            }
            var comp = new HashSet<long>(); var fila = new Queue<long>();
            foreach (var v in vizsPlacas) { long kv = Ecs.K(v); if (comp.Add(kv)) fila.Enqueue(kv); }
            while (fila.Count > 0) { var c = fila.Dequeue(); if (!viz.TryGetValue(c, out var ls)) continue; foreach (var x in ls) if (comp.Add(x)) fila.Enqueue(x); }
            var placasK = new HashSet<long>(); foreach (var v in vizsPlacas) placasK.Add(Ecs.K(v));
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k]; if (sj->_breakForce < 0f) continue;
                bool a = Igual(sj->_a, bloco), b = Igual(sj->_b, bloco); if (!a && !b) continue;
                var outro = a ? sj->_b : sj->_a; long ko = Ecs.K(outro);
                if (placasK.Contains(ko)) continue;   // joint de placa (ja entra)
                if (!comp.Contains(ko)) { outrosBase++; continue; }   // estrutura que so esta na base (segura o turret)
                r.Add(k); if (!vizsBase.Contains(outro)) vizsBase.Add(outro);
            }
            return r;
        }
        /// <summary>Joints do jogo entre o bloco e o que esta colado nas placas = todos, menos os da face de baixo (a base).</summary>
        static unsafe List<int> JointsDasPlacas(EntityManager em, Entity bloco, SpaceshipJoint* joints, int n, out List<Entity> vizs)
        {
            var r = new List<int>(); vizs = new List<Entity>(); var vistos = new HashSet<long>();
            Ecs.Eixos(em, bloco, out _, out var ey, out _); var pb = Ecs.Posicao(em, bloco);
            for (int k = 0; k < n; k++)
            {
                var sj = &joints[k];
                bool a = Igual(sj->_a, bloco), b = Igual(sj->_b, bloco);
                if (!a && !b) continue;
                if (sj->_breakForce < 0f) continue;
                var outro = a ? sj->_b : sj->_a;
                float3 po; try { if (!Ecs.Vivo(em, outro)) continue; po = Ecs.Posicao(em, outro); } catch (Exception) { continue; }
                var d = Ecs.Sub(po, pb); float comp = Ecs.Comp(d); if (comp < 1e-4f) continue;
                if (Ecs.Dot(d, ey) < -0.9f) continue;   // abaixo do bloco (a base cola embaixo: centro do vizinho >= 1 m abaixo)
                r.Add(k); if (vistos.Add(Ecs.K(outro))) vizs.Add(outro);
            }
            return r;
        }

        static unsafe void RestaurarBreakForceTorre(EntityManager em, Torre t)
        {
            if (t.quebrados.Count == 0) return;
            try
            {
                var joints = Ecs.Joints(em, out int n); int ok = 0;
                if (joints != null)
                    foreach (var q in t.quebrados)
                    {
                        if (q.idx < 0 || q.idx >= n) continue;
                        var sj = &joints[q.idx];
                        if (sj->_a.Index != q.a.Index || sj->_b.Index != q.b.Index || sj->_breakForce != -1f) continue;
                        sj->_breakForce = q.breakForce; sj->_currentStrain = 0f; ok++;
                    }
                if (LogDetalhe) Registro.Info(t.nome + " " + t.bloco.Index + ": _breakForce restored on " + ok + "/" + t.quebrados.Count + " joint(s)");
            }
            catch (Exception ex) { if (t.avisos++ < 5) Registro.Aviso(t.nome + " " + t.bloco.Index + ": restoring _breakForce: " + ex.Message); }
            t.quebrados.Clear();
        }

        // ---- estado 1: espera o que esta nas placas virar part(s) propria(s), prende cada uma no pivo
        static void TorreSeparando(EntityManager em, Torre t)
        {
            var pBase = Ecs.Pai(em, t.bloco);
            bool todos = true;
            foreach (var v in t.vizPlacas) { if (!Ecs.Vivo(em, v)) continue; var pv = Ecs.Pai(em, v); if (!Ecs.EhPart(em, pv) || pv.Index == pBase.Index) { todos = false; break; } }
            if (todos)
            {
                RestaurarBreakForceTorre(em, t);
                t.partBase = pBase; t.falhasSeparar = 0;
                if (CriarMovs(em, t, false)) { t.estado = 2; t.tEstado = 0f; }
                else { t.estado = 0; t.tEstado = -5f; }
                return;
            }
            if (t.tEstado > 3f)
            {
                RestaurarBreakForceTorre(em, t);
                t.falhasSeparar++; bool desiste = t.falhasSeparar >= 3;
                Registro.Aviso(t.nome + " " + t.bloco.Index + ": what is on the plates did not separate from the ship within 3 s — it must be attached to the ship through another path; " + (desiste ? "3 attempts, pausing for 2 min" : "retrying"));
                t.estado = 3; t.tEstado = desiste ? -120f : -2f; if (desiste) t.falhasSeparar = 0;
            }
        }

        static quaternion RotGaragem(EntityManager em, Entity e, out bool ok)
        {
            ok = false;
            try
            {
                if (!gt2Ok) { gt2Ok = true; tiGT2 = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>()); }
                unsafe { if (em.HasComponent(e, ComponentType.ReadOnly(tiGT2))) { ok = true; return ((Ecs.TemC(em, e, tiGT2) ? (RigidTransform*)em.GetComponentDataRawRO(e, tiGT2) : null))->rot; } }
            }
            catch (Exception) { }
            return quaternion.identity;
        }

        /// <summary>Uma TorreMov por part distinta entre os blocos das placas: bola-e-soquete no pivo com a part da nave.</summary>
        static unsafe bool CriarMovs(EntityManager em, Torre t, bool recriando)
        {
            try
            {
                foreach (var m in t.movs) { try { if (Ecs.Vivo(em, m.joint)) em.DestroyEntity(m.joint); } catch (Exception) { } DevolverInercia(em, m); }
                t.movs.Clear();
                Ecs.Pose(em, t.bloco, out var posB, out var rotB);
                var qgB = RotGaragem(em, t.bloco, out bool okB);
                var pivo = Ecs.Add(posB, Ecs.Rot(rotB, TorretaPivo));
                var porPart = new Dictionary<long, TorreMov>();
                foreach (var v in t.vizPlacas)
                {
                    if (!Ecs.Vivo(em, v)) continue;
                    var pv = Ecs.Pai(em, v); if (!Ecs.EhPart(em, pv) || pv.Index == t.partBase.Index) continue;
                    long kp = Ecs.K(pv);
                    string face = t.faceDe.TryGetValue(Ecs.K(v), out var fc) ? fc : "?";
                    if (porPart.TryGetValue(kp, out var mj)) { if (!mj.faces.Contains(face)) mj.faces += (mj.faces.Length > 0 ? "+" : "") + face; continue; }
                    var m = new TorreMov { viz = v, part = pv, faces = face };
                    var qgV = RotGaragem(em, v, out bool okV);
                    if (okB && okV) m.Rn0 = Ecs.QMul(Ecs.Inv(qgB), qgV);
                    else { Ecs.Pose(em, v, out _, out var rotV); m.Rn0 = Ecs.QMul(Ecs.QMul(Ecs.Inv(t.Qvis), Ecs.Inv(rotB)), rotV); }
                    var fa = Ecs.Frame(em, pv, pivo, Ecs.UY, Ecs.UX); var fb = Ecs.Frame(em, t.partBase, pivo, Ecs.UY, Ecs.UX);
                    var joint = PhysicsJoint.CreateBallAndSocket(fa.Position, fb.Position);
                    var par = new PhysicsConstrainedBodyPair(); par.Entities = new EntityPair { EntityA = pv, EntityB = t.partBase }; par.EnableCollision = 0;
                    var tipos = new Il2CppStructArray<ComponentType>(3);
                    tipos[0] = ComponentType.ReadWrite(Ecs.tiPar); tipos[1] = ComponentType.ReadWrite(Ecs.tiJoint); tipos[2] = ComponentType.ReadWrite(Ecs.tiPWI);
                    var e = em.CreateEntity(tipos);
                    *(Ecs.TemC(em, e, Ecs.tiPar) ? (PhysicsConstrainedBodyPair*)em.GetComponentDataRawRW(e, Ecs.tiPar) : null) = par;
                    *(Ecs.TemC(em, e, Ecs.tiJoint) ? (PhysicsJoint*)em.GetComponentDataRawRW(e, Ecs.tiJoint) : null) = joint;
                    m.joint = e;
                    try
                    {
                        var va = (Ecs.TemC(em, t.partBase, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(t.partBase, Ecs.tiVel) : null); var vb = (Ecs.TemC(em, pv, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(pv, Ecs.tiVel) : null);
                        if (va != null && vb != null && !recriando) { vb->Linear = va->Linear; vb->Angular = float3.zero; }
                    }
                    catch (Exception) { }
                    porPart[kp] = m; t.movs.Add(m);
                    Registro.Info(t.nome + " " + t.bloco.Index + ": " + (recriando ? "recreated" : "attached to the pivot") + " — part " + pv.Index + " (" + Ecs.Massa(em, pv).ToString("F0") + " kg, block " + v.Index + ") on ship " + t.partBase.Index + " (" + Ecs.Massa(em, t.partBase).ToString("F0") + " kg), joint " + e.Index + (okB && okV ? "" : " (no garage: current pose)"));
                }
                // 0.12.11 (Andre: "cada elo gira conforme o turret, so nao e' pra explodir"): a peca presa em 2+ faces e' um ELO — gira com a
                // mira como as outras, mas com a inercia normal e aceleracao limitada (inercia infinita + velocidade imposta contra o chao
                // era o que fazia o tanque sair voando quando o pneu encostava)
                foreach (var m in t.movs)
                {
                    m.elo = m.faces.Contains("+");
                    if (m.elo && !recriando) Registro.Info(t.nome + " " + t.bloco.Index + ": part " + m.part.Index + " attached at " + m.faces + " = LINK (normal inertia, acceleration up to " + TorretaEloAcel.ToString("F0") + " degrees/s2)");
                }
                return true;
            }
            catch (Exception ex) { Registro.Erro(t.nome + " " + t.bloco.Index + ": creating joints failed: " + ex); return false; }
        }

        static void DesfazerTorre(EntityManager em, Torre t, string motivo)
        {
            foreach (var m in t.movs) { try { if (Ecs.Vivo(em, m.joint)) em.DestroyEntity(m.joint); } catch (Exception) { } DevolverInercia(em, m); }
            if (t.movs.Count > 0) Registro.Info(t.nome + " " + t.bloco.Index + ": joints removed (" + motivo + ")");
            t.movs.Clear(); t.partBase = Entity.Null;
        }
        static unsafe void DevolverInercia(EntityManager em, TorreMov m)
        {
            try { if (m.inerciaGuardada && Ecs.EhPart(em, m.part)) { var pm = (Ecs.TemC(em, m.part, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRW(m.part, Ecs.tiMass) : null); if (pm != null) pm->InverseInertia = m.inerciaOriginal; } } catch (Exception) { }
            m.inerciaGuardada = false;
        }

        // ---- estado 2: mira + servo das pecas + rotacao do desenho
        static unsafe void TorreAtiva(EntityManager em, Torre t, float dt)
        {
            var pBase = Ecs.Pai(em, t.bloco);
            // o jogo refez as parts? religa quem estiver em part propria; se algum voltou pra part da nave, recomeca
            bool refazer = pBase.Index != t.partBase.Index;
            foreach (var m in t.movs)
            {
                if (!Ecs.Vivo(em, m.viz)) continue;
                var pv = Ecs.Pai(em, m.viz);
                if (!Ecs.Vivo(em, m.joint) || !Ecs.EhPart(em, m.part) || pv.Index != m.part.Index) refazer = true;
                if (pv.Index == pBase.Index) { DesfazerTorre(em, t, "what was on the plate stuck to the ship again"); t.estado = 0; t.tEstado = -1f; return; }
            }
            if (refazer) { t.partBase = pBase; if (!CriarMovs(em, t, true)) { DesfazerTorre(em, t, "recreating failed"); t.estado = 0; t.tEstado = -1f; return; } }
            // mira
            if (t.sentado && !CursorLivre)
            {
                bool travado = true; try { travado = Cursor.lockState == CursorLockMode.Locked; } catch (Exception) { }
                if (travado)
                {
                    var d = Entrada.MouseDelta();
                    // 0.12.6: passando de 90 graus pra cima a visao fica de ponta-cabeca -> o mouse de lado inverte pra continuar 'natural'
                    float sx = System.Math.Abs(t.pitch) > 90f ? -1f : 1f;
                    t.yaw += sx * d.x * TorretaSens; t.pitch += d.y * TorretaSens;
                    // 0.19.6 (Andre: "se eu viro muito pra direita ele comeca a virar pra esquerda"): no modo nave inteira a mira NAO dobra em
                    // +-180 (log: mira 159 -> -75 e a nave em 97 ia pelo caminho "mais curto", -172 = esquerda). Mira e rumo sao continuos.
                    if (!t.naveToda) { if (t.yaw > 180f) t.yaw -= 360f; else if (t.yaw < -180f) t.yaw += 360f; }
                    if (t.pitch > TorretaPitchMax) t.pitch = TorretaPitchMax; else if (t.pitch < TorretaPitchMin) t.pitch = TorretaPitchMin;
                }
            }
            if (t.naveToda) { NaveTodaGira(em, t, dt); return; }
            var Qalvo = Ecs.QMul(QuatEixo(Ecs.UY, t.yaw * (float)(System.Math.PI / 180.0)), QuatEixo(Ecs.UX, -t.pitch * (float)(System.Math.PI / 180.0)));
            Ecs.Pose(em, t.bloco, out _, out var rotB);
            var alvoMundo = Ecs.QMul(rotB, Qalvo);
            // velocidade angular da nave (no mundo), pra somar: a torreta gira em relacao a nave
            float3 wNave = float3.zero;
            try
            {
                var ltN = (Ecs.TemC(em, t.partBase, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(t.partBase, Ecs.tiLT) : null); var pmN = (Ecs.TemC(em, t.partBase, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(t.partBase, Ecs.tiMass) : null); var pvN = (Ecs.TemC(em, t.partBase, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRO(t.partBase, Ecs.tiVel) : null);
                if (ltN != null && pmN != null && pvN != null) wNave = Ecs.Rot(Ecs.QMul(ltN->Rotation, pmN->Transform.rot), pvN->Angular);
            }
            catch (Exception) { }
            float erroMax = 0f; bool primeiro = true;
            foreach (var m in t.movs)
            {
                if (!Ecs.Vivo(em, m.viz) || !Ecs.EhPart(em, m.part)) continue;
                var pm = (Ecs.TemC(em, m.part, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRW(m.part, Ecs.tiMass) : null);
                if (pm != null && TorretaInercia && !m.elo) { if (!m.inerciaGuardada) { m.inerciaOriginal = pm->InverseInertia; m.inerciaGuardada = true; } pm->InverseInertia = float3.zero; }
                if (m.elo && m.inerciaGuardada) DevolverInercia(em, m);
                Ecs.Pose(em, m.viz, out _, out var rotV);
                var alvoV = Ecs.QMul(alvoMundo, m.Rn0);
                var qe = Ecs.QMul(alvoV, Ecs.Inv(rotV));
                if (qe.value.w < 0f) { qe.value.x = -qe.value.x; qe.value.y = -qe.value.y; qe.value.z = -qe.value.z; qe.value.w = -qe.value.w; }
                float w = System.Math.Min(1f, qe.value.w); float ang = 2f * (float)System.Math.Acos(w); float s = (float)System.Math.Sqrt(System.Math.Max(0f, 1f - w * w));
                float3 eixo = s < 1e-6f ? Ecs.UY : new float3 { x = qe.value.x / s, y = qe.value.y / s, z = qe.value.z / s };
                float vel = System.Math.Min(TorretaVelMax * (float)(System.Math.PI / 180.0), TorretaGanho * ang);
                var omega = Ecs.Add(wNave, Ecs.Mul(eixo, vel));
                var lt = (Ecs.TemC(em, m.part, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(m.part, Ecs.tiLT) : null);
                var pv = (Ecs.TemC(em, m.part, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(m.part, Ecs.tiVel) : null);
                if (lt != null && pv != null && pm != null)
                {
                    var qMassa = Ecs.QMul(lt->Rotation, pm->Transform.rot);
                    if (m.elo)
                    {
                        // elo: muda a velocidade angular aos poucos (aceleracao limitada) — o chao/rodas podem empurrar sem virar explosao
                        var atual = Ecs.Rot(qMassa, pv->Angular); var dif = Ecs.Sub(omega, atual);
                        float lim = TorretaEloAcel * (float)(System.Math.PI / 180.0) * dt, cd = Ecs.Comp(dif);
                        if (cd > lim && cd > 1e-6f) dif = Ecs.Mul(dif, lim / cd);
                        omega = Ecs.Add(atual, dif);
                    }
                    pv->Angular = Ecs.Rot(Ecs.Inv(qMassa), omega);
                }
                erroMax = System.Math.Max(erroMax, ang);
                // o desenho segue a pose REAL da primeira peca (fica junto com o que esta colado)
                if (primeiro) { primeiro = false; t.Qvis = Ecs.QMul(Ecs.QMul(Ecs.Inv(rotB), rotV), Ecs.Inv(m.Rn0)); }
            }
            if (primeiro) t.Qvis = Qalvo;   // nada nas placas: o desenho vai direto pra mira
            t.tLog += dt;
            if (LogDetalhe && t.tLog >= 0.5f && System.Math.Abs(erroMax * 57.2958f - t.errLog) >= 5f)
            {
                t.tLog = 0f; t.errLog = erroMax * 57.2958f;
                Registro.Info(t.nome + " " + t.bloco.Index + ": aim yaw " + t.yaw.ToString("F0") + " pitch " + t.pitch.ToString("F0") + ", error " + t.errLog.ToString("F1") + " degrees (" + t.movs.Count + " part(s))");
            }
        }

        /// <summary>0.12.11: turret + estrutura sao uma coisa so (base e placas na mesma estrutura) — a NAVE INTEIRA vira pra mira (yaw, no eixo
        /// "top" de quando ligou), com aceleracao limitada; o banco inclina (pitch) so no desenho. A visao segue a nave de verdade.</summary>
        static unsafe void NaveTodaGira(EntityManager em, Torre t, float dt)
        {
            var nave = Ecs.Pai(em, t.bloco); if (!Ecs.EhPart(em, nave)) return;
            t.partBase = nave;
            Ecs.Eixos(em, t.bloco, out _, out _, out var ez);
            var up = Ecs.Norm(t.cima0); var f0 = Ecs.Norm(t.frente0); var r0 = Ecs.Norm(Ecs.Cross(up, f0));
            var fh = Ecs.Sub(ez, Ecs.Mul(up, Ecs.Dot(ez, up)));
            float atualG = (float)(System.Math.Atan2(Ecs.Dot(fh, r0), Ecs.Dot(fh, f0)) * 180.0 / System.Math.PI);
            if (!t.rumoOk) { t.rumoOk = true; t.rumoCont = atualG; t.rumoUlt = atualG; }
            else { float dR = atualG - t.rumoUlt; while (dR > 180f) dR -= 360f; while (dR < -180f) dR += 360f; t.rumoCont += dR; t.rumoUlt = atualG; }
            float erro = t.yaw - t.rumoCont;   // continuo: girou 300 graus pra direita = a nave da 300 graus pra direita
            if (erro > 720f || erro < -720f) { t.yaw = t.rumoCont; erro = 0f; }   // (seguranca: nunca acumula voltas inteiras)
            float velAlvo = System.Math.Max(-TorretaVelMax, System.Math.Min(TorretaVelMax, TorretaGanho * erro)) * (float)(System.Math.PI / 180.0);
            try
            {
                var lt = (Ecs.TemC(em, nave, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(nave, Ecs.tiLT) : null);
                var pm = (Ecs.TemC(em, nave, Ecs.tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(nave, Ecs.tiMass) : null);
                var pv = (Ecs.TemC(em, nave, Ecs.tiVel) ? (PhysicsVelocity*)em.GetComponentDataRawRW(nave, Ecs.tiVel) : null);
                if (lt != null && pm != null && pv != null)
                {
                    var qMassa = Ecs.QMul(lt->Rotation, pm->Transform.rot);
                    var w = Ecs.Rot(qMassa, pv->Angular); float wUp = Ecs.Dot(w, up);
                    float lim = TorretaEloAcel * (float)(System.Math.PI / 180.0) * dt;
                    float novo = wUp + System.Math.Max(-lim, System.Math.Min(lim, velAlvo - wUp));
                    w = Ecs.Add(w, Ecs.Mul(up, novo - wUp));
                    pv->Angular = Ecs.Rot(Ecs.Inv(qMassa), w);
                }
            }
            catch (Exception ex) { if (t.avisos++ < 3) Registro.Aviso(t.nome + " " + t.bloco.Index + ": whole ship turning: " + ex.Message); }
            t.Qvis = QuatEixo(Ecs.UX, -t.pitch * (float)(System.Math.PI / 180.0));   // so a inclinacao, no desenho (o yaw e' a nave de verdade)
            t.tLog += dt;
            if (LogDetalhe && t.tLog >= 0.5f && System.Math.Abs(erro - t.errLog) >= 5f) { t.tLog = 0f; t.errLog = erro; Registro.Info(t.nome + " " + t.bloco.Index + ": whole ship — aim yaw " + t.yaw.ToString("F0") + ", ship at " + t.rumoCont.ToString("F0") + " (error " + erro.ToString("F0") + " degrees)"); }
        }
        static quaternion QuatEixo(float3 eixo, float rad)
        {
            float s = (float)System.Math.Sin(rad / 2), c = (float)System.Math.Cos(rad / 2);
            return new quaternion { value = new float4 { x = eixo.x * s, y = eixo.y * s, z = eixo.z * s, w = c } };
        }

        // ================================================================ desenho que gira (prefix/postfix do TransformSystemGroup)
        /// <summary>Filhos do bloco que giram: todos menos o corpo/pintavel da base (na origem) e o plugue da saida.</summary>
        // 0.12.4: o SCTick_Seat do jogo escreve o LocalTransform do Upper (y -0,775 = altura 0 da alavanca, que esta escondida) e ignora
        // o _position do prefab -> o banco ficava afundado na base. Aqui (prefix do TransformSystemGroup) o Upper vai pra TorretaUpper
        // (= modelos_v2.TURRET_UPPER); o Upper Paintable e' filho dele e vai junto.
        public static readonly float3 TorretaUpper = new float3 { x = 0f, y = -0.85f, z = 0.02f };   // = modelos_v2.TURRET_UPPER (0.12.9: o ajuste do Andre entrou no modelo)
        public static readonly float3 TorretaPedestal = new float3 { x = 0f, y = 0.0213f, z = 0f };   // = modelos_v2.TURRET_PEDESTAL (0.12.9: chapa + haste acompanham o ajuste)
        static readonly Dictionary<long, Entity> pedestalDe = new Dictionary<long, Entity>(); static readonly Dictionary<long, float3> pedestalOff = new Dictionary<long, float3>();
        public static readonly float3 TorretaMarca = new float3 { x = 0.0137f, y = 0.0271f, z = 0.0419f };   // = modelos_v2.TURRET_MARCA
        static TypeIndex tiSCSeat; static bool scSeatOk, scSeatFalhou; static EntityQuery qSeats; static float tSeats = 99f;
        static readonly List<Entity> turretsVistos = new List<Entity>(); static readonly HashSet<long> alturaLogada = new HashSet<long>();
        /// <summary>O bloco e' um turret seat nosso? (tem o renderer "Marca" em TorretaMarca — vale na garagem, onde o SCPrefab nao aparece)</summary>
        static bool EhTurret(EntityManager em, Entity e)
        {
            foreach (var f in Ecs.Filhos(em, e)) { try { if (Ecs.Dist(Ecs.LT(em, f).Position, TorretaMarca) < 0.002f) return true; } catch (Exception) { } }
            return false;
        }
        static unsafe void AlturasDosSeats(EntityManager em)
        {
            try
            {
                if (!scSeatOk)
                {
                    if (scSeatFalhou) return;
                    try
                    {
                        tiSCSeat = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeSeat>());
                        var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiSCSeat); qSeats = em.CreateEntityQuery(a);
                        scSeatOk = true;
                    }
                    catch (Exception ex) { scSeatFalhou = true; Registro.Aviso("turret: SCTypeSeat not available (" + ex.Message + ") — the seat stays at the game's height"); return; }
                }
                tSeats += UnityEngine.Time.unscaledDeltaTime;
                if (tSeats >= 0.5f)
                {
                    tSeats = 0f; turretsVistos.Clear();
                    foreach (var e in Ecs.Lista(qSeats)) if (EhTurret(em, e)) turretsVistos.Add(e);
                }
                foreach (var e in turretsVistos)
                {
                    if (!Ecs.TemC(em, e, tiSCSeat)) continue;
                    var up = *(Entity*)em.GetComponentDataRawRO(e, tiSCSeat);   // SCTypeSeat._upperTransform
                    if (!Ecs.TemC(em, up, Ecs.tiLT)) continue;
                    var lt = (LocalTransform*)em.GetComponentDataRawRW(up, Ecs.tiLT);
                    if (alturaLogada.Add(Ecs.K(e))) Registro.Info("turret seat " + e.Index + ": seat Upper (entity " + up.Index + ") was at " + Ecs.F3(lt->Position) + " -> " + Ecs.F3(Ecs.Add(TorretaUpper, BancoOff)));
                    lt->Position = Ecs.Add(TorretaUpper, BancoOff);   // 0.12.6: + ajuste do banco (modo ajuste, Num/)
                    AplicarJogador(em, e, up, lt->Position);
                    MoverPedestal(em, e);
                }
            }
            catch (Exception ex) { if (errosAltura++ < 3) Registro.Aviso("turret: seat height: " + ex.Message); }
        }
        static int errosAltura;

        /// <summary>0.12.9 (Andre: "fica esse pezinho dentro do modelo"): a chapa e a haste sob o banco sao um renderer proprio (Pedestal)
        /// que anda junto com o ajuste do banco. Roda antes do giro do desenho (o giro salva/devolve o LocalTransform com o ajuste).</summary>
        static unsafe void MoverPedestal(EntityManager em, Entity bloco)
        {
            long kb = Ecs.K(bloco);
            pedestalOff.TryGetValue(kb, out var offAntes);
            if (!pedestalDe.TryGetValue(kb, out var ped) || !Ecs.Vivo(em, ped) || !Ecs.TemC(em, ped, Ecs.tiLT))
            {
                ped = Entity.Null;
                foreach (var f in Ecs.Filhos(em, bloco))
                {
                    if (!Ecs.TemC(em, f, Ecs.tiLT)) continue;
                    var p = Ecs.LT(em, f).Position;
                    if (Ecs.Dist(p, Ecs.Add(TorretaPedestal, offAntes)) < 0.002f || Ecs.Dist(p, TorretaPedestal) < 0.002f) { ped = f; break; }
                }
                if (Ecs.Nulo(ped)) return;   // blueprint antigo (sem Pedestal)
                pedestalDe[kb] = ped;
                if (pedestalLogado.Add(kb)) Registro.Info("turret seat " + bloco.Index + ": pedestal (entity " + ped.Index + ") follows the seat offset");
            }
            var lt = (LocalTransform*)em.GetComponentDataRawRW(ped, Ecs.tiLT); if (lt == null) return;
            lt->Position = Ecs.Add(TorretaPedestal, BancoOff);
            pedestalOff[kb] = BancoOff;
        }
        static readonly HashSet<long> pedestalLogado = new HashSet<long>();

        static unsafe void AcharMoveis(EntityManager em, Torre t)
        {
            var l = new List<Entity>();
            foreach (var f in Ecs.Filhos(em, t.bloco))
            {
                var p = Ecs.LT(em, f).Position;
                if (Ecs.Comp(p) < 0.003f) continue;                              // base (Renderer / Paintable / colisor na origem)
                if (Ecs.Dist(p, TorretaPorta) < 0.003f) continue;                // plugue da saida
                l.Add(f);
            }
            bool primeira = t.moveis == null;
            t.moveis = l.ToArray(); t.salvos = new LocalTransform[t.moveis.Length];
            if (primeira) Registro.Info(t.nome + " " + t.bloco.Index + ": " + t.moveis.Length + " renderer(s) that turn");
        }
        /// <summary>Antes do TransformSystemGroup: gira o desenho de cada turret ativo em volta do pivo (e a camera de quem esta sentado).</summary>
        public static unsafe void TorretaAntesTransform()
        {
            if (!EmOk || MenuBP) return;   // 0.12.6: menu de blueprint aberto -> nada
            var em = EmAtual;
            AlturasDosSeats(em);   // 0.12.5: todo turret do mundo (garagem tambem), antes de girar
            PonteReplay.Girados.Clear();
            if (torres.Count == 0) return;
            foreach (var t in torres.Values)
            {
                t.girado = false;
                if (t.estado != 2 || !Ecs.Vivo(em, t.bloco)) continue;
                var q = t.Qvis;
                if (System.Math.Abs(q.value.w) > 0.99999f) continue;   // no repouso
                try
                {
                    t.tMoveis += UnityEngine.Time.unscaledDeltaTime;
                    if (t.moveis == null || t.tMoveis > 5f) { t.tMoveis = 0f; AcharMoveis(em, t); }
                    if (!Ecs.Vivo(em, t.bloco)) continue;
                    for (int i = 0; i < t.moveis.Length; i++)
                    {
                        var e = t.moveis[i];
                        if (!Ecs.Vivo(em, e) || !em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiLT))) { t.salvos[i].Scale = -1f; t.tMoveis = 99f; continue; }   // 0.12.2: peca sumiu (blueprint trocado) — o crash vinha daqui
                        var lt = (Ecs.TemC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(e, Ecs.tiLT) : null); if (lt == null) { t.salvos[i].Scale = -1f; continue; }
                        t.salvos[i] = *lt;
                        var rel = Ecs.Sub(lt->Position, TorretaPivo);
                        lt->Position = Ecs.Add(TorretaPivo, Ecs.Rot(q, rel));
                        lt->Rotation = Ecs.QMul(q, lt->Rotation);
                        PonteReplay.Girados.Add(Ecs.K(e));
                    }
                    t.girado = true;
                }
                catch (Exception ex) { if (errosGiro++ < 3) Registro.Aviso("turret: turning visuals: " + ex.Message); }
            }
            PatchesVistos |= 1;
            GirarJogador(em);   // 0.12.7: modo cabine — o boneco (e a camera, filha dele) gira junto com o desenho
            TorretaCameraLT();
        }

        // ---- 0.12.7 (Andre): modo CABINE. O banco e os aros giram so no desenho (em volta do pivo) e o boneco ficava parado onde o jogo
        // poe — olhando muito pra cima o banco atravessava a camera. Agora o LocalTransform do boneco (raiz) tambem gira em volta do
        // pivo (no mundo) antes do TransformSystemGroup e volta no postfix: a camera (filha dele) vai junto, nada atravessa a visao, e
        // de fora (camera de bloco, Replay) voce aparece sentado no banco girando. Fisica e o jogo continuam vendo o boneco no lugar.
        static Entity jogGirado; static LocalTransform jogSalvo; static bool jogTemSalvo, jogPaiLogado;
        static unsafe void GirarJogador(EntityManager em)
        {
            jogTemSalvo = false;
            var t = TorreSentada;
            if (ModoBanco == 2 || t == null || !t.girado || t.estado != 2 || !Ecs.Vivo(em, t.bloco)) return;   // 0.12.8: cabine e A (no A a camera vai pro VisaoA)
            try
            {
                var l = Ecs.Lista(qMeu); if (l.Count == 0) return;
                var j = l[0];
                if (!Ecs.TemC(em, j, Ecs.tiLT)) return;
                var pai = Ecs.Pai(em, j);
                if (!Ecs.Nulo(pai)) { if (!jogPaiLogado) { jogPaiLogado = true; Registro.Info("turret seat: the seated character has a parent (entity " + pai.Index + ") — not turning it (if the parent is the seat it already turns along)"); } return; }
                Ecs.Pose(em, t.bloco, out var posB, out var rotB);
                var Rw = Ecs.QMul(Ecs.QMul(rotB, t.Qvis), Ecs.Inv(rotB));
                var Pw = Ecs.Add(posB, Ecs.Rot(rotB, TorretaPivo));
                var lt = (LocalTransform*)em.GetComponentDataRawRW(j, Ecs.tiLT);
                jogSalvo = *lt; jogGirado = j; jogTemSalvo = true;
                lt->Position = Ecs.Add(Pw, Ecs.Rot(Rw, Ecs.Sub(lt->Position, Pw)));
                lt->Rotation = Ecs.QMul(Rw, lt->Rotation);
                PatchesVistos |= 8;
            }
            catch (Exception ex) { if (errosGiro++ < 3) Registro.Aviso("turret: turning the character: " + ex.Message); }
        }
        static unsafe void DesgirarJogador(EntityManager em)
        {
            if (!jogTemSalvo) return; jogTemSalvo = false;
            try { if (Ecs.TemC(em, jogGirado, Ecs.tiLT)) *(LocalTransform*)em.GetComponentDataRawRW(jogGirado, Ecs.tiLT) = jogSalvo; } catch (Exception) { }
        }
        /// <summary>Depois do TransformSystemGroup: desfaz (o jogo — altura do seat, gatilhos — continua vendo tudo no lugar).</summary>
        public static unsafe void TorretaDepoisTransform()
        {
            if (!EmOk || torres.Count == 0) return;
            var em = EmAtual;
            DesgirarJogador(em);
            foreach (var t in torres.Values)
            {
                if (!t.girado || t.moveis == null) continue;
                t.girado = false;
                for (int i = 0; i < t.moveis.Length; i++)
                {
                    if (t.salvos[i].Scale < 0f) continue;
                    try { if (!Ecs.Vivo(em, t.moveis[i])) continue; var lt = (Ecs.TemC(em, t.moveis[i], Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(t.moveis[i], Ecs.tiLT) : null); if (lt != null) *lt = t.salvos[i]; } catch (Exception) { }
                }
            }
        }

        // ================================================================ camera: a visao vira junto com a mira
        static bool RotacaoDaVisao(EntityManager em, out quaternion r)
        {
            r = quaternion.identity;
            var t = TorreSentada;
            if (!TorretaCamera || t == null || t.estado != 2 || !Ecs.Vivo(em, t.bloco)) return false;
            try { Ecs.Pose(em, t.bloco, out _, out var rotB); r = Ecs.QMul(rotB, t.Qvis); return true; } catch (Exception) { return false; }
        }
        static unsafe Entity CameraDoJogador(EntityManager em)
        {
            try
            {
                var l = Ecs.Lista(qMeu); if (l.Count == 0) return Entity.Null;
                var b = (Ecs.TemC(em, l[0], tiPCD) ? (byte*)em.GetComponentDataRawRO(l[0], tiPCD) : null); if (b == null) return Entity.Null;
                return *(Entity*)(b + 0xE8);   // PlayerControllerData._cameraEntity
            }
            catch (Exception) { return Entity.Null; }
        }
        static bool Camera(EntityManager em, out Entity cam)
        {
            tCam += UnityEngine.Time.unscaledDeltaTime;
            if (Ecs.Nulo(camEnt) || !Ecs.Vivo(em, camEnt) || tCam > 2f) { tCam = 0f; camEnt = CameraDoJogador(em); }
            cam = camEnt; return !Ecs.Nulo(cam) && Ecs.Vivo(em, cam);
        }
        public static unsafe void TorretaCameraLT()
        {
            if (!EmOk || TorreSentada == null) return;   // 0.12.2: com o cursor livre a visao continua a do seat
            var em = EmAtual;
            try
            {
                if (!RotacaoDaVisao(em, out var r) || !Camera(em, out var cam) || !em.HasComponent(cam, ComponentType.ReadOnly(Ecs.tiLT))) return;
                var rp = quaternion.identity; var e = Ecs.Pai(em, cam);
                for (int i = 0; i < 8 && !Ecs.Nulo(e) && Ecs.Vivo(em, e) && em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiLT)); i++)
                {
                    rp = Ecs.QMul(((Ecs.TemC(em, e, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(e, Ecs.tiLT) : null))->Rotation, rp);
                    e = Ecs.Pai(em, e);
                }
                var lt = (Ecs.TemC(em, cam, Ecs.tiLT) ? (LocalTransform*)em.GetComponentDataRawRW(cam, Ecs.tiLT) : null);
                if (lt != null) { lt->Rotation = Ecs.QMul(Ecs.Inv(rp), r); PatchesVistos |= 4; }
            }
            catch (Exception ex) { if (errosCam++ < 3) Registro.Aviso("turret: camera (LocalTransform): " + ex.Message); }
        }
        static bool visaoLogada;
        public static unsafe void TorretaCameraLTW(bool doPrefix)
        {
            if (!EmOk || TorreSentada == null) return;   // 0.12.2: com o cursor livre a visao continua a do seat
            var em = EmAtual;
            try
            {
                if (!RotacaoDaVisao(em, out var r) || !Camera(em, out var cam) || !em.HasComponent(cam, ComponentType.ReadOnly(Ecs.tiLTW))) return;
                var p = (Ecs.TemC(em, cam, Ecs.tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRW(cam, Ecs.tiLTW) : null); if (p == null) return;
                var m = p->Value;
                var cx = Ecs.Rot(r, Ecs.UX); var cy = Ecs.Rot(r, Ecs.UY); var cz = Ecs.Rot(r, Ecs.UZ);
                m.c0 = new float4 { x = cx.x, y = cx.y, z = cx.z, w = 0f }; m.c1 = new float4 { x = cy.x, y = cy.y, z = cy.z, w = 0f }; m.c2 = new float4 { x = cz.x, y = cz.y, z = cz.z, w = 0f };
                // 0.12.8 (Andre): no modo A a visao fica num ponto fixo do bloco (VisaoA, perto do centro de giro: nenhum aro/cubo gira por ela)
                var t = TorreSentada;
                if (t != null && Ecs.Vivo(em, t.bloco))
                {
                    Ecs.Pose(em, t.bloco, out var posB, out var rotB);
                    if (!visaoLogada) { visaoLogada = true; var rel = Ecs.Rot(Ecs.Inv(rotB), Ecs.Sub(new float3 { x = m.c3.x, y = m.c3.y, z = m.c3.z }, posB)); Registro.Info("turret seat " + t.bloco.Index + ": game view (seated) in block = " + Ecs.F3(rel) + "; mode A uses " + Ecs.F3(VisaoA)); }
                    if (ModoBanco == 1)
                    {
                        // 0.12.9: a visao gira junto com os aros (fica sempre no mesmo lugar do "cockpit") — assim pode ficar mais longe do
                        // centro (mais alta e mais pra tras) sem nenhum aro passar por ela; visao_a_gira = false volta ao ponto fixo da 0.12.8
                        var rel = VisaoGira ? Ecs.Add(TorretaPivo, Ecs.Rot(t.Qvis, Ecs.Sub(VisaoA, TorretaPivo))) : VisaoA;
                        var v = Ecs.Add(posB, Ecs.Rot(rotB, rel)); m.c3 = new float4 { x = v.x, y = v.y, z = v.z, w = 1f };
                    }
                }
                p->Value = m;
                if (doPrefix) PatchesVistos |= 2;
            }
            catch (Exception ex) { if (errosCam++ < 3) Registro.Aviso("turret: camera (LocalToWorld): " + ex.Message); }
        }

        // ================================================================ 0.13.2: teclas bindadas do banco no turret seat
        /// <summary>Andre: "quando eu viro de costas com o turret seat nao da pra usar as acoes da nave/carro". O SeatBindingsUpdateSystem
        /// (no CustomRenderPipelineGroup) guarda em cada BindingActionable._distance (+88) a distancia AO QUADRADO da camera ate a peca, e
        /// IsInRange = ate 4,5 (~2,1 m). No turret a visao gira em volta do pivo (view_position_a fica 0,46 m atras do centro), entao de
        /// costas o olho anda ~1 m e as pecas do carro saem do alcance. Aqui (postfix do grupo) a distancia e' medida de um ponto FIXO do
        /// bloco: onde fica a visao com o turret olhando pra frente — o alcance nao muda com a mira. OlhoFixoAtivo avisa o 3P View (que
        /// mede da cabeca no F5) pra deixar isso com a Mecanica.</summary>
        /// <summary>OlhoFixoAtivo = a Mecanica reescreveu as distancias no ultimo frame (o 3P View entao nao mexe). Ponto de medida: o
        /// MAIS PERTO entre a visao do jogo sentado (antes da Mecanica mover a camera pro view_position_a) e a visao do turret olhando pra
        /// frente — alcance nunca menor que o do banco normal, e nao muda com a mira.</summary>
        public static bool OlhoFixoAtivo; public static float3 OlhoFixo, OlhoJogo; static int frameOlhoJogo = -1; public static float AlcanceBinds = 4.5f;
        static int errosBinds; static bool logouBinds; static readonly HashSet<string> motivosLogados = new HashSet<string>();
        static void Motivo(string m) { if (motivosLogados.Add(m)) Registro.Info("turret seat: seat key bindings not rewritten (" + m + ")"); }
        /// <summary>Postfix do TransformSystemGroup: o LocalToWorld da camera acabou de ser calculado pelo jogo (ainda sem a visao do turret).</summary>
        public static unsafe void CapturarOlhoJogo()
        {
            if (!EmOk || TorreSentada == null) return;
            try
            {
                var em = EmAtual; if (!Camera(em, out var cam) || !Ecs.TemC(em, cam, Ecs.tiLTW)) return;
                var p = (LocalToWorld*)em.GetComponentDataRawRO(cam, Ecs.tiLTW); if (p == null) return;
                OlhoJogo = new float3 { x = p->Value.c3.x, y = p->Value.c3.y, z = p->Value.c3.z }; frameOlhoJogo = UnityEngine.Time.frameCount;
            }
            catch (Exception) { }
        }
        public static unsafe void DistanciaBindsTorre()
        {
            bool escreveu = false;
            try
            {
                var t = TorreSentada; if (!EmOk || t == null || t.estado != 2) return;
                var em = EmAtual;
                if (!Ecs.Vivo(em, t.bloco)) return;
                if (!RotacaoDaVisao(em, out _)) { Motivo("turret view off: the game measures from its own view"); return; }
                Ecs.Pose(em, t.bloco, out var posB, out var rotB);
                OlhoFixo = Ecs.Add(posB, Ecs.Rot(rotB, VisaoA));
                bool temJogo = frameOlhoJogo == UnityEngine.Time.frameCount;
                if (!PrepararSeat(em)) { Motivo("seat types"); return; }
                if (qSeatSing.CalculateEntityCountWithoutFiltering() != 1) { Motivo("no bindings singleton"); return; }
                var eSing = qSeatSing.GetSingletonEntity(); if (!Ecs.TemC(em, eSing, tiSeatSing)) { Motivo("singleton without data"); return; }
                var b = (byte*)em.GetComponentDataRawRW(eSing, tiSeatSing); if (b == null) { Motivo("null singleton"); return; }
                var lista = *(byte**)b; if (lista == null) { Motivo("no list"); return; }
                var ptr = *(byte**)lista; int n = *(int*)(lista + 8);
                if (ptr == null || n <= 0 || n > 4096) { Motivo("this seat has no bound parts (" + n + ")"); return; }
                int dentroF = 0, dentroJ = 0, dentro = 0; float minF = 1e9f, minJ = 1e9f; var detalhe = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    var el = ptr + i * 96; var e = *(Entity*)(el + 64);
                    if (!Ecs.Vivo(em, e) || !Ecs.TemC(em, e, Ecs.tiLTW)) continue;
                    var ltw = (LocalToWorld*)em.GetComponentDataRawRO(e, Ecs.tiLTW); if (ltw == null) continue;
                    var q = ltw->Value.c3;
                    float fx = q.x - OlhoFixo.x, fy = q.y - OlhoFixo.y, fz = q.z - OlhoFixo.z; float dF = fx * fx + fy * fy + fz * fz;
                    float d2 = dF;
                    if (temJogo) { float jx = q.x - OlhoJogo.x, jy = q.y - OlhoJogo.y, jz = q.z - OlhoJogo.z; float dJ = jx * jx + jy * jy + jz * jz; if (dJ < d2) d2 = dJ; if (dJ <= 4.5f) dentroJ++; if (dJ < minJ) minJ = dJ; }
                    // 0.13.2: o jogo aceita ate 4,5 no quadrado (~2,1 m); aqui ate AlcanceBinds (padrao 4,5 m = distancia do clique pra bindar):
                    // escala a distancia ao quadrado (mantem a ordem) pra que "dentro" = ate AlcanceBinds
                    float esc = 4.5f / (AlcanceBinds * AlcanceBinds);
                    *(float*)(el + 88) = d2 * esc;
                    if (dF <= 4.5f) dentroF++; if (dF < minF) minF = dF; if (d2 * esc <= 4.5f) dentro++;
                    if (!logouBinds) detalhe.Append(" #").Append(i).Append(' ').Append(System.Math.Sqrt(d2).ToString("F2")).Append('m');
                }
                escreveu = true;
                if (!logouBinds) { logouBinds = true; Registro.Info("turret seat: seat key bindings in reach " + dentro + "/" + n + " with bindings_reach (from the game's seated view " + (temJogo ? dentroJ + ", closest " + System.Math.Sqrt(minJ).ToString("F2") + " m" : "unknown") + "; from the turret's forward view " + dentroF + ", closest " + System.Math.Sqrt(minF).ToString("F2") + " m; the game's reach is 2.12 m, this mod's " + AlcanceBinds.ToString("F2") + " m) — nearest distance per bound part:" + detalhe); }
            }
            catch (Exception ex) { if (errosBinds++ < 3) Registro.Aviso("turret seat: seat key bindings: " + ex.Message); }
            finally { OlhoFixoAtivo = escreveu; }
        }

        static string EstadoTorres()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in torres.Values)
            {
                sb.Append("  ").Append(t.nome).Append(' ').Append(t.bloco.Index).Append(": ").Append(NomeTorre(t));
                if (t.estado == 2) sb.Append(", yaw ").Append(t.yaw.ToString("F0")).Append(" pitch ").Append(t.pitch.ToString("F0")).Append(t.sentado ? " (seated)" : "").Append(", ").Append(t.movs.Count).Append(" part(s) on the plates, error ").Append(t.errLog.ToString("F1"));
                sb.Append('\n');
            }
            return sb.ToString();
        }
        static IEnumerable<(Entity a, Entity b)> LigacoesTorres()
        {
            foreach (var t in torres.Values) if (t.estado == 2) foreach (var m in t.movs) if (!Ecs.Nulo(m.part) && !Ecs.Nulo(t.partBase)) yield return (m.part, t.partBase);
        }
        static void RebobinarTorres(EntityManager em)
        {
            foreach (var t in torres.Values)
            {
                if (t.estado == 1) { t.quebrados.Clear(); t.estado = 0; t.tEstado = 0f; continue; }
                if (t.estado != 2 || t.movs.Count == 0) continue;
                t.partBase = Ecs.Pai(em, t.bloco);
                if (!CriarMovs(em, t, true)) { DesfazerTorre(em, t, "time rewind"); t.estado = 0; t.tEstado = 0f; }
            }
        }
        static void EsquecerTorres() { torres.Clear(); TorreSentada = null; }
        static void LimparTorres(EntityManager em)
        {
            foreach (var t in torres.Values) DesfazerTorre(em, t, "cleaning up");
            torres.Clear(); TorreSentada = null;
        }
    }
}
