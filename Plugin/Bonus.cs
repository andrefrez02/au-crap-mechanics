using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>Cabos extras por motor liberado (acumulativo). Depois vira missao; por enquanto a condicao e' "o motor esta
    /// disponivel no inventario" (Core.Singleton.GetMaxAvailableComponents > 0).
    ///   Motor Pequeno: +100 Data Cable, +100 Power Cable
    ///   Motor Medio:   +250 Data Cable, +100 Power Cable
    ///   Motor Grande:  +250 Data Cable, +100 Power Cable
    /// Como: Core.Singleton._maxComponentsAvailability (+104) = UnsafeHashMap&lt;SCPrefab,int&gt;* (o mapa que o inventario
    /// consulta). Somo o bonus em cima do valor do jogo; se o jogo recalcular o mapa (missao completada, load), o valor
    /// muda e eu somo de novo em cima do valor novo.</summary>
    internal static class Bonus
    {
        static readonly (string motor, int data, int power)[] REGRAS = { ("SC_MotorP", 100, 100), ("SC_MotorM", 250, 100), ("SC_MotorG", 250, 100) };
        const string CaboData = "SC_DataCable", CaboPower = "SC_PowerCable";
        static TypeIndex tiCore; static EntityQuery qCore; static bool ok, falhou, layoutLogado;
        static readonly Dictionary<ulong, int> escrito = new Dictionary<ulong, int>();   // cabo -> valor que eu escrevi por ultimo
        static readonly HashSet<string> avisos = new HashSet<string>();
        static float t = 9f;

        // HashMapHelper<TKey> (Unity.Collections 2.x), dentro do UnsafeHashMap: Ptr(valores) Keys Next Buckets Count Capacity Log2MinGrowth BucketCapacity AllocatedIndex FirstFreeIdx SizeOfTValue
        unsafe struct Mapa { public byte* Ptr; public ulong* Keys; public int* Next; public int* Buckets; public int Count, Capacity, Log2MinGrowth, BucketCapacity, AllocatedIndex, FirstFreeIdx, SizeOfTValue; }

        public static unsafe void Passo(EntityManager em, float dt)
        {
            t += dt; if (t < 1f || falhou) return; t = 0f;
            try
            {
                if (!ok)
                {
                    tiCore = TypeManager.GetTypeIndex(Il2CppType.Of<Core.Singleton>());
                    var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiCore); qCore = em.CreateEntityQuery(a); ok = true;
                }
                var es = Ecs.Lista(qCore); if (es.Count == 0) return;
                if (es.Count != nCoreLog) { nCoreLog = es.Count; Registro.Info("bonus: " + es.Count + " Core.Singleton in world"); }
                for (int ie = 0; ie < es.Count; ie++) PassoCore(em, es[ie], ie);
                Garagem(em);
            }
            catch (Exception ex) { falhou = true; Registro.Erro("bonus: " + ex); }
        }
        static int nCoreLog = -1;

        static unsafe void PassoCore(EntityManager em, Entity core, int ie)
        {
            {
                var cs = (Ecs.TemC(em, core, tiCore) ? (byte*)em.GetComponentDataRawRW(core, tiCore) : null); if (cs == null) return;
                var m = *(Mapa**)(cs + 104); if (m == null || m->Keys == null || m->Ptr == null) return;
                if (!layoutLogado) { layoutLogado = true; Registro.Info("bonus: availability map Count=" + m->Count + " Capacity=" + m->Capacity + " Buckets=" + m->BucketCapacity + " SizeOfTValue=" + m->SizeOfTValue); }
                if (m->SizeOfTValue != 4 || m->Count <= 0 || m->Count > m->Capacity || m->BucketCapacity <= 0) { falhou = true; Registro.Aviso("bonus: the map doesn't have the expected format — disabled"); return; }

                int bData = 0, bPower = 0; var liberados = new List<string>();
                foreach (var r in REGRAS) { int v = Ler(m, Ecs.HashPrefab(r.motor), out bool achou); if (achou && v > 0) { bData += r.data; bPower += r.power; liberados.Add(r.motor); } }
                Aplicar(m, 0, CaboData, bData, liberados); Aplicar(m, 0, CaboPower, bPower, liberados);
                // +1 Radar quando o jogador tem liberado tudo que monta um carro (um de cada; 4 rodas; 4 bearings)
                bool carro = Tem(m, "SC_DecouplerQuarter", 1) && Tem(m, "SC_Recoupler", 1) && Tem(m, "SC_Beacon", 1) && Tem(m, "SC_Bearing", 4)
                             && (Tem(m, "SC_MotorP", 1) || Tem(m, "SC_MotorM", 1) || Tem(m, "SC_MotorG", 1))
                             && Soma(m, RODAS) >= 4;
                var lr = carro ? new List<string> { "complete car kit" } : new List<string>();
                Aplicar(m, 0, "SC_Radar", carro ? 1 : 0, lr);
                maxRadar = Ler(m, Ecs.HashPrefab("SC_Radar"), out _);
                // 0.7.3: a garagem NAO usa o mapa acima pra limitar: GarageGrabber.SetUsedComponentsCountMapJob (Burst) le
                // Core.Singleton._standaloneComponentsAvailability (+96), soma por grupo (SCPrefabData +0x3c) e tira o usado ->
                // _remainingComponentsMapPtr. Entao o bonus vai nos dois mapas (o +104 e' o que o inventario mostra).
                var sm = *(Mapa**)(cs + 96);
                if (sm != null && sm->Keys != null && sm->Ptr != null && sm->SizeOfTValue == 4 && sm->Count > 0 && sm->Count <= sm->Capacity && sm->BucketCapacity > 0)
                {
                    if (!layoutSLogado) { layoutSLogado = true; Registro.Info("bonus: standalone map Count=" + sm->Count + " Capacity=" + sm->Capacity + "; before bonus: Radar=" + Ler(sm, Ecs.HashPrefab("SC_Radar"), out _) + " DataCable=" + Ler(sm, Ecs.HashPrefab(CaboData), out _) + " PowerCable=" + Ler(sm, Ecs.HashPrefab(CaboPower), out _)); }
                    Aplicar(sm, 1, CaboData, bData, liberados); Aplicar(sm, 1, CaboPower, bPower, liberados);
                    Aplicar(sm, 1, "SC_Radar", carro ? 1 : 0, lr);
                    standRadar = Ler(sm, Ecs.HashPrefab("SC_Radar"), out _);
                }
                else if (avisos.Add("standalone")) Registro.Aviso("bonus: standalone map (+96) doesn't have the expected format — only the maximum was changed");
                // 0.24.0 (Andre: "+2000 de cada micro frame"): blocos extras da lista do .cfg ([Bonus] extra_blocks). Serve pra
                // qualquer bloco, inclusive de OUTRO mod — o Aplicar so escreve em chave que JA existe no mapa, nunca insere.
                var extras = Extras();
                if (extras.Count > 0)
                {
                    var porque = new List<string> { "[Bonus] extra_blocks" };
                    foreach (var kv in extras)
                    {
                        Aplicar(m, 0, kv.Key, kv.Value, porque);
                        if (sm != null && sm->Keys != null && sm->Ptr != null && sm->SizeOfTValue == 4 && sm->Count > 0) Aplicar(sm, 1, kv.Key, kv.Value, porque);
                    }
                }
            }
        }

        /// <summary>Diagnostico do radar: o inventario/garagem usa GarageGrabberSingleton._remainingComponentsMapPtr (+216,
        /// "quantos ainda posso por") e _isComponentsLimitOverflow (+129, "passou do limite" -> nao deixa voar). Loga quando muda:
        /// maximo no mapa do Core, restante na garagem, quantos radares existem no mundo.</summary>
        static int maxRadar = -1, standRadar = -1; static bool layoutSLogado; static string radarLog; static TypeIndex tiGar; static EntityQuery qGar; static bool garOk;
        static unsafe void Garagem(EntityManager em)
        {
            try
            {
                if (!garOk) { garOk = true; tiGar = TypeManager.GetTypeIndex(Il2CppType.Of<GarageGrabberSingleton>()); var a = new Il2CppStructArray<ComponentType>(1); a[0] = ComponentType.ReadOnly(tiGar); qGar = em.CreateEntityQuery(a); }
                string rest = "no garage", over = "?";
                var gs = Ecs.Lista(qGar);
                if (gs.Count > 0)
                {
                    var g = (Ecs.TemC(em, gs[0], tiGar) ? (byte*)em.GetComponentDataRawRO(gs[0], tiGar) : null);
                    if (g != null)
                    {
                        over = (*(g + 129) != 0).ToString();
                        var mr = *(Mapa**)(g + 216);
                        if (mr == null) rest = "null map";
                        else if (mr->SizeOfTValue != 4 || mr->Count < 0 || mr->Count > mr->Capacity || mr->BucketCapacity <= 0) rest = "map with an odd format";
                        else { int v = Ler(mr, Ecs.HashPrefab("SC_Radar"), out bool achou); rest = achou ? v.ToString() : "not in map"; }
                    }
                }
                ulong h1 = Ecs.HashPrefab("SC_Radar"), h2 = Ecs.HashPrefab("SC_Radar M"); int n = 0;
                foreach (var e in Ecs.Componentes(em)) { ulong h = Ecs.Prefab(em, e); if (h == h1 || h == h2) n++; }
                string s = "bonus/radar: max in Core=" + maxRadar + ", standalone=" + standRadar + ", remaining in garage=" + rest + ", garage over limit=" + over;
                if (s != radarLog) { radarLog = s; Registro.Info(s + ", radars in world=" + n); }   // (0.7.8: a contagem pisca entre garagem e voo — so loga quando o resto muda)
            }
            catch (Exception ex) { if (radarLog != "error") { radarLog = "error"; Registro.Aviso("bonus/radar diagnostics: " + ex.Message); } }
        }

        static unsafe void Aplicar(Mapa* m, int idMapa, string cabo, int bonus, List<string> liberados)
        {
            ulong hp = Ecs.HashPrefab(cabo), h = hp ^ ((ulong)idMapa * 0x9E3779B97F4A7C15UL);
            string nm = idMapa == 0 ? "" : " [standalone]";
            int* v = Achar(m, hp); if (v == null) { if (avisos.Add(cabo + nm)) Registro.Aviso("bonus: " + cabo + nm + " not in map (not unlocked yet?)"); return; }
            escrito.TryGetValue(h, out int ultimo); bool temUltimo = escrito.ContainsKey(h);
            int baseJogo = temUltimo && *v == ultimo ? ultimo - Bonus_(h) : *v;
            int alvo = baseJogo + bonus;
            if (*v == alvo) return;
            *v = alvo; escrito[h] = alvo; bonusAtual[h] = bonus;
            Registro.Info("bonus: " + cabo + nm + " = " + baseJogo + " + " + bonus + " = " + alvo + " (from: " + string.Join(", ", liberados) + ")");
        }
        static readonly Dictionary<ulong, int> bonusAtual = new Dictionary<ulong, int>();
        static int Bonus_(ulong h) => bonusAtual.TryGetValue(h, out var b) ? b : 0;

        /// <summary>0.24.0: "SC_X=2000, SC_Y=50" do .cfg -> lista. Linha vazia = nenhum extra (o padrao).</summary>
        public static string ExtrasCfg = "";
        static string extrasLidas; static Dictionary<string, int> extrasCache = new Dictionary<string, int>();
        static Dictionary<string, int> Extras()
        {
            if (extrasLidas == ExtrasCfg) return extrasCache;
            extrasLidas = ExtrasCfg; extrasCache = new Dictionary<string, int>();
            foreach (var pedaco in (ExtrasCfg ?? "").Split(',', ';'))
            {
                var s = pedaco.Trim(); if (s.Length == 0) continue;
                int i = s.IndexOf('='); if (i <= 0) { if (avisos.Add("extra:" + s)) Registro.Aviso("bonus: [Bonus] extra_blocks: '" + s + "' is not in the 'SC_Name=amount' format"); continue; }
                var nome = s.Substring(0, i).Trim();
                if (!int.TryParse(s.Substring(i + 1).Trim(), out int n) || n == 0) { if (avisos.Add("extra:" + s)) Registro.Aviso("bonus: [Bonus] extra_blocks: '" + s + "' has no valid amount"); continue; }
                extrasCache[nome] = n;
            }
            if (extrasCache.Count > 0) Registro.Info("bonus: " + extrasCache.Count + " block(s) with extra amount from [Bonus] extra_blocks");
            return extrasCache;
        }

        static readonly string[] RODAS = { "SC_RodaSportsP", "SC_RodaSportsM", "SC_RodaSportsG", "SC_RodaOffroadP", "SC_RodaOffroadM", "SC_RodaOffroadG" };
        static unsafe bool Tem(Mapa* m, string nome, int n) { int v = Ler(m, Ecs.HashPrefab(nome), out bool achou); return achou && v >= n; }
        static unsafe int Soma(Mapa* m, string[] nomes) { int t = 0; foreach (var n in nomes) { int v = Ler(m, Ecs.HashPrefab(n), out bool a); if (a && v > 0) t += v; } return t; }

        static unsafe int Ler(Mapa* m, ulong h, out bool achou) { var p = Achar(m, h); achou = p != null; return p != null ? *p : 0; }

        /// <summary>Anda pelas listas de cada bucket (so entradas vivas) procurando a chave.</summary>
        static unsafe int* Achar(Mapa* m, ulong h)
        {
            for (int b = 0; b < m->BucketCapacity; b++)
            {
                int i = m->Buckets[b], guarda = 0;
                while (i >= 0 && i < m->Capacity && guarda++ < 4096)
                {
                    if (m->Keys[i] == h) return (int*)m->Ptr + i;
                    i = m->Next[i];
                }
            }
            return null;
        }
    }
}
