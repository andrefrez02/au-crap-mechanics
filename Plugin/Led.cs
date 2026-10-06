using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace Mecanica
{
    /// <summary>0.19.2 (Andre: "faltou os LEDs de estado nos dois transmitters — o do jogo pisca verde / vermelho / amarelo"): o LED e'
    /// o renderer "Indicator" dos dois blocos. 0.19.3 (Andre: "os leds nao ligaram"): o CRPRendererMemory_Color NAO e' float4 + specular
    /// (sao 8 bytes: cor e specular empacotados — uint2 _data, com Unpack(out color, out specularPacked)); eu lia 16 bytes a frente e
    /// nunca achava a marca. Agora: no Shaft Transmitter (copia do transmitter do jogo) o LED e' o SCTypeWirelessTransmitter._indicatorEntity
    /// (+24); no LRT e' o filho com a marca (specular strength 0,0041 do prefab, comparada ja empacotada) ou, se a marca se perde no
    /// empacotamento, o filho de cor (0,0,0,0) (o LED e' o unico renderer com alpha 0). Pinta com as cores do jogo (SCTick_WirelessTransmitter):
    /// verde (0, 1,4, 0) ok, amarelo (0,95, 0,5, 0) interferencia (mais de um emissor no canal), vermelho (1,9, 0, 0) ninguem na outra
    /// ponta. Pisca a 1 Hz. O specular empacotado do renderer e' mantido.</summary>
    internal static class Led
    {
        public const int Apagado = 0, Verde = 1, Amarelo = 2, Vermelho = 3;
        static TypeIndex tiMem, tiTransm, tiRend, tiBounds; static bool ok, falhou; static uint marca, semMarca; static bool marcaVale;
        // 0.19.6 (Andre: "tem horas que o transmitter fica todo verde ou todo vermelho"): o LED do LRT era o renderer de cor (0,0,0,0) — antes
        // da pintura cair o CORPO tambem tem cor zero e eu pintava o corpo. Agora o LED e' reconhecido pela MALHA do indicador
        // (CRPRendererData._meshReference, +0), aprendida do _indicatorEntity de qualquer wireless transmitter (do mod ou do jogo).
        static uint malhaLed; static bool malhaOk; static float tMalha = 9f; static bool avisouMalha;
        static readonly float4[] CORES = { new float4(0f, 0f, 0f, 1f), new float4(0f, 1.4f, 0f, 1f), new float4(0.95f, 0.5f, 0f, 1f), new float4(1.9f, 0f, 0f, 1f) };
        static readonly HashSet<ulong> logados = new HashSet<ulong>(); static readonly HashSet<long> avisados = new HashSet<long>();

        static bool Preparar()
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                try { tiTransm = TypeManager.GetTypeIndex(Il2CppType.Of<SCTypeWirelessTransmitter>()); } catch (Exception ex) { Registro.Aviso("led: SCTypeWirelessTransmitter: " + ex.Message); }
                try { tiRend = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererData>()); } catch (Exception ex) { Registro.Aviso("led: CRPRendererData: " + ex.Message); }
                try { tiBounds = TypeManager.GetTypeIndex(Il2CppType.Of<CRPLocalBounds>()); } catch (Exception ex) { Registro.Aviso("led: CRPLocalBounds: " + ex.Message); }
                var sm = new SpecularSetup(); sm._strength = 0.0041f; sm._smoothness = 0f;
                new CRPRendererMemory_Color(new float4(0f, 0f, 0f, 1f), sm).Unpack(out _, out marca);
                new CRPRendererMemory_Color(new float4(0f, 0f, 0f, 1f), new SpecularSetup()).Unpack(out _, out semMarca);
                marcaVale = marca != semMarca;
                Registro.Info("led: specular marker packs to " + marca + " (no specular: " + semMarca + ")" + (marcaVale ? "" : " — marker lost in packing, using the alpha-0 renderer instead"));
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("led: " + ex.Message); return false; }
        }

        static unsafe bool Ler(EntityManager em, Entity e, out float4 cor, out uint spec)
        {
            cor = default; spec = 0;
            if (!Ecs.TemC(em, e, tiMem)) return false;
            var p = (CRPRendererMemory_Color*)em.GetComponentDataRawRO(e, tiMem); if (p == null) return false;
            (*p).Unpack(out cor, out spec); return true;
        }

        /// <summary>O renderer do LED do bloco (indicador do transmitter ou filho marcado). Entity.Null se nao achou.</summary>
        public static unsafe Entity Achar(EntityManager em, Entity bloco)
        {
            if (!Preparar() || !Ecs.Vivo(em, bloco)) return Entity.Null;
            var achado = Entity.Null; string como = "";
            if (tiTransm.Value != 0 && Ecs.TemC(em, bloco, tiTransm))
            {
                var p = (byte*)em.GetComponentDataRawRO(bloco, tiTransm);
                if (p != null) { var ind = *(Entity*)(p + 24); if (Ecs.Vivo(em, ind) && Ecs.TemC(em, ind, tiMem)) { achado = ind; como = "the transmitter's indicator entity"; } }
            }
            if (Ecs.Nulo(achado))
            {
                // 0.19.6b (log: "mesh ref 1" em todo mundo — o _meshReference nao distingue; o corpo continuava verde): o LED e' o menor
                // renderer com cor do bloco (CRPLocalBounds: o indicador do transmitter tem poucos milimetros; corpo, tela e etiqueta sao de cm)
                float menor = float.MaxValue;
                foreach (var f in Ecs.Filhos(em, bloco))
                {
                    if (!Ler(em, f, out var cor, out var spec)) continue;
                    if (marcaVale && spec == marca) { achado = f; como = "the specular marker"; menor = 0f; break; }
                    float ext = Extensao(em, f);
                    if (ext < menor) { menor = ext; achado = f; como = "the smallest coloured renderer (extent " + (ext * 100f).ToString("F1") + " cm)"; }
                }
                if (!Ecs.Nulo(achado) && menor > 0.03f) { if (avisados.Add(Ecs.K(bloco))) Registro.Aviso("transmitter block " + bloco.Index + ": the smallest coloured renderer is " + (menor * 100f).ToString("F1") + " cm — not an LED, leaving it off (rebuild the bundle: the LED needs the 0.19.8 prefab)"); achado = Entity.Null; }
            }
            ulong h = Ecs.Prefab(em, bloco);
            if (logados.Add(h))
            {
                var sb = new System.Text.StringBuilder("transmitter block " + bloco.Index + ": LED " + (Ecs.Nulo(achado) ? "NOT found" : "= renderer " + achado.Index + " (" + como + ")") + "; renderer children:");
                foreach (var f in Ecs.Filhos(em, bloco)) if (Ler(em, f, out var cor, out var spec)) sb.Append("\n    ").Append(f.Index).Append(" color (").Append(cor.x.ToString("F2")).Append(',').Append(cor.y.ToString("F2")).Append(',').Append(cor.z.ToString("F2")).Append(',').Append(cor.w.ToString("F2")).Append(") specular ").Append(spec).Append(" extent ").Append((Extensao(em, f) * 100f).ToString("F1")).Append(" cm mesh ").Append(Malha(em, f));
                Registro.Info(sb.ToString());
            }
            return achado;
        }

        /// <summary>Maior semi-extensao da caixa local do renderer (CRPLocalBounds: AABB = Center float3 + Extents float3).</summary>
        static unsafe float Extensao(EntityManager em, Entity e)
        {
            if (tiBounds.Value == 0 || !Ecs.TemC(em, e, tiBounds)) return float.MaxValue;
            var p = (float*)em.GetComponentDataRawRO(e, tiBounds); if (p == null) return float.MaxValue;
            return Math.Max(Math.Abs(p[3]), Math.Max(Math.Abs(p[4]), Math.Abs(p[5])));
        }
        static unsafe uint Malha(EntityManager em, Entity e)
        {
            if (tiRend.Value == 0 || !Ecs.TemC(em, e, tiRend)) return 0xffffffff;
            var p = (uint*)em.GetComponentDataRawRO(e, tiRend); return p == null ? 0xffffffff : *p;
        }
        /// <summary>Aprende a malha do indicador com o _indicatorEntity de qualquer wireless transmitter (1 tentativa a cada 3 s).</summary>
        static unsafe void AprenderMalha(EntityManager em)
        {
            if (malhaOk || tiTransm.Value == 0 || tiRend.Value == 0) return;
            tMalha += UnityEngine.Time.deltaTime; if (tMalha < 3f) return; tMalha = 0f;
            try
            {
                foreach (var t in Ecs.Transmitters(em))
                {
                    if (!Ecs.TemC(em, t, tiTransm)) continue;
                    var p = (byte*)em.GetComponentDataRawRO(t, tiTransm); if (p == null) continue;
                    var ind = *(Entity*)(p + 24); if (!Ecs.Vivo(em, ind)) continue;
                    uint m = Malha(em, ind); if (m == 0xffffffff) continue;
                    malhaLed = m; malhaOk = true; Registro.Info("led: indicator mesh learned from transmitter " + t.Index + " (mesh ref " + m + ")"); return;
                }
            }
            catch (Exception ex) { Registro.Aviso("led: learning the indicator mesh: " + ex.Message); }
        }

        /// <summary>true na metade "acesa" do segundo.</summary>
        public static bool Aceso => (UnityEngine.Time.time % 1f) < 0.5f;

        /// <summary>Escreve a cor do estado (ou apagado na metade escura do piscar). Devolve o que foi escrito (pra nao reescrever a toa).</summary>
        public static unsafe int Pintar(EntityManager em, Entity led, int estado, int escrito)
        {
            if (!ok || !Ecs.Vivo(em, led) || !Ecs.TemC(em, led, tiMem)) return escrito;
            int quer = Aceso ? estado : Apagado;
            // (reescreve todo frame: no Shaft Transmitter o tick do jogo tambem pode pintar o indicador)
            var p = (CRPRendererMemory_Color*)em.GetComponentDataRawRW(led, tiMem); if (p == null) return escrito;
            (*p).Unpack(out _, out uint spec);
            *p = new CRPRendererMemory_Color(CORES[quer], spec);
            return quer;
        }
    }
}
