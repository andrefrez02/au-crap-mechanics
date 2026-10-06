using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Mecanica
{
    /// <summary>0.19.13 (Andre, 03/10: "quando um objeto solto tiver beacon continua rodando a fisica dele"). O jogo tem o
    /// `SPActiveUpdateSystem` (PhysicsPreGroup, OrderFirst): toda part (SPData + LocalTransform + Child) a mais de 100 km do centro do
    /// universo (|LocalTransform.Position|² > 1E+10, o centro e' o jogador) perde `SPActive` + `PhysicsWorldIndex` e os blocos dela
    /// perdem `SCActive`; quem volta pra menos de 100 km ganha `SPActive` e `SCActive` de volta (o jogo NAO devolve o PhysicsWorldIndex).
    /// Sem SPActive/SCActive param a fisica, o tick da camera, do transmitter, do thruster — foi isso que matou a camera da sonda aos 61 s
    /// do video de 03/10 (nao foi atmosfera nem energia).
    ///
    /// Aqui o system do jogo e' DESLIGADO (SystemState.Enabled = false, pelo ponteiro do estado) e a mesma regra roda no prefix do
    /// PhysicsPreGroup (mesmo lugar, mesma conta), com uma excecao: part que tem um SC_Beacon do mod com energia continua simulada a
    /// qualquer distancia (e, se o jogo ja tinha desligado, e' religada: SPActive + PhysicsWorldIndex 0 + SCActive nos blocos).
    /// Config [Beacon] keep_simulated_far. Se nao conseguir desligar o system do jogo, nao mexe em nada (aviso no log).</summary>
    internal static class Longe
    {
        public static bool Ligado = true;
        const float LIMITE2 = 1E+10f;   // (100 km)²

        static TypeIndex tiSPActive, tiSCActive, tiChild; static EntityQuery qPartes; static bool tiOk, falhou;
        static World mundoDesligado; static IntPtr estadoJogo; static bool desligado; static float tConfere;
        static readonly Dictionary<long, Entity> isentos = new Dictionary<long, Entity>();   // part -> beacon com energia
        static readonly Dictionary<long, byte> situacao = new Dictionary<long, byte>();       // part -> 0 perto, 1 longe desligada, 2 longe mantida
        static int erros;

        static bool Preparar(EntityManager em)
        {
            if (tiOk) return true; if (falhou) return false;
            try
            {
                tiSPActive = TypeManager.GetTypeIndex(Il2CppType.Of<SPActive>());
                tiSCActive = TypeManager.GetTypeIndex(Il2CppType.Of<SCActive>());
                tiChild = TypeManager.GetTypeIndex(Il2CppType.Of<Child>());
                var a = new Il2CppStructArray<ComponentType>(3);
                a[0] = ComponentType.ReadOnly(Ecs.tiSPData); a[1] = ComponentType.ReadOnly(Ecs.tiLT); a[2] = ComponentType.ReadOnly(tiChild);
                qPartes = em.CreateEntityQuery(a);
                tiOk = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("far parts: setup failed (" + ex.Message + ") — the game keeps its 100 km rule"); return false; }
        }

        /// <summary>Desliga o SPActiveUpdateSystem do jogo neste World. Interop: WorldUnmanaged.ResolveSystemState(handle) devolve o
        /// ponteiro cru do SystemState (metodo que devolve ponteiro — chamado por reflexao, igual ao GetComponentDataRawRO); o
        /// set_Enabled nativo e' chamado por il2cpp_runtime_invoke com esse ponteiro como `this` (struct desembalada, como o proprio
        /// interop faz com il2cpp_object_unbox).</summary>
        static unsafe bool DesligarJogo(World w)
        {
            if (desligado && mundoDesligado == w && tConfere < 10f) return true;
            try
            {
                // o ponteiro do estado e' resolvido de novo a cada 10 s (e sempre depois de um Reiniciar): se o jogo recriou o system,
                // o ponteiro velho nao e' tocado
                var wu = w.Unmanaged;
                var h = wu.GetExistingUnmanagedSystem(Il2CppType.Of<SPActiveUpdateSystem>());
                var m = typeof(WorldUnmanaged).GetMethod("ResolveSystemState", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(SystemHandle) }, null);
                if (m == null) throw new InvalidOperationException("WorldUnmanaged.ResolveSystemState not found in the interop");
                object r = m.Invoke(wu, new object[] { h });
                IntPtr estado = (IntPtr)System.Reflection.Pointer.Unbox(r);
                if (estado == IntPtr.Zero) throw new InvalidOperationException("SystemState is null");
                bool antes = LerEnabled(estado);
                bool mesmo = desligado && estado == estadoJogo && mundoDesligado == w;
                if (!antes && mesmo) { tConfere = 0f; return true; }   // conferido: continua desligado
                EscreverEnabled(estado, false);
                if (LerEnabled(estado)) throw new InvalidOperationException("Enabled still true after writing false");
                if (mesmo) Registro.Aviso("far parts: the game switched its SPActiveUpdateSystem back on — switched off again");
                else Registro.Info("far parts: the game's SPActiveUpdateSystem is off (was " + (antes ? "on" : "off") + (desligado ? "; system recreated" : "") + ") — the 100 km rule now runs in the mod, with powered beacons exempt");
                estadoJogo = estado; desligado = true; mundoDesligado = w; tConfere = 0f;
                return true;
            }
            catch (Exception ex) { falhou = true; desligado = false; estadoJogo = IntPtr.Zero; Registro.Aviso("far parts: could not switch the game's SPActiveUpdateSystem off (" + ex.Message + ") — the game keeps its 100 km rule"); return false; }
        }
        static IntPtr MetodoNativo(string campo)
        {
            var f = typeof(SystemState).GetField(campo, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) throw new InvalidOperationException("SystemState." + campo + " not found in the interop");
            return (IntPtr)f.GetValue(null);
        }
        static unsafe bool LerEnabled(IntPtr estado)
        {
            IntPtr exc = IntPtr.Zero;
            IntPtr ret = IL2CPP.il2cpp_runtime_invoke(MetodoNativo("NativeMethodInfoPtr_get_Enabled_Public_get_Boolean_0"), estado, null, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
            return *(bool*)IL2CPP.il2cpp_object_unbox(ret);
        }
        static unsafe void EscreverEnabled(IntPtr estado, bool v)
        {
            IntPtr* args = stackalloc IntPtr[1]; args[0] = (IntPtr)(&v); IntPtr exc = IntPtr.Zero;
            IL2CPP.il2cpp_runtime_invoke(MetodoNativo("NativeMethodInfoPtr_set_Enabled_Public_set_Void_Boolean_0"), estado, (void**)args, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
        }
        /// <summary>Devolve o system do jogo (config desligada / saida).</summary>
        public static void ReligarJogo()
        {
            if (!desligado) return;
            try { if (mundoDesligado != null && mundoDesligado.IsCreated && estadoJogo != IntPtr.Zero) { EscreverEnabled(estadoJogo, true); Registro.Info("far parts: the game's SPActiveUpdateSystem is back on"); } }
            catch (Exception ex) { Registro.Aviso("far parts: could not switch the game's system back on (" + ex.Message + ")"); }
            desligado = false; estadoJogo = IntPtr.Zero; mundoDesligado = null;
        }
        /// <summary>Mundo descarregado/trocado: o estado do system morreu com ele.</summary>
        public static void Reiniciar() { desligado = false; estadoJogo = IntPtr.Zero; mundoDesligado = null; situacao.Clear(); isentos.Clear(); }

        /// <summary>Prefix do PhysicsPreGroup (onde o system do jogo rodava, OrderFirst): a regra dos 100 km, com a excecao do beacon.</summary>
        /// <summary>0.19.13 (replay): na reproducao do Replay o mundo esta congelado e o Recentrar leva o centro do universo pra camera
        /// do replay — a nave viva fica a milhares de km do centro e, nos momentos em que o passo fixo chega a rodar (3x no meio do replay),
        /// perdia SCActive: o tick da camera que o Replay roda parava de desenhar (monitor chuviscando no video de 03/10). Em reproducao
        /// ninguem e' desligado, e o que estiver desligado e' religado (chamado tambem do Update, ja que o passo fixo nao roda).</summary>
        public static unsafe void Passo(EntityManager em, bool reproducao = false)
        {
            if (!Ligado || falhou || Descarga.Pendente) return;
            if (!Preparar(em)) return;
            var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return;
            if (!reproducao) tConfere += UnityEngine.Time.fixedDeltaTime;
            if (!DesligarJogo(w)) return;
            try
            {

                isentos.Clear();
                foreach (var b in Acoplador.Beacons())
                {
                    if (b.semEnergia || !Ecs.Vivo(em, b.bloco)) continue;
                    var p = Ecs.Pai(em, b.bloco); if (Ecs.Nulo(p) || !Ecs.Vivo(em, p)) continue;
                    isentos[Ecs.K(p)] = b.bloco;
                }
                var partes = Ecs.Lista(qPartes);
                foreach (var e in partes)
                {
                    if (!Ecs.TemC(em, e, Ecs.tiLT)) continue;
                    bool ativo = em.HasComponent(e, ComponentType.ReadOnly(tiSPActive));
                    var lt = (LocalTransform*)em.GetComponentDataRawRO(e, Ecs.tiLT);
                    float d2 = math.lengthsq(lt->Position);
                    bool perto = d2 < LIMITE2;                       // o jogo: perto <=> !(1E10 < d²) && (1E10 − d² != 0)
                    long k = Ecs.K(e); bool isento = isentos.TryGetValue(k, out var beacon);
                    if (reproducao)
                    {
                        if (!ativo) { Ativar(em, e, true); Situacao(k, 3, e, d2, beacon); }
                        continue;
                    }
                    if (ativo && !perto && !isento) { Desativar(em, e); Situacao(k, 1, e, d2, beacon); }
                    else if (!ativo && perto) { Ativar(em, e, false); Situacao(k, 0, e, d2, beacon); }
                    else if (!ativo && isento) { Ativar(em, e, true); Situacao(k, 2, e, d2, beacon); }   // o jogo ja tinha desligado (beacon ligou depois / mod carregou depois)
                    else Situacao(k, perto ? (byte)0 : isento ? (byte)2 : (byte)1, e, d2, beacon);
                }
            }
            catch (Exception ex) { if (erros++ < 3) Registro.Aviso("far parts: " + ex.Message); }
        }

        static void Situacao(long k, byte s, Entity e, float d2, Entity beacon)
        {
            situacao.TryGetValue(k, out var antes);
            if (antes == s) return;
            situacao[k] = s;
            string km = (Math.Sqrt(d2) / 1000.0).ToString("F0");
            if (s == 1) Registro.Info("far parts: part " + e.Index + " is " + km + " km from you — switched off by the 100 km rule (no powered beacon on it)");
            else if (s == 2) Registro.Info("far parts: part " + e.Index + " is " + km + " km from you — kept simulated (SC_Beacon " + beacon.Index + " powered): physics, camera and transmitter ticks go on");
            else if (s == 3) Registro.Info("far parts: part " + e.Index + " was switched off " + km + " km from the universe center during playback — switched on again (camera/monitor ticks need it)");
            else if (antes != 0) Registro.Info("far parts: part " + e.Index + " is back within 100 km (" + km + " km)" + (antes == 1 ? " — switched on again" : ""));
        }

        static void Desativar(EntityManager em, Entity e)
        {
            em.RemoveComponent(e, ComponentType.ReadWrite(tiSPActive));
            if (em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiPWI))) em.RemoveComponent(e, ComponentType.ReadWrite(Ecs.tiPWI));
            foreach (var c in Ecs.BufferEntities(em, e, tiChild))
                if (Ecs.Vivo(em, c) && em.HasComponent(c, ComponentType.ReadOnly(tiSCActive))) em.RemoveComponent(c, ComponentType.ReadWrite(tiSCActive));
        }
        static void Ativar(EntityManager em, Entity e, bool comFisica)
        {
            em.AddComponent(e, ComponentType.ReadWrite(tiSPActive));
            if (comFisica && !em.HasComponent(e, ComponentType.ReadOnly(Ecs.tiPWI))) em.AddComponent(e, ComponentType.ReadWrite(Ecs.tiPWI));   // shared component com o valor padrao = mundo 0 (o jogo poe (PhysicsWorldIndex)0)
            foreach (var c in Ecs.BufferEntities(em, e, tiChild))
                if (Ecs.Vivo(em, c) && !em.HasComponent(c, ComponentType.ReadOnly(tiSCActive))) em.AddComponent(c, ComponentType.ReadWrite(tiSCActive));
        }
    }
}
