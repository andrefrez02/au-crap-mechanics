using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;
using Unity.Physics;

namespace Mecanica
{
    /// <summary>Acesso cru ao ECS do jogo (sem generics do interop, que nem sempre existem compilados) + matematica minima.</summary>
    internal static class Ecs
    {
        public static TypeIndex tiSPData, tiVel, tiMass, tiLT, tiLTW, tiParent, tiPWI, tiPar, tiJoint, tiDecJ, tiDecTipo, tiPortRef, tiPort, tiSing, tiPrefab, tiPower, tiFramePortas, tiWireless, tiCabo;
        public static TypeIndex tiPTM; public static bool ptmOk;   // 0.11.0: PostTransformMatrix (escala nao uniforme dos renderers)
        public static TypeIndex tiGuid; static bool guidOk;         // 0.11.1: SCGuid (id do bloco que o jogo salva no mundo/blueprint)
        public static bool Ok = false, Falhou = false;
        static EntityQuery qParts, qBlocos, qSing, qComps, qWireless, qFilhos;

        public static bool Preparar(EntityManager em)
        {
            if (Ok) return true; if (Falhou) return false;
            try
            {
                tiSPData = TypeManager.GetTypeIndex(Il2CppType.Of<SPData>());
                tiVel = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsVelocity>());
                tiMass = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsMass>());
                tiLT = TypeManager.GetTypeIndex(Il2CppType.Of<LocalTransform>());
                tiLTW = TypeManager.GetTypeIndex(Il2CppType.Of<LocalToWorld>());
                tiParent = TypeManager.GetTypeIndex(Il2CppType.Of<Parent>());
                tiPWI = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsWorldIndex>());
                tiPar = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsConstrainedBodyPair>());
                tiJoint = TypeManager.GetTypeIndex(Il2CppType.Of<PhysicsJoint>());
                tiDecJ = TypeManager.GetTypeIndex(Il2CppType.Of<SCDecouplingJoint>());
                tiDecTipo = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeDecoupler>());
                tiPortRef = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPortRef>());
                tiPort = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipElectricPort>());
                tiSing = TypeManager.GetTypeIndex(Il2CppType.Of<SpaceshipSingleton>());
                tiPrefab = TypeManager.GetTypeIndex(Il2CppType.Of<SCPrefab>());
                tiPower = TypeManager.GetTypeIndex(Il2CppType.Of<SCElectricPower>());
                tiFramePortas = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeFrameWithPorts>());
                try { tiCabo = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCableCell>()); } catch (Exception ex) { Registro.Aviso("SCTypeCableCell: " + ex.Message); }   // 0.19.10
                tiWireless = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>());
                var w = new Il2CppStructArray<ComponentType>(1); w[0] = ComponentType.ReadOnly(tiWireless);
                qWireless = em.CreateEntityQuery(w);
                var a = new Il2CppStructArray<ComponentType>(4); a[0] = ComponentType.ReadOnly(tiSPData); a[1] = ComponentType.ReadOnly(tiVel); a[2] = ComponentType.ReadOnly(tiMass); a[3] = ComponentType.ReadOnly(tiLT);
                qParts = em.CreateEntityQuery(a);
                var b = new Il2CppStructArray<ComponentType>(3); b[0] = ComponentType.ReadOnly(tiDecJ); b[1] = ComponentType.ReadOnly(tiLTW); b[2] = ComponentType.ReadOnly(tiParent);
                qBlocos = em.CreateEntityQuery(b);
                var c = new Il2CppStructArray<ComponentType>(1); c[0] = ComponentType.ReadOnly(tiSing);
                qSing = em.CreateEntityQuery(c);
                var d = new Il2CppStructArray<ComponentType>(3); d[0] = ComponentType.ReadOnly(tiPrefab); d[1] = ComponentType.ReadOnly(tiParent); d[2] = ComponentType.ReadOnly(tiLTW);
                qComps = em.CreateEntityQuery(d);
                // 0.11.0: tudo que tem Parent + LocalTransform (blocos e os renderers/colisores filhos dos blocos)
                var f = new Il2CppStructArray<ComponentType>(2); f[0] = ComponentType.ReadOnly(tiParent); f[1] = ComponentType.ReadOnly(tiLT);
                qFilhos = em.CreateEntityQuery(f);
                try { tiPTM = TypeManager.GetTypeIndex(Il2CppType.Of<PostTransformMatrix>()); ptmOk = true; } catch (Exception ex) { Registro.Aviso("PostTransformMatrix not available: " + ex.Message); }
                int sj = Marshal.SizeOf<PhysicsJoint>(), sp = Marshal.SizeOf<PhysicsConstrainedBodyPair>();
                Registro.Info("ECS ready. PhysicsJoint=" + sj + " B, BodyPair=" + sp + " B, SpaceshipJoint=" + Marshal.SizeOf<SpaceshipJoint>() + " B (game: " + IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<SpaceshipJoint>.NativeClassPtr, ref alinh) + ")");
                Ok = true; return true;
            }
            catch (Exception ex) { Falhou = true; Registro.Erro("ECS: setup failed: " + ex); return false; }
        }
        static uint alinh;

        // ---- 0.12.0: ponteiros (il2cpp) de grupos de sistemas gerenciados, pro prefix do Harmony em SystemBase.Update reconhecer a instancia
        public static IntPtr PtrTransform, PtrPresentation, PtrGrupoBinds, PtrTickPre, PtrTickPost, PtrPhysicsPre; static bool ptrsOk;   // 0.19.13: + PhysicsPreGroup (regra dos 100 km)   // 0.19.6: + grupos de tick dos componentes (LRT)
        public static void PrepararPonteiros()
        {
            if (ptrsOk) return;
            try
            {
                var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return;
                var lista = w.Systems; int n = lista.Count;
                for (int i = 0; i < n; i++)
                {
                    var s = lista[i]; if (s == null) continue;
                    string nome; try { nome = s.GetIl2CppType().Name; } catch (Exception) { continue; }
                    if (nome == "TransformSystemGroup") PtrTransform = s.Pointer;
                    else if (nome == "PresentationSystemGroup") PtrPresentation = s.Pointer;
                    else if (nome == "CustomRenderPipelineGroup") PtrGrupoBinds = s.Pointer;   // 0.13.2: onde roda o SeatBindingsUpdateSystem
                    else if (nome == "SpaceshipComponentsTickPreGroup") PtrTickPre = s.Pointer;
                    else if (nome == "SpaceshipComponentsTickPostGroup") PtrTickPost = s.Pointer;
                    else if (nome == "PhysicsPreGroup") PtrPhysicsPre = s.Pointer;   // 0.19.13
                }
                ptrsOk = true;
                Registro.Info("system groups: TransformSystemGroup " + (PtrTransform == IntPtr.Zero ? "NOT found" : "ok") + ", PresentationSystemGroup " + (PtrPresentation == IntPtr.Zero ? "NOT found" : "ok") + ", CustomRenderPipelineGroup (seat key bindings) " + (PtrGrupoBinds == IntPtr.Zero ? "NOT found" : "ok") + ", SpaceshipComponentsTickPre/Post " + (PtrTickPre == IntPtr.Zero || PtrTickPost == IntPtr.Zero ? "NOT found" : "ok") + ", PhysicsPreGroup " + (PtrPhysicsPre == IntPtr.Zero ? "NOT found" : "ok") + " (" + n + " managed systems)");
            }
            catch (Exception ex) { ptrsOk = true; Registro.Aviso("system group pointers: " + ex.Message); }
        }
        public static void ReiniciarPonteiros() { ptrsOk = false; PtrTransform = IntPtr.Zero; PtrPresentation = IntPtr.Zero; PtrGrupoBinds = IntPtr.Zero; PtrTickPre = IntPtr.Zero; PtrTickPost = IntPtr.Zero; PtrPhysicsPre = IntPtr.Zero; Longe.Reiniciar(); }

        // ---- listas
        public static unsafe List<Entity> Lista(EntityQuery q)
        {
            var r = new List<Entity>();
            var arr = q.ToEntityArray(new AllocatorManager.AllocatorHandle { Index = 2, Version = 0 });
            int n = arr.m_Length; var buf = (Entity*)arr.m_Buffer;
            for (int i = 0; i < n; i++) r.Add(buf[i]);
            try { arr.Dispose(); } catch (Exception) { }
            return r;
        }
        public static List<Entity> Parts(EntityManager em) => Lista(qParts);
        /// <summary>Blocos com buffer SCDecouplingJoint (decouplers do jogo e os nossos, que sao copias do decoupler).</summary>
        public static List<Entity> BlocosDecoupler(EntityManager em) => Lista(qBlocos);
        /// <summary>Todo componente de nave (SCPrefab + Parent + LocalToWorld) — pra achar bearing e motor, que nao sao copias do decoupler.</summary>
        public static List<Entity> Componentes(EntityManager em) => Lista(qComps);
        /// <summary>Wireless Transmitter: SCTypeWirelessTransmitter._currentChannel (+32). -1 se nao e' transmitter.</summary>
        public static unsafe int Canal(EntityManager em, Entity e)
        {
            try { if (Nulo(e) || !em.HasComponent(e, ComponentType.ReadOnly(tiWireless))) return -1; return *(int*)((Ecs.TemC(em, e, tiWireless) ? (byte*)em.GetComponentDataRawRO(e, tiWireless) : null) + 32); }
            catch (Exception) { return -1; }
        }
        public static List<Entity> Transmitters(EntityManager em) => Lista(qWireless);
        public static EntityQuery QSing => qSing;
        public static long K(Entity e) => ((long)e.Index << 32) | (uint)e.Version;
        public static Entity DeK(long k) => new Entity { Index = (int)(k >> 32), Version = (int)(uint)(k & 0xffffffff) };
        /// <summary>0.11.0: entities cujo Parent e' `pai` (renderers/colisores de um bloco; blocos de uma part).</summary>
        // 0.12.6: indice pai -> filhos refeito no maximo 1x por frame. Antes cada chamada varria TODOS os filhos do mundo; com o menu de
        // blueprint aberto (o jogo cria as entities das previas dos BPs) isso virava milhares x milhares por varredura = menu travando.
        static int frameFilhos = -1; static readonly Dictionary<long, List<Entity>> filhosDe = new Dictionary<long, List<Entity>>();
        public static int TotalFilhos;
        public static unsafe List<Entity> Filhos(EntityManager em, Entity pai)
        {
            try
            {
                int f = UnityEngine.Time.frameCount;
                if (f != frameFilhos)
                {
                    frameFilhos = f; foreach (var l in filhosDe.Values) l.Clear();
                    var todos = Lista(qFilhos); TotalFilhos = todos.Count;
                    foreach (var e in todos)
                    {
                        var p = (Ecs.TemC(em, e, tiParent) ? (Parent*)em.GetComponentDataRawRO(e, tiParent) : null);
                        if (p == null) continue;
                        long k = K(p->Value);
                        if (!filhosDe.TryGetValue(k, out var lista)) { lista = new List<Entity>(); filhosDe[k] = lista; }
                        lista.Add(e);
                    }
                    if (filhosDe.Count > 20000) { var vazios = new List<long>(); foreach (var kv in filhosDe) if (kv.Value.Count == 0) vazios.Add(kv.Key); foreach (var k2 in vazios) filhosDe.Remove(k2); }
                }
                return filhosDe.TryGetValue(K(pai), out var r) ? new List<Entity>(r) : new List<Entity>();
            }
            catch (Exception ex) { Registro.Aviso("children of " + pai.Index + ": " + ex.Message); return new List<Entity>(); }
        }
        public static unsafe bool TemPTM(EntityManager em, Entity e) => ptmOk && em.HasComponent(e, ComponentType.ReadOnly(tiPTM));
        public static unsafe void EscalaY(EntityManager em, Entity e, float sy)
        {
            var m = (Ecs.TemC(em, e, tiPTM) ? (float4x4*)em.GetComponentDataRawRW(e, tiPTM) : null);
            if (m == null) return;
            *m = float4x4.Scale(new float3 { x = 1f, y = sy, z = 1f });
        }
        public static unsafe string Componentes(EntityManager em, Entity e)
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
                    sb.Append(i == 0 ? "" : ", ").Append(nome ?? ("#" + ct.TypeIndex.Value));
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "(could not list: " + ex.Message + ")"; }
        }
        public static bool EhDecouplerDoJogo(EntityManager em, Entity e) => em.HasComponent(e, ComponentType.ReadOnly(tiDecTipo));
        /// <summary>0.11.1: SCGuid do bloco (16 bytes) em hexa, "" se nao tem. E' o id que o jogo guarda no save/blueprint —
        /// serve de chave pra guardar coisas nossas por bloco (texto das etiquetas dos blocos genericos).</summary>
        public static unsafe string Guid(EntityManager em, Entity e)
        {
            try
            {
                if (!guidOk) { guidOk = true; tiGuid = TypeManager.GetTypeIndex(Il2CppType.Of<SCGuid>()); }
                if (tiGuid.Value == 0 || !em.HasComponent(e, ComponentType.ReadOnly(tiGuid))) return "";
                var b = (Ecs.TemC(em, e, tiGuid) ? (byte*)em.GetComponentDataRawRO(e, tiGuid) : null); if (b == null) return "";
                var sb = new System.Text.StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
            catch (Exception ex) { Registro.Aviso("block guid " + e.Index + ": " + ex.Message); return ""; }
        }
        // 0.19.12: jogador (PlayerControllerData) pro log de transicao
        static TypeIndex tiJog; static EntityQuery qJog; static bool jogOk;
        public static Entity MeuJogadorOuNulo(EntityManager em)
        {
            try
            {
                if (!jogOk) { jogOk = true; tiJog = TypeManager.GetTypeIndex(Il2CppType.Of<PlayerControllerData>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiJog); qJog = em.CreateEntityQuery(a); }
                foreach (var e in Lista(qJog)) return e;
            }
            catch (Exception) { }
            return Entity.Null;
        }
        public static bool Nulo(Entity e) => e.Index == 0 && e.Version == 0;
        public static bool Vivo(EntityManager em, Entity e) { try { return !Nulo(e) && em.Exists(e); } catch (Exception) { return false; } }
        public static bool EhPart(EntityManager em, Entity e)
        {
            try { return Vivo(em, e) && em.HasComponent(e, ComponentType.ReadOnly(tiSPData)) && em.HasComponent(e, ComponentType.ReadOnly(tiMass)) && em.HasComponent(e, ComponentType.ReadOnly(tiPWI)); }
            catch (Exception) { return false; }
        }
        public static unsafe Entity Pai(EntityManager em, Entity e)
        {
            try { if (!em.HasComponent(e, ComponentType.ReadOnly(tiParent))) return Entity.Null; return ((Ecs.TemC(em, e, tiParent) ? (Parent*)em.GetComponentDataRawRO(e, tiParent) : null))->Value; }
            catch (Exception) { return Entity.Null; }
        }

        // ---- buffers crus. ATENCAO: EntityManager.GetBufferRawRO devolve o ponteiro dos ELEMENTOS (BufferHeader.GetElementPointer),
        // nao do cabecalho; o tamanho vem de GetBufferLength. (Ate a 0.1.3 eu lia como se fosse o cabecalho -> sempre "vazio".)
        public static unsafe int[] BufferInts(EntityManager em, Entity e, TypeIndex ti)
        {
            int n = em.GetBufferLength(e, ti); if (n <= 0 || n > 4096) return new int[0];
            var dados = (int*)em.GetBufferRawRO(e, ti); if (dados == null) return new int[0];
            var r = new int[n]; for (int i = 0; i < n; i++) r[i] = dados[i];
            return r;
        }
        public static unsafe Entity[] BufferEntities(EntityManager em, Entity e, TypeIndex ti)
        {
            int n = em.GetBufferLength(e, ti); if (n <= 0 || n > 4096) return new Entity[0];
            var dados = (Entity*)em.GetBufferRawRO(e, ti); if (dados == null) return new Entity[0];
            var r = new Entity[n]; for (int i = 0; i < n; i++) r[i] = dados[i];
            return r;
        }

        // ---- SpaceshipSingleton._joints = UnsafeList<SpaceshipJoint>*
        [StructLayout(LayoutKind.Sequential)] public unsafe struct UnsafeListCabecalho { public void* Ptr; public int Length; public int Capacity; }
        public static unsafe SpaceshipJoint* Joints(EntityManager em, out int n)
        {
            n = 0;
            var sings = Lista(qSing); if (sings.Count == 0) return null;
            var s = (Ecs.TemC(em, sings[0], tiSing) ? (SpaceshipSingleton*)em.GetComponentDataRawRO(sings[0], tiSing) : null);
            var ul = (UnsafeListCabecalho*)s->_joints; if (ul == null || ul->Ptr == null) return null;
            n = ul->Length; if (n < 0 || n > 1000000) { n = 0; return null; }
            return (SpaceshipJoint*)ul->Ptr;
        }

        // ---- sinal da porta de entrada do bloco (primeira porta do buffer SpaceshipElectricPortRef)
        /// <summary>Sinal da porta [0] do bloco. SpaceshipElectricPort.DataValue() (IL do jogo) = `_portValue` (float no offset 8)
        /// se ele for <= um maximo, senao 0. Le cru pra nao chamar metodo de struct do jogo em ponteiro de chunk.
        /// `motivo` diz por que nao leu (pra logar uma vez).</summary>
        public static unsafe bool Sinal(EntityManager em, Entity bloco, out float v, out string motivo)
        {
            v = 0f; motivo = null;
            try
            {
                if (!em.HasComponent(bloco, ComponentType.ReadOnly(tiPortRef))) { motivo = "block has no SpaceshipElectricPortRef buffer"; return false; }
                var portas = BufferEntities(em, bloco, tiPortRef); if (portas.Length == 0) { motivo = "SpaceshipElectricPortRef buffer is empty"; return false; }
                var p = PortaDoTipo(em, bloco, PortaEntrada); if (Nulo(p)) p = portas[0];
                if (!Vivo(em, p)) { motivo = "port " + p.Index + " does not exist"; return false; }
                if (!em.HasComponent(p, ComponentType.ReadOnly(tiPort))) { motivo = "port " + p.Index + " has no SpaceshipElectricPort"; return false; }
                var port = (Ecs.TemC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null);
                if (port == null) { motivo = "port GetComponentDataRawRO returned null"; return false; }
                v = *(float*)(port + 8);
                if (float.IsNaN(v) || float.IsInfinity(v) || v > 1e6f) v = 0f;
                return true;
            }
            catch (Exception ex) { motivo = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        /// <summary>Diagnostico da porta, uma vez por bloco: quantas portas, qual entity, valor cru e o que DataValue()/DataValue01() devolvem.</summary>
        public static unsafe string SinalDiagnostico(EntityManager em, Entity bloco)
        {
            try
            {
                if (!em.HasComponent(bloco, ComponentType.ReadOnly(tiPortRef))) return "no SpaceshipElectricPortRef buffer";
                var portas = BufferEntities(em, bloco, tiPortRef);
                var sb = new System.Text.StringBuilder(); sb.Append(portas.Length).Append(" port(s):");
                for (int i = 0; i < portas.Length && i < 4; i++)
                {
                    var p = portas[i]; sb.Append(" [").Append(i).Append(": ").Append(p.Index).Append(":").Append(p.Version);
                    if (!Vivo(em, p)) { sb.Append(" dead]"); continue; }
                    if (!em.HasComponent(p, ComponentType.ReadOnly(tiPort))) { sb.Append(" no SpaceshipElectricPort]"); continue; }
                    var port = (Ecs.TemC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null);
                    sb.Append(" raw+8=").Append((*(float*)(port + 8)).ToString("F3")).Append(" bytes=");
                    for (int k = 0; k < 32; k++) sb.Append(port[k].ToString("x2"));
                    try { var sp = *(SpaceshipElectricPort*)port; sb.Append(" DataValue=").Append(sp.DataValue().ToString("F3")).Append(" DataValue01=").Append(sp.DataValue01().ToString("F3")); }
                    catch (Exception ex) { sb.Append(" DataValue failed: ").Append(ex.Message); }
                    sb.Append("]");
                }
                return sb.ToString();
            }
            catch (Exception ex) { return "diagnostics failed: " + ex.Message; }
        }

        // ---- portas (SpaceshipElectricPort: +0 _persistentConnectedCCOrPort, +8 _portValue, +12 _runtimeEndEntity, +20 _setupType, +21 _runtimeType, +22 _index)
        public const byte PortaEntrada = 0, PortaSaida = 1, PortaEnergia = 2;
        public static Entity[] Portas(EntityManager em, Entity bloco)
        {
            try { if (!em.HasComponent(bloco, ComponentType.ReadOnly(tiPortRef))) return new Entity[0]; return BufferEntities(em, bloco, tiPortRef); }
            catch (Exception) { return new Entity[0]; }
        }
        public static unsafe bool Porta(EntityManager em, Entity p, out byte tipo, out float valor, out Entity outraPonta)
        {
            tipo = 255; valor = 0f; outraPonta = Entity.Null;
            try
            {
                if (!Vivo(em, p) || !em.HasComponent(p, ComponentType.ReadOnly(tiPort))) return false;
                var b = (Ecs.TemC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); if (b == null) return false;
                valor = *(float*)(b + 8); if (float.IsNaN(valor) || float.IsInfinity(valor) || valor > 1e6f || valor < -1e6f) valor = 0f;
                outraPonta = *(Entity*)(b + 12); tipo = b[20];
                return true;
            }
            catch (Exception) { return false; }
        }
        /// <summary>Acha a porta que ALIMENTA uma entrada, atravessando Frame With Ports. O jogo, quando a entrada encosta direto
        /// numa porta de frame (sem cabo no meio), deixa _runtimeEndEntity apontando pra porta do frame e nao passa o valor adiante.
        /// Aqui: porta do frame -> porta par na face oposta do mesmo frame (indice + n/2) -> a outra ponta dela -> ... ate uma saida.
        /// `trilha` descreve o caminho (pro log).</summary>
        public static Entity Fonte(EntityManager em, Entity entrada, out string trilha)
        {
            var sb = new System.Text.StringBuilder(); trilha = "";
            var visto = new HashSet<long>(); Entity cur = entrada;
            for (int hop = 0; hop < 16; hop++)
            {
                if (!visto.Add(K(cur))) { sb.Append(" loop"); break; }
                if (!PortaCrua(em, cur, out var t, out _, out var fim, out var persist)) { sb.Append(" ?").Append(cur.Index); break; }
                var alvo = !Nulo(fim) && !(fim.Index == cur.Index && fim.Version == cur.Version) ? fim : persist;
                if (Nulo(alvo)) { sb.Append(" -> nothing"); break; }
                // 0.19.10 (logs de 02/10: depois de um time rewind / recriacao da nave, o jogo deixa _runtimeEndEntity apontando pro
                // PROPRIO cable cell — todos os LRTs "LOST (path: -> N(not a port))" ao mesmo tempo): se o alvo e' um cable cell, anda
                // pelo cabo (SCTypeCableCell +0x10 _connectedFwd / +0x18 _connectedBwd) ate a porta da outra ponta, sem depender do jogo.
                if (EhCabo(em, alvo)) { int celulas; var fim2 = AndarCabo(em, alvo, cur, out celulas); sb.Append(" -> cable(").Append(celulas).Append(" cell(s))"); if (Nulo(fim2) || (fim2.Index == cur.Index && fim2.Version == cur.Version)) { sb.Append(" -> dead end"); break; } alvo = fim2; }
                if (!PortaCrua(em, alvo, out var t2, out var v2, out _, out _)) { sb.Append(" -> ").Append(alvo.Index).Append("(not a port)"); break; }
                var dono = Pai(em, alvo);
                bool frame = !Nulo(dono) && Vivo(em, dono) && em.HasComponent(dono, ComponentType.ReadOnly(tiFramePortas));
                sb.Append(" -> ").Append(alvo.Index).Append("(t").Append(t2).Append(frame ? " frame " + dono.Index : "").Append(" v=").Append(v2.ToString("F2")).Append(")");
                if (t2 == PortaSaida || !frame) { trilha = sb.ToString(); return alvo; }
                var ps = Portas(em, dono); int i = Array.FindIndex(ps, x => x.Index == alvo.Index && x.Version == alvo.Version);
                if (i < 0 || ps.Length < 2) { sb.Append(" (port outside the frame's buffer)"); break; }
                cur = ps[(i + ps.Length / 2) % ps.Length];
                sb.Append(" => pair ").Append(cur.Index);
            }
            trilha = sb.ToString(); return Entity.Null;
        }
        public static bool EhCabo(EntityManager em, Entity e) { try { return tiCabo.Value != 0 && Vivo(em, e) && em.HasComponent(e, ComponentType.ReadOnly(tiCabo)); } catch (Exception) { return false; } }
        /// <summary>0.19.10: segue um cabo a partir da celula `cel` (vindo de `de`) ate a entity que nao e' celula (a porta da outra ponta).
        /// Entity.Null se o cabo termina no nada.</summary>
        public static unsafe Entity AndarCabo(EntityManager em, Entity cel, Entity de, out int celulas)
        {
            celulas = 0; var prev = de; var cur = cel; var visto = new HashSet<long>();
            for (int i = 0; i < 2048; i++)
            {
                if (!EhCabo(em, cur)) return cur;
                if (!visto.Add(K(cur))) return Entity.Null;
                celulas++;
                var b = (byte*)em.GetComponentDataRawRO(cur, tiCabo); if (b == null) return Entity.Null;
                var fwd = *(Entity*)(b + 0x10); var bwd = *(Entity*)(b + 0x18);
                bool fwdEhPrev = fwd.Index == prev.Index && fwd.Version == prev.Version, bwdEhPrev = bwd.Index == prev.Index && bwd.Version == prev.Version;
                Entity prox = fwdEhPrev ? bwd : bwdEhPrev ? fwd : (!Nulo(fwd) && !visto.Contains(K(fwd)) ? fwd : bwd);
                if (Nulo(prox) || !Vivo(em, prox)) return Entity.Null;
                prev = cur; cur = prox;
            }
            return Entity.Null;
        }
        /// <summary>Le uma entrada: valor da porta que alimenta (atravessando frames), ou o valor da propria porta.</summary>
        public static bool LerEntrada(EntityManager em, Entity entrada, out float valor, out Entity fonte, out string trilha)
        {
            valor = 0f; fonte = Fonte(em, entrada, out trilha);
            if (!Nulo(fonte) && Porta(em, fonte, out _, out var vf, out _)) { valor = vf; return true; }
            return false;   // nada ligado (ou so frames sem cabo na outra ponta)
        }
        public static bool PortaCruaPub(EntityManager em, Entity p, out byte tipo, out float valor, out Entity fim, out Entity persist) => PortaCrua(em, p, out tipo, out valor, out fim, out persist);
        static unsafe bool PortaCrua(EntityManager em, Entity p, out byte tipo, out float valor, out Entity fim, out Entity persist)
        {
            tipo = 255; valor = 0f; fim = Entity.Null; persist = Entity.Null;
            try
            {
                if (!Vivo(em, p) || !em.HasComponent(p, ComponentType.ReadOnly(tiPort))) return false;
                var b = (Ecs.TemC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); if (b == null) return false;
                persist = *(Entity*)b; valor = *(float*)(b + 8); fim = *(Entity*)(b + 12); tipo = b[20];
                if (float.IsNaN(valor) || float.IsInfinity(valor)) valor = 0f;
                return true;
            }
            catch (Exception) { return false; }
        }

        public static unsafe void EscreverPorta(EntityManager em, Entity p, float v)
        {
            try { if (!Vivo(em, p)) return; var b = (Ecs.TemC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRW(p, tiPort) : null); if (b != null) *(float*)(b + 8) = v; } catch (Exception) { }
        }
        /// <summary>0.11.0: _index da porta (+22): posicao dela na lista _electricPorts do prefab.</summary>
        public static unsafe int PortaIndice(EntityManager em, Entity p)
        {
            try { var b = (Ecs.TemC(em, p, tiPort) ? (byte*)em.GetComponentDataRawRO(p, tiPort) : null); return b == null ? -1 : b[22]; } catch (Exception) { return -1; }
        }
        /// <summary>Primeira porta do tipo pedido (0 entrada, 1 saida, 2 energia), na ordem do prefab.</summary>
        public static Entity PortaDoTipo(EntityManager em, Entity bloco, byte tipo)
        {
            foreach (var p in Portas(em, bloco)) if (Porta(em, p, out var t, out _, out _) && t == tipo) return p;
            return Entity.Null;
        }

        // ---- energia (SCElectricPower, que o jogo poe em todo componente com porta vermelha):
        // +32 _consumptionPerSec, +36 _suppliedState (0 NonSupplied, 1 Deficiency, 2 Supplied), +37 _isElectricityInterrupted
        public static unsafe bool TemEnergia(EntityManager em, Entity bloco, out byte estado)
        {
            estado = 255;
            try
            {
                if (!em.HasComponent(bloco, ComponentType.ReadOnly(tiPower))) return false;
                var b = (Ecs.TemC(em, bloco, tiPower) ? (byte*)em.GetComponentDataRawRO(bloco, tiPower) : null); if (b == null) return false;
                estado = b[36]; return estado == 2 && b[37] == 0;
            }
            catch (Exception) { return false; }
        }
        public static unsafe void Consumo(EntityManager em, Entity bloco, float pps)
        {
            try { if (!em.HasComponent(bloco, ComponentType.ReadOnly(tiPower))) return; var b = (Ecs.TemC(em, bloco, tiPower) ? (byte*)em.GetComponentDataRawRW(bloco, tiPower) : null); if (b != null) *(float*)(b + 32) = pps; } catch (Exception) { }
        }

        // ---- prefab do bloco (SCPrefab._prefab = hash do nome)
        static bool prefabCru = true, prefabAvisou = false;
        public static unsafe ulong Prefab(EntityManager em, Entity e)
        {
            try
            {
                if (prefabCru) { try { return ((Ecs.TemC(em, e, tiPrefab) ? (SCPrefab*)em.GetComponentDataRawRO(e, tiPrefab) : null))->_prefab; } catch (Exception) { prefabCru = false; } }
                return em.GetSharedComponent<SCPrefab>(e)._prefab;
            }
            catch (Exception ex) { if (!prefabAvisou) { prefabAvisou = true; Registro.Aviso("could not read the block's SCPrefab: " + ex.Message); } return 0; }
        }
        public static ulong HashPrefab(string nome) { try { return new SCPrefab(nome)._prefab; } catch (Exception ex) { Registro.Aviso("prefab hash " + nome + ": " + ex.Message); return 0; } }

        // ---- transformacoes
        /// <summary>LocalTransform do bloco (relativo ao part pai).</summary>
        /// <summary>0.12.3: a entity existe E tem o componente. Ler/escrever RawRO/RW de um componente que a entity nao tem (bloco sendo
        /// apagado na troca de blueprint) le lixo do chunk ou derruba o jogo (log: _value = -549571231744 e crash logo depois).</summary>
        public static bool TemC(EntityManager em, Entity e, TypeIndex ti) { try { return !Nulo(e) && em.Exists(e) && em.HasComponent(e, ComponentType.ReadOnly(ti)); } catch (Exception) { return false; } }
        public static void Exigir(EntityManager em, Entity e, TypeIndex ti) { if (!TemC(em, e, ti)) throw new InvalidOperationException("entity " + e.Index + " is gone or lost the component"); }
        public static unsafe LocalTransform LT(EntityManager em, Entity e) { Exigir(em, e, tiLT); return *(Ecs.TemC(em, e, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(e, tiLT) : null); }
        public static unsafe float3 Posicao(EntityManager em, Entity e)
        {
            Exigir(em, e, tiLTW);
            var ltw = (Ecs.TemC(em, e, tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRO(e, tiLTW) : null);
            return new float3 { x = ltw->Value.c3.x, y = ltw->Value.c3.y, z = ltw->Value.c3.z };
        }
        /// <summary>Eixos locais do bloco no mundo (colunas do LocalToWorld, normalizadas).</summary>
        public static unsafe void Eixos(EntityManager em, Entity e, out float3 ex, out float3 ey, out float3 ez)
        {
            Exigir(em, e, tiLTW);
            var ltw = (Ecs.TemC(em, e, tiLTW) ? (LocalToWorld*)em.GetComponentDataRawRO(e, tiLTW) : null);
            ex = Norm(new float3 { x = ltw->Value.c0.x, y = ltw->Value.c0.y, z = ltw->Value.c0.z });
            ey = Norm(new float3 { x = ltw->Value.c1.x, y = ltw->Value.c1.y, z = ltw->Value.c1.z });
            ez = Norm(new float3 { x = ltw->Value.c2.x, y = ltw->Value.c2.y, z = ltw->Value.c2.z });
        }
        public static unsafe float3 CentroMassa(EntityManager em, Entity part)
        {
            Exigir(em, part, tiLT); Exigir(em, part, tiMass);
            var lt = (Ecs.TemC(em, part, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(part, tiLT) : null); var m = (Ecs.TemC(em, part, tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(part, tiMass) : null);
            float sc = lt->Scale == 0f ? 1f : lt->Scale;
            return Add(lt->Position, Rot(lt->Rotation, Mul(m->Transform.pos, sc)));
        }
        public static unsafe float Massa(EntityManager em, Entity part) { Exigir(em, part, tiMass); var m = (Ecs.TemC(em, part, tiMass) ? (PhysicsMass*)em.GetComponentDataRawRO(part, tiMass) : null); return m->InverseMass > 0f ? 1f / m->InverseMass : 0f; }
        /// <summary>0.7.9: pose do bloco no mundo pela FISICA (LocalTransform da part pai ∘ LocalTransform do bloco), sem depender do
        /// LocalToWorld — que no 1o frame depois de lancar/refazer parts pode estar atrasado (encaixe nascia no lugar errado e a
        /// roda ficava presa longe do eixo).</summary>
        public static unsafe void Pose(EntityManager em, Entity bloco, out float3 pos, out quaternion rot)
        {
            Exigir(em, bloco, tiLT);
            var lb = (Ecs.TemC(em, bloco, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(bloco, tiLT) : null); var pai = Pai(em, bloco);
            if (Nulo(pai) || !em.HasComponent(pai, ComponentType.ReadOnly(tiLT))) { pos = lb->Position; rot = lb->Rotation; return; }
            var lp = (Ecs.TemC(em, pai, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(pai, tiLT) : null); float s = lp->Scale == 0f ? 1f : lp->Scale;
            pos = Add(lp->Position, Rot(lp->Rotation, Mul(lb->Position, s))); rot = QMul(lp->Rotation, lb->Rotation);
        }
        public static quaternion QMul(quaternion a, quaternion b)
        {
            float4 p = a.value, q = b.value;
            return new quaternion { value = new float4 { x = p.w * q.x + p.x * q.w + p.y * q.z - p.z * q.y, y = p.w * q.y - p.x * q.z + p.y * q.w + p.z * q.x, z = p.w * q.z + p.x * q.y - p.y * q.x + p.z * q.w, w = p.w * q.w - p.x * q.x - p.y * q.y - p.z * q.z } };
        }
        public static readonly float3 UX = new float3 { x = 1f }, UY = new float3 { y = 1f }, UZ = new float3 { z = 1f };

        /// <summary>Frame do joint no espaco local do part.</summary>
        public static unsafe BodyFrame Frame(EntityManager em, Entity part, float3 pontoMundo, float3 eixoMundo, float3 perpMundo)
        {
            Exigir(em, part, tiLT);
            var lt = (Ecs.TemC(em, part, tiLT) ? (LocalTransform*)em.GetComponentDataRawRO(part, tiLT) : null);
            var qi = Inv(lt->Rotation); float s = lt->Scale == 0f ? 1f : lt->Scale;
            var bf = new BodyFrame();
            bf.Position = Div(Rot(qi, Sub(pontoMundo, lt->Position)), s);
            bf.Axis = Rot(qi, eixoMundo); bf.PerpendicularAxis = Rot(qi, perpMundo);
            return bf;
        }

        // ---- matematica (sem chamar Unity.Mathematics pelo il2cpp)
        public static float3 Rot(quaternion q, float3 v)
        {
            float qx = q.value.x, qy = q.value.y, qz = q.value.z, qw = q.value.w;
            float cx = qy * v.z - qz * v.y + qw * v.x, cy = qz * v.x - qx * v.z + qw * v.y, cz = qx * v.y - qy * v.x + qw * v.z;
            return new float3 { x = v.x + 2f * (qy * cz - qz * cy), y = v.y + 2f * (qz * cx - qx * cz), z = v.z + 2f * (qx * cy - qy * cx) };
        }
        public static quaternion Inv(quaternion q) { var r = q; r.value.x = -q.value.x; r.value.y = -q.value.y; r.value.z = -q.value.z; return r; }
        public static float3 Sub(float3 a, float3 b) => new float3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z };
        public static float3 Add(float3 a, float3 b) => new float3 { x = a.x + b.x, y = a.y + b.y, z = a.z + b.z };
        public static float3 Mul(float3 a, float s) => new float3 { x = a.x * s, y = a.y * s, z = a.z * s };
        public static float3 Div(float3 a, float s) => new float3 { x = a.x / s, y = a.y / s, z = a.z / s };
        public static float Dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static float Comp(float3 v) => (float)System.Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        public static float Dist(float3 a, float3 b) => Comp(Sub(a, b));
        public static float3 Norm(float3 v) { float c = Comp(v); return c < 1e-6f ? v : Div(v, c); }
        public static float3 Cross(float3 a, float3 b) => new float3 { x = a.y * b.z - a.z * b.y, y = a.z * b.x - a.x * b.z, z = a.x * b.y - a.y * b.x };
        public static string F3(float3 v) => v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2");
        public static float3 Perp(float3 eixo)
        {
            var r = System.Math.Abs(eixo.y) < 0.9f ? new float3 { x = 0, y = 1, z = 0 } : new float3 { x = 1, y = 0, z = 0 };
            return Norm(Cross(eixo, r));
        }
    }

    internal static class Registro
    {
        public static BepInEx.Logging.ManualLogSource Fonte;
        public static void Info(string s) { Fonte?.LogInfo(s); }
        public static void Aviso(string s) { Fonte?.LogWarning(s); }
        public static void Erro(string s) { Fonte?.LogError(s); }
    }
}
