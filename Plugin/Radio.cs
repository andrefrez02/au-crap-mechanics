using System;
using Il2CppInterop.Runtime;
using System.Collections.Generic;
using Unity.Entities;

namespace Mecanica
{
    /// <summary>0.18.0 (Andre: "um long range transmitter que inves de colocar o canal pela telinha ele vai colocar o canal por uma entrada de
    /// dados, que pode mudar durante a simulacao, e o range dele vai ser muito grande"; "duas portas: uma entra/sai, outra pro canal").
    /// Bloco do mod com as malhas do Wireless Transmitter: porta 0 = sinal (InOut, igual a do transmitter do jogo), porta 1 = canal (entrada
    /// de dados, arredondada). Todo frame: quem tem o sinal ligado numa SAIDA (a porta resolve como entrada) envia; quem tem o sinal ligado
    /// numa ENTRADA (a porta resolve como saida) recebe o valor do que envia no mesmo canal. Sem limite de distancia (qualquer nave no
    /// mundo). Canais proprios: nao conversa com o Wireless Transmitter do jogo.</summary>
    internal static class Radio
    {
        sealed class R { public Entity bloco, sinal, canalP, texto; public bool portasOk, logado, textoProcurado; public int canal = int.MinValue, avisos, canalTela = int.MinValue; public byte tipoLog = 255; public float tTexto; public bool pintado; public float tPintura, tLed = 9f; public Entity led; public int ledEscrito = -1;
            public int canalCand = int.MinValue; public float tCand; public Entity fonteCanal; public bool segurouLog;
            public float ultimoV = float.NaN, tUltimoV; public bool congeladoLog, semFonteLog; }   // 0.19.8: diagnostico de fonte de video congelada   // 0.19.7: fonte do canal (segura o canal se a mesma fonte ler 0)   // 0.19.6: canal candidato (so vira o canal depois de 0,3 s estavel)
        static readonly Dictionary<long, R> radios = new Dictionary<long, R>();
        public static IEnumerable<Entity> Blocos() { foreach (var r in radios.Values) yield return r.bloco; }   // 0.19.12: log de transicao
        public static int Quantos() => radios.Count;
        static readonly Dictionary<int, float> valorCanal = new Dictionary<int, float>();
        static readonly Dictionary<int, Entity> quemEnvia = new Dictionary<int, Entity>();
        static readonly HashSet<int> avisoDoisEnviam = new HashSet<int>();

        public static bool Tem(long k) => radios.ContainsKey(k);
        // 0.18.2: numero do canal na tela (texto 3D do jogo, "RendererText3D Channel", 3 casas alinhadas a direita)
        static TypeIndex tiTexto; static bool textoOk, textoFalhou;
        static readonly Unity.Mathematics.float3 PosTexto = new Unity.Mathematics.float3 { x = 0.04958624f, y = -0.01527491f, z = -0.04406795f };
        static unsafe void Tela(EntityManager em, R r, float dt)
        {
            if (textoFalhou) return;
            try
            {
                if (!textoOk) { tiTexto = TypeManager.GetTypeIndex(Il2CppInterop.Runtime.Il2CppType.Of<CRPText3DString>()); textoOk = true; }
                r.tTexto += dt;
                if (!Ecs.Vivo(em, r.texto) && (!r.textoProcurado || r.tTexto > 3f))
                {
                    r.textoProcurado = true; r.tTexto = 0f; r.texto = Entity.Null; Entity tres = Entity.Null;
                    foreach (var f in Ecs.Filhos(em, r.bloco))
                    {
                        if (!em.HasComponent(f, ComponentType.ReadOnly(tiTexto))) continue;
                        var p = Ecs.LT(em, f).Position;
                        if (System.Math.Abs(System.Math.Abs(p.x) - PosTexto.x) +   /* 0.19.0: versao M tem x espelhado */ System.Math.Abs(p.y - PosTexto.y) + System.Math.Abs(p.z - PosTexto.z) < 0.01f) { r.texto = f; break; }
                        if (em.GetBufferLength(f, tiTexto) == 3) tres = f;
                    }
                    if (Ecs.Nulo(r.texto)) r.texto = tres;
                    if (Ecs.Nulo(r.texto)) { if (r.avisos++ < 1 && Ecs.EhPart(em, Ecs.Pai(em, r.bloco))) Registro.Aviso("SC_LongRangeTransmitter " + r.bloco.Index + ": channel display not found (rebuild the bundle in the Modkit)"); return; }
                    r.canalTela = int.MinValue;
                }
                if (Ecs.Nulo(r.texto) || r.canal == r.canalTela) return;
                int n = em.GetBufferLength(r.texto, tiTexto); var b = (char*)em.GetBufferRawRW(r.texto, tiTexto); if (b == null || n <= 0) return;
                string s = r.canal.ToString(); if (s.Length > n) s = s.Substring(s.Length - n);
                s = s.PadLeft(n);
                for (int i = 0; i < n; i++) b[i] = s[i];
                r.canalTela = r.canal;
            }
            catch (Exception ex) { textoFalhou = true; Registro.Aviso("SC_LongRangeTransmitter: channel display: " + ex.Message); }
        }
        public static void Esquecer() { radios.Clear(); valorCanal.Clear(); quemEnvia.Clear(); }
        public static void Novo(Entity e) { radios[Ecs.K(e)] = new R { bloco = e }; Registro.Info("SC_LongRangeTransmitter found: block " + e.Index + ":" + e.Version); }

