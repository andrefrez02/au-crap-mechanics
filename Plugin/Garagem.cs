using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;

namespace Mecanica
{
    /// <summary>0.20.0 (Andre: "colocar o pistao na garagem ja numa posicao especifica ... ja estendido" / "ao apertar H ... uma linha na
    /// ponta dele mostrando o curso da haste ... se o bloco estiver desalinhado a linha do pistao mais perto fica vermelha").
    /// Passo da GARAGEM (a cada 0,1 s), por bloco com GarageTransform fora de uma part:
    /// - pistao com um Piston End (SC_PistaoPonta) alinhado no eixo dele (ate o curso maximo): haste e cabeca ja ficam esticadas ate a
    ///   ponta — o curso na simulacao vai ser essa distancia (Juntas.EsperandoTopo acha a ponta do mesmo jeito);
    /// - com o H do jogo ligado (GaragebotSystem._visible = o robo da garagem com o centro de massa/peso), a barra-guia (renderer de vidro
    ///   do jogo na face de cima) estica ate curso maximo x knob, verde; fica VERMELHA no pistao mais perto de um Piston End que nao
    ///   esta alinhado com pistao nenhum. O H do jogo nao e' tocado: so lido. Se nao der pra ler, a tecla H (Input System) alterna.</summary>
    internal static class Garagem
    {
        class Info { public Entity haste, cabeca, guia; public float ext = -1f, guiaL = -1f; public int cor = -1; public bool log; public string nome; public float esc, cursoMax; public Entity ponta; public float tLancado = -1f; }
        static readonly Dictionary<long, Info> pistoes = new Dictionary<long, Info>();
        static readonly HashSet<long> vistos = new HashSet<long>();
        static TypeIndex tiGT, tiMem; static EntityQuery qGar; static bool ok, falhou; static float t = 9f; static ulong hashPonta; static bool hashOk;
        static int avisos;
        public static bool LogDetalhe = true;

        static bool Preparar(EntityManager em)
        {
            if (ok) return true; if (falhou) return false;
            try
            {
                tiGT = TypeManager.GetTypeIndex(Il2CppType.Of<GarageTransform>());
                tiMem = TypeManager.GetTypeIndex(Il2CppType.Of<CRPRendererMemory_Color>());
                var a = new Il2CppStructArray<ComponentType>(2); a[0] = ComponentType.ReadOnly(Ecs.tiPrefab); a[1] = ComponentType.ReadOnly(tiGT);
                qGar = em.CreateEntityQuery(a);
                ok = true; return true;
            }
            catch (Exception ex) { falhou = true; Registro.Aviso("garage: " + ex.Message); return false; }
        }

        /// <summary>O Garagem.cs esta cuidando do visual (haste/cabeca) deste bloco na garagem? (Juntas.RecolherNaGaragem nao mexe entao.)</summary>
        public static bool CuidaDoVisual(Entity bloco) => vistos.Contains(Ecs.K(bloco));
        public static bool Ativa => ok;
        /// <summary>0.20.1: pistoes que a garagem viu com um Piston End alinhado (pra descobrir na hora quando sao lancados).</summary>
        public static List<Entity> PistoesComPonta()
        {
            var r = new List<Entity>();
            foreach (var kv in pistoes) if (!Ecs.Nulo(kv.Value.ponta)) r.Add(Ecs.DeK(kv.Key));
            return r;
        }
        /// <summary>0.20.5 (log do Andre: num quick load o mod puxava a peca da ponta 58 cm de volta pra pose da garagem): o pistao acabou de SAIR
        /// da garagem com um Piston End (a garagem o viu ha menos de 5 s)? So nesse caso o joint nasce puxando pra pose da garagem; depois de um
        /// load / reconstrucao a pose atual e' a que vale.</summary>
        public static bool RecemLancado(Entity bloco)
        {
            // 0.24.0: o Piston End deixou de ser exigencia. Sem ele o tLancado nunca era anotado e o RecemLancado dava sempre false,
            // entao o snap de volta pra pose da garagem ([Piston] snap_back) nunca chegava a rodar nos pistoes do Andre, que nao
            // usam Piston End nenhum ("17 piston(s), 0 Piston End(s)" no log).
            if (!pistoes.TryGetValue(Ecs.K(bloco), out var i)) return false;
            return i.tLancado < 0f || UnityEngine.Time.time - i.tLancado < 5f;
        }
        /// <summary>Vao ate o Piston End alinhado com este pistao na garagem (0 se nao tem / nao e' da garagem).</summary>
        public static float ExtDe(Entity bloco) => pistoes.TryGetValue(Ecs.K(bloco), out var i) && vistos.Contains(Ecs.K(bloco)) && i.ext > 0f ? i.ext : 0f;

