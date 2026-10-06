using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Mecanica
{
    /// <summary>0.19.2 (Andre: o beacon nunca apareceu no radar — o diagnostico mostra o fantasma com icone 8 e o buffer da tela so com
    /// a base): em vez de esperar o FindIconsJob do jogo pegar o fantasma, o mod escreve o icone de cada beacon DIRETO no buffer que a
    /// tela desenha (SCTick_Radar.RadarLocationGPUData: _uv float2, _type uint, _isTarget float; 16 B), igual o job faz:
    /// uv = normalizesafe((worldToProjection x pos).xy). As matrizes vem do singleton do radar (SCTick_Radar.Singleton: _viewMatrix +8,
    /// _projectionMatrix +72). O buffer e' refeito pelo jogo a cada tick fisico, entao a escrita e' todo frame, depois do
    /// TransformSystemGroup (antes do render). A formula e' conferida 1x contra o icone da base (tipo 0) que o jogo escreveu: a variante
    /// que bater e' a usada (world w=1, world w=0, relativa ao planeta).</summary>
    internal static class RadarEscrita
    {
        const int T = 16;
        static TypeIndex tiGPU, tiSing, tiStation; static EntityQuery qGPU, qSing, qStation; static bool ok, falhou;
        static int variante = -1, frame = -1, escritos, logs, tentativas; static float tLog = 9f; static string ultimo = "";

        static bool Preparar(EntityManager em)
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiGPU = TypeManager.GetTypeIndex(Il2CppType.Of<SCTick_Radar.RadarLocationGPUData>());
                tiSing = TypeManager.GetTypeIndex(Il2CppType.Of<SCTick_Radar.Singleton>());
                var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadWrite(tiGPU); qGPU = em.CreateEntityQuery(a);
                var b = new Il2CppStructArray<ComponentType>(1); b[0] = ComponentType.ReadOnly(tiSing); qSing = em.CreateEntityQuery(b);
                try
                {
                    tiStation = TypeManager.GetTypeIndex(Il2CppType.Of<PlanetStationData>());
                    var c = new Il2CppStructArray<ComponentType>(2); c[0] = ComponentType.ReadOnly(tiStation); c[1] = ComponentType.ReadOnly(Ecs.tiLTW); qStation = em.CreateEntityQuery(c);
                }
                catch (Exception ex) { Registro.Aviso("radar/write: PlanetStationData not available (" + ex.Message + ") — the uv formula will not be checked"); }
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("radar/write: not available (" + ex.Message + ")"); return false; }
        }

        /// <summary>Cabecalho do buffer (BufferHeader no chunk: Pointer +0, Length +8, Capacity +12). GetComponentDataRawRW num tipo
        /// buffer devolve o cabecalho (o IL2CPP sem checks nao reclama).</summary>
        static unsafe byte* Cabecalho(EntityManager em, Entity e) => (byte*)em.GetComponentDataRawRW(e, tiGPU);
        static unsafe byte* Elementos(byte* h) { var p = *(byte**)h; return p != null ? p : h + 16; }
        /// <summary>Garante capacidade pra n elementos (igual BufferHeader.EnsureCapacity: novo bloco Persistent, copia, solta o velho).</summary>
        static unsafe bool Garantir(byte* h, int n)
        {
            int cap = *(int*)(h + 12); if (n <= cap) return true;
            if (cap < 0 || cap > 100000) return false;
            int novaCap = Math.Max(n, cap * 2);
            var novo = (byte*)UnsafeUtility.Malloc((long)novaCap * T, 16, Allocator.Persistent); if (novo == null) return false;
            int len = *(int*)(h + 8); var velho = Elementos(h);
            for (int i = 0; i < len * T; i++) novo[i] = velho[i];
            var heap = *(byte**)h; if (heap != null) UnsafeUtility.Free(heap, Allocator.Persistent);
            *(byte**)h = novo; *(int*)(h + 12) = novaCap;
            return true;
        }

        // 0.19.3 (log da 0.19.2: "hits #0=0 #1=0 #2=0" — eu normalizava SEMPRE, e o icone da base fica perto do centro da tela, nao na
        // borda): a uv e' a projecao em si; so quem cai fora do circulo (comprimento > 1) e' trazido pra borda. Variantes: #0 projecao
        // direta (ortografica), #1 com divisao por w (perspectiva), #2 relativa ao planeta.
        static unsafe float2 Uv(float4x4 w2p, float3 pos, float3 planeta, int v)
        {
            float4 p = v == 2 ? new float4(pos - planeta, 1f) : new float4(pos, 1f);
            var r = math.mul(w2p, p);
            var uv = new float2(r.x, r.y);
            if (v == 1 && Math.Abs(r.w) > 1e-6f) uv /= r.w;
            float c = math.length(uv);
            return c > 1f ? uv / c : uv;
        }

        // 0.19.3 (Andre: "a cada simulacao ele deixa um icone velho no radar"): quando o SCTick_Radar para de refazer o buffer (fim da
        // simulacao), o que eu acrescentei ficava la. Guardo os bytes que escrevi e onde; se no frame seguinte o buffer ainda esta
        // exatamente assim (mesmo comprimento, mesmos bytes = o jogo nao refez), encurto de volta antes de escrever de novo (ou de nao escrever).
        static Entity donoEscrito; static int nAntes, kDepois; static byte[] escrito;
        static unsafe void Limpar(EntityManager em)
        {
            if (escrito == null) return;
            try
            {
                if (Ecs.Vivo(em, donoEscrito) && Ecs.TemC(em, donoEscrito, tiGPU))
                {
                    var h = Cabecalho(em, donoEscrito);
                    if (h != null && *(int*)(h + 8) == kDepois)
                    {
                        var el = Elementos(h); bool igual = true;
                        for (int i = 0; i < escrito.Length && igual; i++) if (el[nAntes * T + i] != escrito[i]) igual = false;
                        if (igual) *(int*)(h + 8) = nAntes;
                    }
                }
            }
            catch (Exception) { }
            escrito = null;
        }
        public static void Esquecer() { escrito = null; }

        /// <summary>Escreve os icones dos beacons (lista de (posicao no mundo, icone)) no buffer do radar. Chamar 1x por frame, depois das poses
        /// (tambem com a lista vazia: ai so tira o que ficou do frame anterior).</summary>
        public static unsafe void Escrever(EntityManager em, List<(float3 pos, uint icone)> icones)
        {
            if (!Preparar(em)) return;
            if (frame == UnityEngine.Time.frameCount) return; frame = UnityEngine.Time.frameCount;
            Limpar(em);
            if (icones.Count == 0) return;
            try
            {
                var sings = Ecs.Lista(qSing); if (sings.Count == 0) return;
                var sp = (byte*)em.GetComponentDataRawRO(sings[0], tiSing); if (sp == null) return;
                if (sp[153] == 0) return;   // _anyActiveRadar
                var view = *(float4x4*)(sp + 8); var proj = *(float4x4*)(sp + 72);
                var w2p = math.mul(proj, view);
                var donos = Ecs.Lista(qGPU); if (donos.Count == 0) return;
                var h = Cabecalho(em, donos[0]); if (h == null) return;
                int n = *(int*)(h + 8); if (n < 0 || n > 4096) return;
                var el = Elementos(h);
                // 1x: qual variante da formula reproduz o icone da base (tipo 0) que o jogo escreveu
                if (variante < 0) variante = Conferir(em, w2p, view, proj, el, n);
                int v = variante < 0 ? 0 : variante;
                float3 planeta = default;   // (so a variante 2 usa; sem o FrameData, planeta = origem)
                // nao duplica: se o jogo ja pos um icone com o nosso tipo (fantasma pego pelo job), pula esse
                var tiposNoBuffer = new HashSet<uint>(); for (int i = 0; i < n; i++) tiposNoBuffer.Add(*(uint*)(el + i * T + 8));
                int add = 0;
                foreach (var (pos, icone) in icones) if (!tiposNoBuffer.Contains(icone)) add++;
                if (add == 0) return;
                if (!Garantir(h, n + add)) { if (logs++ < 3) Registro.Aviso("radar/write: could not grow the radar buffer (" + n + " + " + add + ")"); return; }
                el = Elementos(h); int k = n;
                foreach (var (pos, icone) in icones)
                {
                    if (tiposNoBuffer.Contains(icone)) continue;
                    var uv = Uv(w2p, pos, planeta, v);
                    var q = el + k * T; *(float2*)q = uv; *(uint*)(q + 8) = icone; *(float*)(q + 12) = 0f; k++;
                }
                *(int*)(h + 8) = k; escritos++;
                donoEscrito = donos[0]; nAntes = n; kDepois = k; escrito = new byte[(k - n) * T];
                for (int i = 0; i < escrito.Length; i++) escrito[i] = el[n * T + i];
                tLog += UnityEngine.Time.deltaTime;
                string s = "radar/write: " + (k - n) + " beacon icon(s) added to the radar buffer (" + n + " from the game), formula " + (variante < 0 ? "unchecked" : "#" + variante);
                if (s != ultimo && logs++ < 12) { ultimo = s; Registro.Info(s); }
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("radar/write: " + ex.Message + " — turning it off"); }
        }

        /// <summary>Compara as 3 variantes com os icones tipo 0 do jogo (estacoes): devolve a que bate (ou -1 se nao da pra conferir ainda).</summary>
        static unsafe int Conferir(EntityManager em, float4x4 w2p, float4x4 view, float4x4 proj, byte* el, int n)
        {
            try
            {
                if (qStation.Equals(default(EntityQuery)) || tiStation.Value == 0) return 0;
                var alvos = new List<float2>();
                for (int i = 0; i < n; i++) if (*(uint*)(el + i * T + 8) == 0) alvos.Add(*(float2*)(el + i * T));
                if (alvos.Count == 0) return -1;
                var ests = Ecs.Lista(qStation); if (ests.Count == 0) return -1;
                int[] acertos = new int[3]; float[] erro = new float[3];
                Entity perto = Entity.Null; float dPerto = float.MaxValue; var olho = UnityEngine.Camera.main != null ? (float3)UnityEngine.Camera.main.transform.position : default;
                foreach (var e in ests)
                {
                    var pos = Ecs.Posicao(em, e);
                    float d = math.length(pos - olho); if (d < dPerto) { dPerto = d; perto = e; }
                    for (int v = 0; v < 3; v++)
                    {
                        var uv = Uv(w2p, pos, default, v);
                        float melhor = 9f; foreach (var a in alvos) melhor = Math.Min(melhor, math.length(a - uv));
                        if (melhor < 0.05f) acertos[v]++; erro[v] += melhor;
                    }
                }
                int best = 0; for (int v = 1; v < 3; v++) if (acertos[v] > acertos[best] || (acertos[v] == acertos[best] && erro[v] < erro[best])) best = v;
                var sb = new System.Text.StringBuilder();
                sb.Append("radar/write: uv formula check against ").Append(alvos.Count).Append(" station icon(s) and ").Append(ests.Count).Append(" station(s): hits #0=").Append(acertos[0]).Append(" #1=").Append(acertos[1]).Append(" #2=").Append(acertos[2]).Append(" -> using #").Append(best).Append(acertos[best] == 0 ? " (NO match — icons may be misplaced; send the log)" : "");
                sb.Append("\n  station icons in the buffer:"); foreach (var a in alvos) sb.Append(" (").Append(a.x.ToString("F3")).Append(',').Append(a.y.ToString("F3")).Append(')');
                if (!Ecs.Nulo(perto))
                {
                    var pos = Ecs.Posicao(em, perto); var r = math.mul(w2p, new float4(pos, 1f));
                    sb.Append("\n  nearest station ").Append(perto.Index).Append(" at ").Append(Ecs.F3(pos)).Append(" (").Append(dPerto.ToString("F0")).Append(" m from the camera): raw projection (").Append(r.x.ToString("F3")).Append(',').Append(r.y.ToString("F3")).Append(',').Append(r.z.ToString("F3")).Append(',').Append(r.w.ToString("F3")).Append(')');
                    for (int v = 0; v < 3; v++) { var uv = Uv(w2p, pos, default, v); sb.Append(" #").Append(v).Append("=(").Append(uv.x.ToString("F3")).Append(',').Append(uv.y.ToString("F3")).Append(')'); }
                }
                sb.Append("\n  view row0 ").Append(Ecs.F3(view.c0.xyz)).Append(" c3 ").Append(Ecs.F3(view.c3.xyz)).Append("; proj c0 ").Append(Ecs.F3(proj.c0.xyz)).Append(" c1 ").Append(Ecs.F3(proj.c1.xyz)).Append(" c3 ").Append(Ecs.F3(proj.c3.xyz));
                Registro.Info(sb.ToString());
                if (acertos[best] == 0) return ++tentativas >= 4 ? 0 : -1;   // tenta de novo nos proximos frames (ate 4x)
                return best;
            }
            catch (Exception ex) { Registro.Aviso("radar/write: formula check: " + ex.Message); return 0; }
        }
    }
}