        static unsafe byte TipoRuntime(EntityManager em, Entity p)
        {
            // 0.19.3 (crash dump 01/10, 16:47: AV lendo [porta+21] — a porta cacheada tinha sido destruida quando o jogo refez o BP na
            // garagem): so le se a entity ainda existe e tem o componente
            try { if (!Ecs.TemC(em, p, Ecs.tiPort)) return 255; var b = (byte*)em.GetComponentDataRawRO(p, Ecs.tiPort); return b == null ? (byte)255 : b[21]; } catch (Exception) { return 255; }
        }

        /// <summary>0.19.0 (Andre: "o transmitter longo nao ta transmitindo dados da camera"): o sinal de video da camera e' um
        /// identificador grande na porta de dados — o Ecs.LerEntrada zera valores acima de 1e6 (o DataValue() do jogo faz isso pra mostrar
        /// numero). Aqui o valor vai cru (os 4 bytes), igual o Wireless Transmitter do jogo repassa.</summary>
        static unsafe bool LerCru(EntityManager em, Entity entrada, out float v)
        {
            v = 0f; var f = Ecs.Fonte(em, entrada, out _);
            if (Ecs.Nulo(f) || !Ecs.Vivo(em, f) || !em.HasComponent(f, ComponentType.ReadOnly(Ecs.tiPort))) return false;
            var b = (byte*)em.GetComponentDataRawRO(f, Ecs.tiPort); if (b == null) return false;
            v = *(float*)(b + 8);
            // sem filtro: o identificador pode ter qualquer bit (copia os 4 bytes como estao)
            if (!logCru && Math.Abs(v) > 1e6f) { logCru = true; Registro.Info("SC_LongRangeTransmitter: passing a large signal (" + v.ToString("G9") + " — camera video?) through the channel"); }
            return true;
        }
        static bool logCru;
        /// <summary>0.19.6 (Andre: "a camera ta piscando sem parar usando o LRT" a 3x e ate em velocidade normal; "perdendo sinal depois de
        /// um tempo"): o sinal era copiado 1x por FRAME (Update), mas o identificador do video e o monitor andam por TICK de fisica
        /// (a 3x sao 3 ticks por frame: 2 ficavam com valor velho = chuvisco); e o canal, lido entre ticks, caia pra 0 por alguns frames.
        /// Agora a copia roda no prefix/postfix dos grupos de tick dos componentes (e no Update, por garantia), e o canal so muda
        /// depois de 0,3 s estavel.</summary>
        public static void Copiar(EntityManager em, bool noTick)
        {
            if (radios.Count == 0) return;
            valorCanal.Clear(); quemEnvia.Clear();
            float agora = UnityEngine.Time.unscaledTime;
            foreach (var r in radios.Values)
            {
                if (!r.portasOk || !Ecs.Vivo(em, r.bloco)) continue;
                try
                {
                    int canal = 0;
                    bool temCanal = Ecs.LerEntrada(em, r.canalP, out var vc, out var fonte, out var trilha); if (temCanal) canal = (int)Math.Round(vc);
                    // 0.19.7 (log: os 3 LRTs iam pro canal 0 juntos ao sair do banco / no time rewind e o sinal sumia): se a MESMA fonte que
                    // dava um canal != 0 passa a ler 0, segura o canal (o jogo zera as portas nesses momentos).
                    // 0.19.8 (Andre: "saindo da atmosfera perde o sinal"; log: canal 0 sem o "holding" = a FONTE sumiu, nao leu 0): segura
                    // tambem quando o caminho ate a fonte some, e loga 1x o caminho (trilha) pra ver o que o jogo mudou.
                    bool mesmaFonte = fonte.Index == r.fonteCanal.Index && fonte.Version == r.fonteCanal.Version;
                    if ((!temCanal || canal == 0) && r.canal != int.MinValue && r.canal != 0 && (mesmaFonte || !temCanal))
                    {
                        canal = r.canal;
                        if (!r.segurouLog) { r.segurouLog = true; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": channel source " + (temCanal ? "read 0" : "LOST (path:" + trilha + ")") + " — holding channel " + r.canal + "; " + Diag(em, r)); }
                    }
                    else if (temCanal && canal != 0) { r.fonteCanal = fonte; if (r.segurouLog) { r.segurouLog = false; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": channel source is back (" + canal + ")"); } }
                    if (r.canal == int.MinValue) { r.canal = canal; r.canalCand = canal; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": channel " + canal); }
                    else if (canal != r.canal)
                    {
                        if (canal != r.canalCand) { r.canalCand = canal; r.tCand = agora; }
                        else if (agora - r.tCand >= 0.3f) { r.canal = canal; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": channel " + canal); }
                    }
                    else r.canalCand = canal;
                    byte tr = TipoRuntime(em, r.sinal);
                    if (tr != r.tipoLog) { r.tipoLog = tr; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": signal port is " + (tr == Ecs.PortaEntrada ? "SENDING (wired to an output)" : tr == Ecs.PortaSaida ? "RECEIVING (wired to an input)" : "not wired (type " + tr + ")") + " on channel " + r.canal); }
                    float v = 0f; bool temVideo = tr == Ecs.PortaEntrada && LerCru(em, r.sinal, out v);
                    if (tr == Ecs.PortaEntrada && !temVideo && !noTick) { if (!r.semFonteLog) { r.semFonteLog = true; Ecs.Fonte(em, r.sinal, out var tv); Registro.Aviso("SC_LongRangeTransmitter " + r.bloco.Index + ": SENDING but the signal source is gone (path:" + tv + "); " + Diag(em, r)); } }
                    else if (temVideo && r.semFonteLog) { r.semFonteLog = false; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": signal source is back"); }
                    if (temVideo)
                    {
                        if (quemEnvia.TryGetValue(r.canal, out var outro) && outro.Index < r.bloco.Index) { if (avisoDoisEnviam.Add(r.canal)) Registro.Aviso("long range channel " + r.canal + ": more than one transmitter is SENDING — the first one wins (block " + outro.Index + ")"); continue; }
                        quemEnvia[r.canal] = r.bloco; valorCanal[r.canal] = v;
                        if (!noTick) Congelado(em, r, v, agora);   // 0.19.8 (Andre: "sai da atmosfera e perdi o sinal"): video que para de mudar
                    }
                }
                catch (Exception ex) { if (r.avisos++ < 3) Registro.Aviso("SC_LongRangeTransmitter " + r.bloco.Index + ": " + ex.Message); }
            }
            foreach (var r in radios.Values)
            {
                if (!r.portasOk || r.tipoLog != Ecs.PortaSaida) continue;
                Ecs.EscreverPorta(em, r.sinal, valorCanal.TryGetValue(r.canal, out var v) ? v : 0f);
            }
        }
        /// <summary>0.19.8: o identificador do video muda todo tick; se parou de mudar por 2 s, a camera do outro veiculo nao esta sendo
        /// simulada (fora da atmosfera / outro planeta o jogo tira o ActiveWormholeWorld dos blocos de la). So log, 1x por bloco.</summary>
        static TypeIndex tiAtivo; static bool ativoOk;
        /// <summary>0.19.8: estado do bloco pro log (ActiveWormholeWorld, part, portas vivas).</summary>
        static string Diag(EntityManager em, R r)
        {
            string ativo = "?";
            try { if (!ativoOk) { ativoOk = true; tiAtivo = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>()); } ativo = Ecs.TemC(em, r.bloco, tiAtivo) ? "yes" : "NO"; } catch (Exception) { }
            var pai = Ecs.Pai(em, r.bloco);
            return "block ActiveWormholeWorld " + ativo + ", parent " + pai.Index + (Ecs.EhPart(em, pai) ? " (part)" : " (not a part)") + ", signal port " + (Ecs.Vivo(em, r.sinal) ? "alive" : "DEAD") + ", channel port " + (Ecs.Vivo(em, r.canalP) ? "alive" : "DEAD") + "; signal port raw: " + Ecs.SinalDiagnostico(em, r.bloco);
        }
        static void Congelado(EntityManager em, R r, float v, float agora)
        {
            if (Math.Abs(v) < 1e6f) { r.ultimoV = v; r.tUltimoV = agora; r.congeladoLog = false; return; }   // nao e' video
            if (float.IsNaN(r.ultimoV) || v != r.ultimoV) { r.ultimoV = v; r.tUltimoV = agora; if (r.congeladoLog) { r.congeladoLog = false; Registro.Info("SC_LongRangeTransmitter " + r.bloco.Index + ": video source is changing again"); } return; }
            if (r.congeladoLog || agora - r.tUltimoV < 2f) return;
            r.congeladoLog = true;
            string ativo = "?";
            try { if (!ativoOk) { ativoOk = true; tiAtivo = TypeManager.GetTypeIndex(Il2CppType.Of<ActiveWormholeWorld>()); } ativo = Ecs.TemC(em, r.bloco, tiAtivo) ? "yes" : "NO"; } catch (Exception) { }
            Registro.Aviso("SC_LongRangeTransmitter " + r.bloco.Index + ": the video it sends stopped changing 2 s ago (camera not simulated? block ActiveWormholeWorld: " + ativo + ") — the monitor on the other end shows static");
        }
        public static void Passo(EntityManager em)
        {
            if (radios.Count == 0) return;
            var mortos = new List<long>();
            valorCanal.Clear(); quemEnvia.Clear();
            // 1) canais e quem envia
            foreach (var kv in radios)
            {
                var r = kv.Value;
                if (!Ecs.Vivo(em, r.bloco)) { mortos.Add(kv.Key); continue; }
                try
                {
                    if (r.portasOk && (!Ecs.Vivo(em, r.sinal) || !Ecs.Vivo(em, r.canalP))) { r.portasOk = false; r.sinal = Entity.Null; r.canalP = Entity.Null; r.tipoLog = 255; }   // 0.19.3: portas refeitas pelo jogo
                    if (!r.portasOk)
                    {
                        foreach (var p in Ecs.Portas(em, r.bloco))
                        {
                            if (!Ecs.Porta(em, p, out var t, out _, out _)) continue;
                            if (t == 3 && Ecs.Nulo(r.sinal)) r.sinal = p; else if (t == Ecs.PortaEntrada && Ecs.Nulo(r.canalP)) r.canalP = p;
                        }
                        if (Ecs.Nulo(r.sinal) || Ecs.Nulo(r.canalP)) { if (r.avisos++ < 2) Registro.Aviso("SC_LongRangeTransmitter " + r.bloco.Index + ": ports not found (" + Ecs.Portas(em, r.bloco).Length + ")"); continue; }
                        r.portasOk = true;
                    }
                    if (!r.pintado && r.avisos == 0) { r.tPintura += UnityEngine.Time.deltaTime; if (r.tPintura > 1f) r.pintado = Pintura.Pintar(em, r.bloco); }   // 0.19.2: cor padrao (DarkGray) nos renderers
                    Tela(em, r, UnityEngine.Time.deltaTime);
                }
                catch (Exception ex) { if (r.avisos++ < 3) Registro.Aviso("SC_LongRangeTransmitter " + r.bloco.Index + ": " + ex.Message); }
            }
            foreach (var k in mortos) radios.Remove(k);
            // 2) canal + copia do sinal (0.19.6: tambem roda dentro do tick de fisica — ver Copiar)
            Copiar(em, false);
            // 3) LEDs (0.19.2): por canal, quantos ha e quantos enviam
            var porCanal = new Dictionary<int, int>(); var emissores = new Dictionary<int, int>();
            foreach (var r in radios.Values)
            {
                if (!r.portasOk) continue;
                porCanal.TryGetValue(r.canal, out int c); porCanal[r.canal] = c + 1;
                if (r.tipoLog == Ecs.PortaEntrada) { emissores.TryGetValue(r.canal, out int e2); emissores[r.canal] = e2 + 1; }
            }
            foreach (var r in radios.Values)
            {
                try
                {
                    r.tLed += UnityEngine.Time.deltaTime;
                    if (!Ecs.Vivo(em, r.led) && r.tLed > 2f) { r.tLed = 0f; r.led = Led.Achar(em, r.bloco); r.ledEscrito = -1; }
                    if (Ecs.Nulo(r.led)) continue;
                    int estado;
                    if (!r.portasOk || r.tipoLog == 255 || r.tipoLog > 1) estado = Led.Vermelho;
                    else
                    {
                        porCanal.TryGetValue(r.canal, out int n); emissores.TryGetValue(r.canal, out int ne);
                        estado = ne > 1 ? Led.Amarelo : (n > 1 && ne == 1) ? Led.Verde : Led.Vermelho;
                    }
                    r.ledEscrito = Led.Pintar(em, r.led, estado, r.ledEscrito);
                }
                catch (Exception) { }
            }
        }
    }
}