        public static void Passo(EntityManager em, float dt)
        {
            if (!Juntas.HashesOk || !Preparar(em)) return;
            TeclaH();   // todo frame (wasPressedThisFrame)
            t += dt; if (t < (pistoes.Count > 0 ? 0.1f : 0.5f)) return; t = 0f;   // sem pistao na garagem (voando): varredura mais rara
            try { Varrer(em); }
            catch (Exception ex) { if (avisos++ < 3) Registro.Aviso("garage pass: " + ex.Message); }
        }

        /// <summary>0.23.1: pose da garagem de um bloco (Anel.cs usa pra parear anel x junta na garagem).</summary>
        public static unsafe bool GT(EntityManager em, Entity e, out RigidTransform gt)
        {
            gt = default;
            if (!Ecs.TemC(em, e, tiGT)) return false;
            var p = (RigidTransform*)em.GetComponentDataRawRO(e, tiGT); if (p == null) return false;
            gt = *p; return true;
        }

        /// <summary>Piston End `gE` alinhado com o pistao `gP` (meio = metade do lado do pistao)? gap = vao entre a face de cima do pistao e a
        /// face de baixo da ponta (= curso); ax/lat = ao longo do eixo / de lado (pra diagnostico).</summary>
        /// <summary>0.22.4: meia espessura do Piston End ao longo do eixo do pistao: cubo antigo (SC_PistaoPonta) 0,125; placas novas 0,0625.</summary>
        public static float MeiaPonta(string nome) => 0.0625f;   // 0.22.5: so as placas (o cubo saiu)
        /// <summary>0.22.4: blocos da garagem (SCPrefab + GarageTransform) — pro Anel.cs pintar os plugues na garagem tambem.</summary>
        public static List<Entity> Blocos(EntityManager em) => Preparar(em) ? Ecs.Lista(qGar) : new List<Entity>();
        public static bool Alinhada(RigidTransform gP, float meio, float cursoMax, RigidTransform gE, out float gap, out float ax, out float lat, float meiaPonta = 0.125f)
        {
            var up = Ecs.Rot(gP.rot, Ecs.UY); var d = Ecs.Sub(gE.pos, gP.pos);
            ax = Ecs.Dot(d, up); lat = Ecs.Comp(Ecs.Sub(d, Ecs.Mul(up, ax)));
            gap = ax - meio - meiaPonta;
            if (lat > 0.02f) return false;
            if (Math.Abs(Ecs.Dot(Ecs.Rot(gE.rot, Ecs.UY), up)) < 0.99f) return false;   // ponta de lado: o soquete nao olha pro pistao
            return gap >= -0.002f && gap <= cursoMax + 0.002f;
        }

        /// <summary>Na simulacao (Juntas.EsperandoTopo): o Piston End alinhado com este pistao, pela pose da GARAGEM dos dois. Entity.Null se nao ha.</summary>
        public static bool AcharPonta(EntityManager em, Entity pistao, float meio, float cursoMax, out Entity ponta, out float gap, out string motivo)
        {
            ponta = Entity.Null; gap = 0f; motivo = null;
            if (!Preparar(em)) { motivo = "garage helper not ready"; return false; }
            if (!GT(em, pistao, out var gP)) { motivo = "the piston has no GarageTransform"; return false; }
            float melhor = 999f; int perto = 0;
            foreach (var e in Ecs.Componentes(em))
            {
                ulong h; try { h = Ecs.Prefab(em, e); } catch (Exception) { continue; }
                if (!Juntas.TipoDoHash(h, out var tp, out _, out var np) || tp != Tipo.Ponta || !GT(em, e, out var gE)) continue;   // 0.22.4: cubo antigo + placas
                if (Alinhada(gP, meio, cursoMax, gE, out var g, out var ax, out var lat, MeiaPonta(np))) { if (g < melhor) { melhor = g; ponta = e; gap = g; } }
                else if (Ecs.Comp(Ecs.Sub(gE.pos, gP.pos)) < cursoMax + 1f) perto++;
            }
            if (Ecs.Nulo(ponta)) { motivo = perto > 0 ? perto + " Piston End(s) nearby but not on this piston's axis" : "no Piston End on this piston's axis"; return false; }
            return true;
        }

