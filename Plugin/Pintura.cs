using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace Mecanica
{
    /// <summary>0.17.1 (Andre: "o shaft e o transmitter nascem com a cor errada, e so corrige se eu pegar o pincel e limpar a cor
    /// atual — talvez tinha que atualizar uma vez depois de colocar no mundo"): a cor pintada fica em SCPaintableColor, mas o que o
    /// render desenha e' o CRPRendererMemory_Color de cada renderer pintavel (SCPaintableRenderer). O jogo so reescreve essa memoria
    /// quando alguem pinta (GarageGrabber); nos blocos do mod com cor padrao != None (Drive Shaft, Shaft Transmitter) ela nasce com
    /// outra cor. Aqui, uma vez por entidade nova: memoria de cada renderer = cor pintada (None com padrao != None = a padrao).</summary>
    internal static class Pintura
    {
        static TypeIndex tiPaint, tiRend, tiMem, tiCelula, tiTransm; static bool ok, falhou;
        static EntityQuery qCelulas, qTransm; static float t = 9f; static ulong hCabo, hT, hTM;
        static readonly HashSet<long> feitos = new HashSet<long>(); static int logados;

        static bool Preparar(EntityManager em)
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiPaint = TypeManager.GetTypeIndex(Il2CppType.Of<SCPaintableColor>());
                tiRend = TypeManager.GetTypeIndex(Il2CppType.Of<SCPaintableRenderer>());
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                tiCelula = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCableCell>());
                tiTransm = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiPaint); a[1] = ComponentType.ReadOnly(tiCelula);
                qCelulas = em.CreateEntityQuery(a);
                var b = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2); b[0] = ComponentType.ReadOnly(tiPaint); b[1] = ComponentType.ReadOnly(tiTransm);
                qTransm = em.CreateEntityQuery(b);
                hCabo = Ecs.HashPrefab(Juntas.CABO); hT = Ecs.HashPrefab(Juntas.TRANSMISSOR); hTM = Ecs.HashPrefab(Juntas.TRANSMISSOR + " M");
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Erro("paint: types: " + ex.Message); return false; }
        }

        public static void Passo(EntityManager em, float dt)
        {
            t += dt; if (t < 0.5f) return; t = 0f;
            if (!Preparar(em)) return;
            int n = 0;
            try { n += Varrer(em, qCelulas, true); n += Varrer(em, qTransm, false); }
            catch (Exception ex) { if (logados++ < 3) Registro.Aviso("paint: " + ex.Message); }
            if (n > 0 && logados++ < 20) Registro.Info("paint: " + n + " Drive Shaft / Shaft Transmitter part(s) got their painted color on the renderers");
        }

        /// <summary>0.19.2: pinta um bloco qualquer do mod com cor padrao (Long Range Transmitter): 1x por entity.</summary>
        public static bool Pintar(EntityManager em, Entity e)
        {
            if (!Preparar(em) || !Ecs.Vivo(em, e) || !Ecs.TemC(em, e, tiPaint)) return false;
            long k = Ecs.K(e); if (feitos.Contains(k)) return true;
            try { return PintarUm(em, e); } catch (Exception ex) { if (logados++ < 3) Registro.Aviso("paint: " + ex.Message); return false; }
        }
        static unsafe int Varrer(EntityManager em, EntityQuery q, bool celulas)
        {
            int n = 0;
            foreach (var e in Ecs.Lista(q))
            {
                long k = Ecs.K(e); if (feitos.Contains(k)) continue;
                ulong h = Ecs.Prefab(em, e);
                if (celulas ? h != hCabo : (h != hT && h != hTM)) { feitos.Add(k); continue; }
                if (PintarUm(em, e)) n++;
            }
            return n;
        }
        static unsafe bool PintarUm(EntityManager em, Entity e)
        {
            long k = Ecs.K(e);
            {
                var p = (byte*)em.GetComponentDataRawRW(e, tiPaint); if (p == null) return false;
                byte cor = p[0], padrao = p[8];
                if (cor == 0 && padrao != 0) { for (int i = 0; i < 8; i++) p[i] = padrao; cor = padrao; }
                SpaceshipComponentExtensions.GetProperties((SpaceshipComponentColor)cor, out float4 c, out SpecularSetup s);
                var mem = new CRPRendererMemory_Color(c, s);
                int escritos = 0;
                if (Ecs.TemC(em, e, tiMem)) { *(CRPRendererMemory_Color*)em.GetComponentDataRawRW(e, tiMem) = mem; escritos++; }
                if (em.HasComponent(e, ComponentType.ReadOnly(tiRend)))
                {
                    int len = em.GetBufferLength(e, tiRend); var buf = (Entity*)em.GetBufferRawRO(e, tiRend);
                    for (int i = 0; i < len && buf != null; i++)
                    {
                        var r = buf[i]; if (!Ecs.Vivo(em, r) || !Ecs.TemC(em, r, tiMem)) continue;
                        *(CRPRendererMemory_Color*)em.GetComponentDataRawRW(r, tiMem) = mem; escritos++;
                    }
                }
                feitos.Add(k); return escritos > 0;
            }
        }
    }
}
