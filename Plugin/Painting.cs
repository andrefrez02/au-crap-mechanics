using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace CrapMechanics
{
    /// <summary>0.17.1 (Andre: "the shaft and the transmitter are born with the wrong colour, and it only fixes itself if I take the brush and clear the current
    /// colour — maybe it had to update once after placing it in the world"): the painted colour lives in SCPaintableColor, but what the
    /// render draws is the CRPRendererMemory_Color of each paintable renderer (SCPaintableRenderer). The game only rewrites that memory
    /// when someone paints (GarageGrabber); on the mod's blocks with a default colour != None (Drive Shaft, Shaft Transmitter) it's born with
    /// another colour. Here, once per new entity: memory of each renderer = painted colour (None with default != None = the default).</summary>
    internal static class Painting
    {
        static TypeIndex tiPaint, tiRend, tiMem, tiCell, tiTransm; static bool ok, failed;
        static EntityQuery qCells, qTransm; static float t = 9f; static ulong hCable, hT, hTM;
        static readonly HashSet<long> doneSet = new HashSet<long>(); static int loggedSet;

        static bool Prepare(EntityManager em)
        {
            if (ok) return true; if (failed) return false;
            try
            {
                tiPaint = TypeManager.GetTypeIndex(Il2CppType.Of<SCPaintableColor>());
                tiRend = TypeManager.GetTypeIndex(Il2CppType.Of<SCPaintableRenderer>());
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                tiCell = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeCableCell>());
                tiTransm = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>());
                var a = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(tiPaint); a[1] = ComponentType.ReadOnly(tiCell);
                qCells = em.CreateEntityQuery(a);
                var b = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ComponentType>(2); b[0] = ComponentType.ReadOnly(tiPaint); b[1] = ComponentType.ReadOnly(tiTransm);
                qTransm = em.CreateEntityQuery(b);
                hCable = Ecs.HashPrefab(JointSystem.CABLE); hT = Ecs.HashPrefab(JointSystem.TRANSMITTER); hTM = Ecs.HashPrefab(JointSystem.TRANSMITTER + " M");
                ok = true; return true;
            }
            catch (Exception ex) { failed = true; ModLog.Error("paint: types: " + ex.Message); return false; }
        }

        public static void Step(EntityManager em, float dt)
        {
            t += dt; if (t < 0.5f) return; t = 0f;
            if (!Prepare(em)) return;
            int n = 0;
            try { n += Scan(em, qCells, true); n += Scan(em, qTransm, false); }
            catch (Exception ex) { if (loggedSet++ < 3) ModLog.Warn("paint: " + ex.Message); }
            if (n > 0 && loggedSet++ < 20) ModLog.Info("paint: " + n + " Drive Shaft / Shaft Transmitter part(s) got their painted color on the renderers");
        }

        /// <summary>0.19.2: paints any mod block with a default colour (Long Range Transmitter): once per entity.</summary>
        public static bool Paint(EntityManager em, Entity e)
        {
            if (!Prepare(em) || !Ecs.Alive(em, e) || !Ecs.HasC(em, e, tiPaint)) return false;
            long k = Ecs.K(e); if (doneSet.Contains(k)) return true;
            try { return PaintOne(em, e); } catch (Exception ex) { if (loggedSet++ < 3) ModLog.Warn("paint: " + ex.Message); return false; }
        }
        static unsafe int Scan(EntityManager em, EntityQuery q, bool cells)
        {
            int n = 0;
            foreach (var e in Ecs.QueryList(q))
            {
                long k = Ecs.K(e); if (doneSet.Contains(k)) continue;
                ulong h = Ecs.Prefab(em, e);
                if (cells ? h != hCable : (h != hT && h != hTM)) { doneSet.Add(k); continue; }
                if (PaintOne(em, e)) n++;
            }
            return n;
        }
        static unsafe bool PaintOne(EntityManager em, Entity e)
        {
            long k = Ecs.K(e);
            {
                var p = (byte*)em.GetComponentDataRawRW(e, tiPaint); if (p == null) return false;
                byte color = p[Layout.PaintPainted], defaultValue = p[Layout.PaintDefault];
                if (color == 0 && defaultValue != 0) { for (int i = 0; i < 8; i++) p[Layout.PaintPainted + i] = defaultValue; color = defaultValue; }
                SpaceshipComponentExtensions.GetProperties((SpaceshipComponentColor)color, out float4 c, out SpecularSetup s);
                var mem = new CRPRendererMemory_Color(c, s);
                int writtenSet = 0;
                if (Ecs.HasC(em, e, tiMem)) { *(CRPRendererMemory_Color*)em.GetComponentDataRawRW(e, tiMem) = mem; writtenSet++; }
                if (em.HasComponent(e, ComponentType.ReadOnly(tiRend)))
                {
                    int len = em.GetBufferLength(e, tiRend); var buf = (Entity*)em.GetBufferRawRO(e, tiRend);
                    for (int i = 0; i < len && buf != null; i++)
                    {
                        var r = buf[i]; if (!Ecs.Alive(em, r) || !Ecs.HasC(em, r, tiMem)) continue;
                        *(CRPRendererMemory_Color*)em.GetComponentDataRawRW(r, tiMem) = mem; writtenSet++;
                    }
                }
                doneSet.Add(k); return writtenSet > 0;
            }
        }
    }
}