        static void Varrer(EntityManager em)
        {
            bool h = HLigado(em);
            var pist = new List<(Entity e, RigidTransform gt, float esc, float cursoMax, string nome)>();
            var pontas = new List<(Entity e, RigidTransform gt, float meia)>();
            foreach (var e in Ecs.Lista(qGar))
            {
                if (Ecs.EhPart(em, Ecs.Pai(em, e))) continue;   // lancado: a Junta cuida
                ulong hh; try { hh = Ecs.Prefab(em, e); } catch (Exception) { continue; }
                if (!Juntas.TipoDoHash(hh, out var tipo, out var curso, out var nome)) continue;
                if (tipo == Tipo.Ponta) { if (GT(em, e, out var g)) pontas.Add((e, g, MeiaPonta(nome))); }
                else if (tipo == Tipo.Pistao) { if (GT(em, e, out var g)) pist.Add((e, g, Juntas.EscalaDoNome(nome), curso, nome)); }
            }
            vistos.Clear();
            // pistao -> ponta alinhada (a mais perto)
            var pontaDe = new Dictionary<long, (Entity ponta, float gap)>(); var usadas = new HashSet<long>();
            foreach (var p in pist)
            {
                float melhor = 999f; Entity pe = Entity.Null;
                foreach (var q in pontas) if (Alinhada(p.gt, 0.25f * p.esc, p.cursoMax, q.gt, out var g, out _, out _, q.meia) && g < melhor) { melhor = g; pe = q.e; }
                if (!Ecs.Nulo(pe)) { pontaDe[Ecs.K(p.e)] = (pe, melhor); usadas.Add(Ecs.K(pe)); }
            }
            // ponta solta (sem pistao) -> o pistao mais perto fica vermelho
            var vermelhos = new HashSet<long>();
            foreach (var q in pontas)
            {
                if (usadas.Contains(Ecs.K(q.e))) continue;
                float melhor = 999f; long kp = 0;
                foreach (var p in pist) { float d = Ecs.Comp(Ecs.Sub(q.gt.pos, p.gt.pos)); if (d < melhor && d < p.cursoMax + 1f) { melhor = d; kp = Ecs.K(p.e); } }
                if (kp != 0) vermelhos.Add(kp);
            }
            foreach (var p in pist)
            {
                long k = Ecs.K(p.e); vistos.Add(k);
                if (!pistoes.TryGetValue(k, out var inf)) { inf = new Info { esc = p.esc, cursoMax = p.cursoMax, nome = p.nome }; pistoes[k] = inf; }
                if (!Ecs.Vivo(em, inf.haste) || !Ecs.Vivo(em, inf.cabeca) || (!Ecs.Vivo(em, inf.guia) && inf.guiaL != -2f))
                {
                    Juntas.AcharPartesPistao(em, p.e, p.esc, p.cursoMax, out inf.haste, out inf.cabeca, out inf.guia);
                    inf.ext = -1f; inf.guiaL = Ecs.Nulo(inf.guia) ? -2f : -1f; inf.cor = -1;   // -2 = este prefab nao tem guia (2/4/8): nao procura de novo
                    if (!inf.log) { inf.log = true; if (LogDetalhe) Registro.Info(p.nome + " " + p.e.Index + ": garage — rod " + (Ecs.Nulo(inf.haste) ? "NOT found" : "ok") + ", head " + (Ecs.Nulo(inf.cabeca) ? "NOT found" : "ok") + ", guide bar " + (Ecs.Nulo(inf.guia) ? "none" : "ok")); }
                }
                // haste/cabeca: esticadas ate a ponta alinhada (0 sem ponta)
                float ext = 0f; Entity ponta = Entity.Null;
                if (pontaDe.TryGetValue(k, out var pg)) { ext = pg.gap; ponta = pg.ponta; }
                // 0.20.2: confere pela POSICAO REAL da cabeca (nao por valor guardado): se alguem recolheu/mexeu, poe de volta
                float yCab = float.NaN; try { if (Ecs.Vivo(em, inf.cabeca)) yCab = Ecs.LT(em, inf.cabeca).Position.y; } catch (Exception) { }
                bool mexida = !float.IsNaN(yCab) && Math.Abs(yCab - (Juntas.CabecaYDe(p.esc) + ext)) > 0.002f;
                if (Math.Abs(ext - inf.ext) > 0.0005f || mexida)
                {
                    bool mudou = Math.Abs(ext - inf.ext) > 0.0005f;
                    Juntas.PorVisualPistao(em, inf.haste, inf.cabeca, ext, p.esc); inf.ext = ext;
                    if (LogDetalhe && mudou) Registro.Info(p.nome + " " + p.e.Index + ": garage — " + (Ecs.Nulo(ponta) ? "no Piston End on its axis: rod retracted" : "Piston End " + ponta.Index + " at " + ext.ToString("F3") + " m: rod drawn extended (stroke will be " + ext.ToString("F3") + " m)"));
                }
                inf.ponta = ponta;
                // barra-guia (so com o H ligado): curso maximo x knob; vermelha se uma ponta solta esta mais perto deste pistao
                if (Ecs.Nulo(inf.guia) || !Ecs.Vivo(em, inf.guia)) continue;
                float L = 0.0005f; int cor = -1;
                if (h)
                {
                    float frac = 1f;
                    try { var kn = Knobs.Achar(em, p.e); float v = Knobs.Valor(em, kn); if (!float.IsNaN(v)) frac = Knobs.FracaoCurso(v); } catch (Exception) { }
                    L = Math.Max(0.01f, p.cursoMax * frac); cor = vermelhos.Contains(k) ? 1 : 0;
                }
                if (Math.Abs(L - inf.guiaL) > 0.0005f) { Ecs.EscalaY(em, inf.guia, L); inf.guiaL = L; }
                if (cor >= 0 && cor != inf.cor) { Pintar(em, inf.guia, cor); inf.cor = cor; }
            }
            // pistoes que sairam da garagem (lancados / apagados): esconde a guia; esquece — menos os lancados com Piston End, que ficam
            // na lista (0.20.1: o Juntas descobre na hora por ela) ate a Junta existir ou a entity morrer
            var fora = new List<long>();
            foreach (var kv in pistoes) if (!vistos.Contains(kv.Key)) fora.Add(kv.Key);
            foreach (var k in fora)
            {
                var inf = pistoes[k]; var e = Ecs.DeK(k);
                try { if (Ecs.Vivo(em, inf.guia) && inf.guiaL > 0.001f) { Ecs.EscalaY(em, inf.guia, 0.0005f); inf.guiaL = 0.0005f; } } catch (Exception) { }
                if (Ecs.Vivo(em, e) && !Juntas.JuntaAtiva(e)) { if (inf.tLancado < 0f) inf.tLancado = UnityEngine.Time.time; continue; }   // 0.20.5: fica ate o motor nascer (RecemLancado); 0.24.0: todo pistao, com ou sem Piston End
                pistoes.Remove(k);
            }
            if (h != hAntes) { hAntes = h; if (LogDetalhe) Registro.Info("garage: H display " + (h ? "on" : "off") + (hFonte == 2 ? " (H key)" : " (game's garage bot)") + " — " + pist.Count + " piston(s), " + pontas.Count + " Piston End(s)"); }
        }
        static bool hAntes;

        static readonly float4 Verde = new float4(0.25f, 0.95f, 0.35f, 1f), Vermelho = new float4(0.98f, 0.12f, 0.08f, 1f);
        static unsafe void Pintar(EntityManager em, Entity guia, int cor)
        {
            if (!Ecs.TemC(em, guia, tiMem)) { if (avisos++ < 3) Registro.Aviso("garage: the guide bar has no CRPRendererMemory_Color (rebuild the bundle in the Modkit)"); return; }
            var p = (CRPRendererMemory_Color*)em.GetComponentDataRawRW(guia, tiMem); if (p == null) return;
            (*p).Unpack(out _, out uint spec);
            *p = new CRPRendererMemory_Color(cor == 1 ? Vermelho : Verde, spec);
        }

        // ---------------------------------------------------------------- H do jogo
        // GaragebotSystem (ISystem do jogo): o struct do system fica em SystemState.m_SystemPtr; `_visible` e' o robo da garagem (H).
        // Offsets dos campos pelo il2cpp (il2cpp_field_get_offset conta o cabecalho do objeto, 16 bytes, nos structs): conferido lendo
        // `_entity` (tem que ser uma entity viva). Se nada disso der, a tecla H (Input System) alterna um estado proprio.
        static IntPtr sys; static float tSys = 9f; static int hFonte;   // 0 = ainda nao sabe, 1 = system do jogo, 2 = tecla
        static int offVis = -1, offEnt = -1, offSysPtr = -1; static bool hManual; static int hAviso;
        static unsafe bool HLigado(EntityManager em)
        {
            if (hFonte == 2) return hManual;
            tSys += 0.1f;
            try
            {
                if (sys == IntPtr.Zero || tSys >= 5f)
                {
                    tSys = 0f;
                    var w = World.DefaultGameObjectInjectionWorld; if (w == null || !w.IsCreated) return hManual;
                    var wu = w.Unmanaged;
                    var hd = wu.GetExistingUnmanagedSystem(Il2CppType.Of<GaragebotSystem>());
                    var m = typeof(WorldUnmanaged).GetMethod("ResolveSystemState", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(SystemHandle) }, null);
                    if (m == null) throw new InvalidOperationException("WorldUnmanaged.ResolveSystemState not found");
                    IntPtr estado = (IntPtr)System.Reflection.Pointer.Unbox(m.Invoke(wu, new object[] { hd }));
                    if (estado == IntPtr.Zero) throw new InvalidOperationException("GaragebotSystem has no SystemState (not in this world?)");
                    if (offSysPtr < 0)
                    {
                        offSysPtr = Offset(typeof(SystemState), "NativeFieldInfoPtr_m_SystemPtr");
                        offVis = Offset(typeof(GaragebotSystem), "NativeFieldInfoPtr__visible");
                        offEnt = Offset(typeof(GaragebotSystem), "NativeFieldInfoPtr__entity");
                    }
                    IntPtr s = *(IntPtr*)(estado + offSysPtr);
                    if (s == IntPtr.Zero) throw new InvalidOperationException("m_SystemPtr is null");
                    var ent = *(Entity*)(s + offEnt);
                    if (ent.Index <= 0 || !em.Exists(ent)) throw new InvalidOperationException("sanity check failed (_entity " + ent.Index + ":" + ent.Version + " is not a live entity; offsets " + offSysPtr + "/" + offEnt + "/" + offVis + ")");
                    if (sys != s && LogDetalhe) Registro.Info("garage: reading the game's H display from GaragebotSystem._visible (garage bot entity " + ent.Index + ")");
                    sys = s; hFonte = 1;
                }
                return *(bool*)(sys + offVis);
            }
            catch (Exception ex)
            {
                sys = IntPtr.Zero;
                if (hAviso++ < 1) Registro.Aviso("garage: could not read the game's H display (" + ex.Message + ") — the H key toggles the piston guide instead");
                hFonte = 2; return hManual;
            }
        }
        /// <summary>Offset do campo (dentro do struct) a partir do NativeFieldInfoPtr_* gerado pelo interop: il2cpp_field_get_offset - 16 (cabecalho).</summary>
        static int Offset(Type t, string campo)
        {
            var f = t.GetField(campo, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (f == null) throw new InvalidOperationException(t.Name + "." + campo + " not found in the interop");
            var ptr = (IntPtr)f.GetValue(null);
            int off = (int)IL2CPP.il2cpp_field_get_offset(ptr) - 16;
            if (off < 0) throw new InvalidOperationException(t.Name + "." + campo + ": offset " + (off + 16) + " too small");
            return off;
        }
        static bool teclaFalhou;
        static void TeclaH()
        {
            if (hFonte != 2 || teclaFalhou) return;
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current; if (kb == null) return;
                if (kb.hKey.wasPressedThisFrame) hManual = !hManual;
            }
            catch (Exception ex) { teclaFalhou = true; Registro.Aviso("garage: H key: " + ex.Message); }
        }

        public static void Esquecer() { pistoes.Clear(); vistos.Clear(); sys = IntPtr.Zero; }
    }
}
