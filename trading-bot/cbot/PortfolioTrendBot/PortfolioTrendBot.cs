using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // =====================================================================
    // PortfolioTrendBot  -  v1
    //
    // Seguidor de tendencia diversificado sobre una cesta de simbolos, con
    // una sola instancia del bot. Implementa exactamente la configuracion que
    // sobrevivio a las doce pruebas del arnes (ver HALLAZGOS.md):
    //
    //     señal ..... TSMOM, signo del retorno a 252 barras diarias, FIJO
    //     entrada ... al abrir la barra siguiente a la señal
    //     stop ...... 2xATR(14) en la entrada
    //     salida .... trailing de 3xATR. SIN objetivo fijo.
    //     tamaño .... riesgo % del equity / distancia del stop
    //     pesos ..... iguales entre simbolos
    //
    // ---------------------------------------------------------------------
    // POR QUE cTRADER Y NO FUTUROS
    //
    // El lotaje fraccionado. En futuros el tamaño minimo es un contrato
    // entero y eso expulsa a los mercados caros de una cuenta pequeña: el
    // micro maiz exige ~24.000 USD para que su riesgo quepa en el presupuesto.
    // Aqui el volumen se fracciona hasta VolumeInUnitsStep, asi que el tamaño
    // se ajusta al riesgo en vez de al reves. Es la razon de que este bot
    // exista en esta plataforma.
    //
    // ---------------------------------------------------------------------
    // LO QUE HAY QUE VIGILAR, Y POR QUE ESTA INSTRUMENTADO
    //
    // El arnes valido la señal sobre FUTUROS, donde la financiacion va dentro
    // del precio. En CFD el swap se cobra aparte cada noche, y este sistema
    // aguanta posiciones SEMANAS O MESES. Una estimacion previa sugeria que
    // con swaps del 3% anual el coste se comeria el beneficio, pero esa cifra
    // depende del broker, del instrumento y del lado.
    //
    // Por eso el bot NO discute la tasa: la MIDE. CostTracker acumula el swap
    // real, lo compara con un presupuesto y dispara alarma si se pasa. Al
    // final del run publica el veredicto. Los numeros reales deciden.
    //
    // Es la primera cosa que hay que mirar en el primer backtest largo.
    //
    // ---------------------------------------------------------------------
    // DECISIONES QUE NO SE TOCAN SIN VOLVER A MEDIR
    //
    //   - El lookback NO se reoptimiza. Fijo en 252 dio Sharpe 0.756 fuera de
    //     muestra; reoptimizarlo cada año lo bajo a 0.591.
    //   - NO hay objetivo fijo. Amputa la cola derecha, que es de donde vive
    //     la esperanza de un trend follower.
    //   - NO hay capa de GARCH ni de regimen. El sizing por ATR ya es vol
    //     targeting; añadir capas no creo ventaja en ninguna prueba.
    // =====================================================================

    [Robot(AccessRights = AccessRights.FullAccess, TimeZone = TimeZones.UTC, AddIndicators = true)]
    public class PortfolioTrendBot : Robot
    {
        public const string Version = "1.0";

        #region Cesta

        [Parameter("Simbolos (separados por coma)", Group = "Cesta",
            // Nombres verificados contra la lista real de este broker. Son pares FX
        // porque su volumen minimo (~1.000 unidades) es el unico que una cuenta
        // pequeña puede dimensionar: el minimo de XAUUSD es 1 onza y con 1.000 USD
        // eso obliga a arriesgar el 7.3% por operacion.
        //
        // AVISO HONESTO: la prueba 8 del arnes NO encontro ventaja significativa
        // en divisas (p=0.31). Los sectores que si la mostraron -metales,
        // agricolas, indices- tienen minimos demasiado grandes para esta cuenta.
        // Esta cesta es la que se PUEDE operar, no la que se demostro que funciona.
        DefaultValue = "EURUSD,GBPUSD,AUDUSD,NZDUSD,USDCAD,USDCHF,USDJPY,EURJPY,EURGBP,AUDJPY")]
        public string SimbolosCsv { get; set; }

        [Parameter("Etiqueta de posiciones", Group = "Cesta", DefaultValue = "PTB")]
        public string Etiqueta { get; set; }

        #endregion

        #region Señal

        // 252 = 12 meses. Valor del paper original de Moskowitz-Ooi-Pedersen,
        // no elegido mirando estos datos. Meseta ancha: 252/378/504 dan
        // Sharpe 0.73-0.76. NO reoptimizar: hacerlo baja el Sharpe a 0.591.
        [Parameter("Lookback TSMOM (barras diarias)", Group = "Señal",
            DefaultValue = 252, MinValue = 60, MaxValue = 756, Step = 1)]
        public int Lookback { get; set; }

        [Parameter("Direccion permitida", Group = "Señal", DefaultValue = DireccionModo.Ambas)]
        public DireccionModo Direccion { get; set; }

        #endregion

        #region Riesgo

        [Parameter("Riesgo % por operacion", Group = "Riesgo",
            DefaultValue = 0.5, MinValue = 0.05, MaxValue = 5.0, Step = 0.05)]
        public double RiesgoPct { get; set; }

        [Parameter("Periodo ATR", Group = "Riesgo", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int PeriodoAtr { get; set; }

        [Parameter("Stop inicial (xATR)", Group = "Riesgo",
            DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double StopAtr { get; set; }

        [Parameter("Trailing (xATR)", Group = "Riesgo",
            DefaultValue = 3.0, MinValue = 0.5, MaxValue = 15.0, Step = 0.1)]
        public double TrailingAtr { get; set; }

        [Parameter("Si el riesgo queda bajo el minimo del broker", Group = "Riesgo",
            DefaultValue = AccionInfradimensionada.Saltar)]
        public AccionInfradimensionada SiInfradimensionada { get; set; }

        #endregion

        #region Cortacircuitos

        [Parameter("Drawdown maximo % (0=off)", Group = "Cortacircuitos",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 90.0, Step = 1.0)]
        public double DrawdownMaxPct { get; set; }

        [Parameter("Cerrar todo al tocar el drawdown", Group = "Cortacircuitos", DefaultValue = true)]
        public bool CerrarAlTocarDrawdown { get; set; }

        #endregion

        #region Costes

        [Parameter("Spread maximo (fraccion del precio)", Group = "Costes",
            DefaultValue = 0.0010, MinValue = 0.0001, MaxValue = 0.02, Step = 0.0001)]
        public double SpreadMaxFraccion { get; set; }

        [Parameter("Presupuesto de coste (fraccion del bruto)", Group = "Costes",
            DefaultValue = 0.35, MinValue = 0.05, MaxValue = 1.5, Step = 0.05)]
        public double PresupuestoCoste { get; set; }

        #endregion

        #region Telemetria

        [Parameter("Escribir telemetria", Group = "Telemetria", DefaultValue = true)]
        public bool Telemetria { get; set; }

        [Parameter("Carpeta de runs", Group = "Telemetria",
            DefaultValue = @"C:\Users\eical\trading-bot\data\runs")]
        public string CarpetaRuns { get; set; }

        [Parameter("Listar simbolos del broker al arrancar", Group = "Telemetria",
            DefaultValue = true)]
        public bool ListarSimbolos { get; set; }

        [Parameter("Log detallado", Group = "Telemetria", DefaultValue = true)]
        public bool LogDetallado { get; set; }

        #endregion

        // =================================================================
        // ESTADO
        // =================================================================

        private sealed class Mercado
        {
            public string Nombre;
            public Symbol Simbolo;
            public Bars Diarias;

            // Copias cacheadas. Bars.ClosePrices.ToArray() copia la serie
            // entera: llamarlo 4 veces por simbolo y dia son cientos de miles
            // de copias en un backtest largo. Se refrescan solo cuando entra
            // una barra nueva.
            public double[] Close, High, Low, Atr;
            public int BarrasEnCache = -1;

            public void RefrescarSiHaceFalta(int periodoAtr)
            {
                if (Diarias.Count == BarrasEnCache) return;
                Close = Diarias.ClosePrices.ToArray();
                High = Diarias.HighPrices.ToArray();
                Low = Diarias.LowPrices.ToArray();
                Atr = Signals.Atr(High, Low, Close, periodoAtr);
                BarrasEnCache = Diarias.Count;
            }
        }

        private readonly List<Mercado> _mercados = new();
        private readonly RiskSizer _sizer = new();
        private readonly CostTracker _costes = new();
        private TelemetryEmitter _tel;

        private DateTime _ultimoDiaProcesado = DateTime.MinValue;
        private double _balanceInicial;
        private double _picoEquity;
        private double _drawdownMaxPct;
        private bool _paradoPorDrawdown;
        private int _entradas, _salidas, _vetosSpread, _vetosSizing, _rechazos;
        private readonly Dictionary<string, DateTime> _entradaDe = new();
        private readonly Dictionary<string, double> _riesgoInicialDe = new();
        // Un aviso por simbolo, no uno por barra. El primer backtest genero
        // 343 lineas identicas y enterro el resumen.
        private readonly HashSet<string> _yaAvisadoSizing = new();
        private readonly List<string> _nombresMalos = new();   // no existen
        private readonly List<string> _sinDatos = new();       // existen, sin historico
        private readonly Dictionary<string, int> _saltadasPorSizing = new();

        // Solo RealTime es operativa real. VisualBacktesting es un backtest
        // que se dibuja mientras corre: clasificarlo como "live" hacia que el
        // archivo de telemetria de un backtest se llamara ...-live.jsonl.
        private string Modo => RunningMode == RunningMode.RealTime ? "live" : "backtest";

        // =================================================================
        // ARRANQUE
        // =================================================================

        protected override void OnStart()
        {
            _balanceInicial = Account.Balance;
            _picoEquity = Account.Equity;

            _costes.PresupuestoCoste = PresupuestoCoste;
            _costes.SpreadMaximoFraccion = SpreadMaxFraccion;

            ResolverCesta();
            InformarSimbolosDescartados();
            if (_mercados.Count == 0)
            {
                Print("ERROR: ninguno de los simbolos indicados existe en este broker. El bot se detiene.");
                Stop();
                return;
            }

            PrecargarHistorico();
            IniciarTelemetria();
            ImprimirConfiguracion();

            // El manejador se engancha ANTES del diagnostico. Cuando el
            // diagnostico reventaba por un FormatException se llevaba por
            // delante el resto de OnStart, incluida esta linea, y el bot
            // quedaba sin registrar los cierres.
            Positions.Closed += AlCerrarse;

            // Un fallo al INFORMAR no puede impedir OPERAR. Misma leccion que
            // el FIX del bot anterior con PrintPerformanceReport.
            try
            {
                ListarSimbolosDisponibles();
                DiagnosticarCuenta();
            }
            catch (Exception ex)
            {
                Print("AVISO: el diagnostico de arranque fallo ({0}: {1}). " +
                      "El bot sigue operando con normalidad.",
                    ex.GetType().Name, ex.Message);
            }
        }

        private void ResolverCesta()
        {
            var nombres = (SimbolosCsv ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var nombre in nombres)
            {
                // Symbols.Exists evita pedirle al broker un simbolo que no
                // tiene: GetSymbol a secas escupe un Error rojo por cada uno
                // y ensucia el log antes de que el bot pueda explicarse.
                if (!Symbols.Exists(nombre))
                {
                    _nombresMalos.Add(nombre);
                    continue;
                }

                Symbol s;
                try { s = Symbols.GetSymbol(nombre); }
                catch (Exception)
                {
                    _sinDatos.Add(nombre);
                    continue;
                }
                if (s == null)
                {
                    // Existe en el servidor pero el backtester no puede
                    // servirlo: faltan los datos historicos descargados.
                    _sinDatos.Add(nombre);
                    continue;
                }

                var bars = MarketData.GetBars(TimeFrame.Daily, nombre);
                if (bars == null)
                {
                    Print("AVISO: sin barras diarias para '{0}'. Se omite.", nombre);
                    continue;
                }

                _mercados.Add(new Mercado { Nombre = nombre, Simbolo = s, Diarias = bars });
            }
        }

        /// <summary>
        /// TSMOM(252) necesita al menos 252 barras diarias antes de poder
        /// generar la primera señal. Sin precarga, el bot se pasaria el primer
        /// año del backtest sin operar, que es el error que el FIX 16 del bot
        /// anterior ya documentaba.
        /// </summary>
        private void PrecargarHistorico()
        {
            int necesarias = Lookback + PeriodoAtr + 20;
            foreach (var m in _mercados)
            {
                int cargas = 0;
                while (m.Diarias.Count < necesarias && cargas < 40)
                {
                    int añadidas = m.Diarias.LoadMoreHistory();
                    cargas++;
                    if (añadidas <= 0) break;
                }
                if (m.Diarias.Count < necesarias)
                {
                    Print("AVISO: '{0}' solo tiene {1} barras diarias, hacen falta {2}. " +
                          "No operara hasta acumular historico.",
                        m.Nombre, m.Diarias.Count, necesarias);
                }
            }
        }

        private void IniciarTelemetria()
        {
            if (!Telemetria) return;
            _tel = new TelemetryEmitter(CarpetaRuns, "PortfolioTrendBot", Modo,
                                        activo: true,
                                        flushCada: Modo == "live" ? 1 : 200);
            if (!_tel.Activo)
            {
                Print("AVISO: no se pudo abrir el archivo de telemetria. Se sigue sin el.");
                return;
            }

            _tel.Run(Server.Time, "PortfolioTrendBot", Version, Modo, _balanceInicial,
                Huella(), new Dictionary<string, object>
                {
                    ["simbolos"] = _mercados.Select(m => m.Nombre).ToArray(),
                    ["lookback"] = Lookback,
                    ["riesgo_pct"] = RiesgoPct,
                    ["stop_atr"] = StopAtr,
                    ["trailing_atr"] = TrailingAtr,
                    ["direccion"] = Direccion.ToString(),
                });
            Print("Telemetria en: {0}", _tel.Ruta);
        }

        private string Huella()
            => string.Format(CultureInfo.InvariantCulture,
                "tsmom{0} sl{1}xATR trail{2}xATR riesgo{3}% dir={4} n={5}",
                Lookback, StopAtr, TrailingAtr, RiesgoPct, Direccion, _mercados.Count);

        /// <summary>
        /// Separa los dos motivos por los que un simbolo se cae de la cesta,
        /// porque la accion a tomar es distinta en cada caso.
        /// </summary>
        private void InformarSimbolosDescartados()
        {
            if (_nombresMalos.Count > 0)
            {
                Print("DESCARTADOS -- el nombre no existe en este broker: {0}",
                    string.Join(", ", _nombresMalos));
                Print("  Corrige el parametro 'Simbolos'. La lista completa del broker");
                Print("  sale mas abajo si 'Listar simbolos del broker' esta activo.");
            }

            if (_sinDatos.Count > 0)
            {
                Print("DESCARTADOS -- el simbolo EXISTE pero no hay datos historicos: {0}",
                    string.Join(", ", _sinDatos));
                Print("  NO es un error de nombre. cTrader solo puede backtestear");
                Print("  simbolos cuyo historico ya esta descargado. Solucion: abre");
                Print("  una vez el grafico DIARIO de cada uno en cTrader para que");
                Print("  baje los datos, y vuelve a lanzar el backtest.");
            }
        }

        /// <summary>
        /// Vuelca los nombres de simbolo que este broker SI tiene.
        ///
        /// El segundo backtest fallo porque seis de los nueve nombres por
        /// defecto no existian aqui -- ni siquiera EURUSD, que en la mayoria
        /// de brokers existe. Los nombres varian mucho entre brokers y no hay
        /// forma de adivinarlos desde fuera: hay que preguntarle al broker.
        /// </summary>
        private void ListarSimbolosDisponibles()
        {
            if (!ListarSimbolos) return;

            int total = Symbols.Count;
            Print("========== SIMBOLOS DISPONIBLES EN ESTE BROKER ({0}) ==========", total);

            var nombres = new List<string>(total);
            for (int i = 0; i < total; i++)
                nombres.Add(Symbols[i]);
            nombres.Sort(StringComparer.OrdinalIgnoreCase);

            // De 12 en 12 por linea: el log de cTrader corta las lineas largas.
            const int porLinea = 12;
            for (int i = 0; i < nombres.Count; i += porLinea)
                Print("  {0}", string.Join("  ", nombres.GetRange(
                    i, Math.Min(porLinea, nombres.Count - i))));

            Print("Copia de aqui los nombres EXACTOS al parametro 'Simbolos'.");
            Print("Para dejar de ver esta lista: 'Listar simbolos del broker' = false.");
            Print("==============================================================");
        }

        /// <summary>
        /// ¿Puede esta cuenta operar estos simbolos con el riesgo pedido?
        ///
        /// Se responde AL ARRANCAR, no despues de 343 señales saltadas. El
        /// primer backtest acabo con 0 operaciones y balance intacto porque el
        /// minimo de XAUUSD (1 onza) con un stop de 2xATR obligaba a arriesgar
        /// 91-229 USD sobre una cuenta de 1.000: entre 18 y 45 veces el 0.5%
        /// pedido. El bot hizo lo correcto saltandolas, pero eso hay que
        /// saberlo en la primera linea del log, no deducirlo del resumen.
        /// </summary>
        private void DiagnosticarCuenta()
        {
            Print("========== ¿PUEDE ESTA CUENTA OPERAR ESTA CESTA? ==========");
            Print("Equity {0:F2} | riesgo pedido {1}% = {2:F2} por operacion",
                Account.Equity, RiesgoPct, Account.Equity * RiesgoPct / 100.0);
            // OJO: la alineacion en C# es {1,10} / {1,-10}. Un {1,>10} lanza
            // FormatException. El bot anterior ya lo documenta en
            // PrintPerformanceReport y aqui se repitio: tumbo todo el OnStart.
            Print("{0,-12} {1,10} {2,12} {3,12} {4,9}  {5}",
                "simbolo", "vol min", "ATR mediana", "riesgo min", "% equity", "veredicto");

            int viables = 0;
            double equity = Account.Equity;
            double pedidoPct = RiesgoPct;

            foreach (var m in _mercados)
            {
                m.RefrescarSiHaceFalta(PeriodoAtr);
                double atrMed = MedianaAtr(m);
                if (double.IsNaN(atrMed) || atrMed <= 0)
                {
                    Print("{0,-12} {1,10} {2,12} {3,12} {4,9}  sin historico suficiente",
                        m.Nombre, m.Simbolo.VolumeInUnitsMin, "n/d", "n/d", "n/d");
                    continue;
                }

                var s = m.Simbolo;
                double valorPunto = s.PipSize > 0 ? s.PipValue / s.PipSize : 0;
                double distancia = atrMed * StopAtr;
                double riesgoMin = s.VolumeInUnitsMin * distancia * valorPunto;
                double pctEquity = equity > 0 ? riesgoMin / equity * 100.0 : 999;

                string veredicto;
                if (pctEquity <= pedidoPct * 1.05)
                {
                    veredicto = "OK";
                    viables++;
                }
                else
                {
                    double capitalNecesario = riesgoMin / (pedidoPct / 100.0);
                    veredicto = string.Format(CultureInfo.InvariantCulture,
                        "NO -- harian falta {0:N0} de capital", capitalNecesario);
                }

                Print("{0,-12} {1,10} {2,12:F4} {3,12:F2} {4,8:F2}%  {5}",
                    m.Nombre, s.VolumeInUnitsMin, atrMed, riesgoMin, pctEquity, veredicto);
            }

            Print("-----------------------------------------------------------");
            if (viables == 0)
            {
                Print("NINGUN simbolo es operable con {0}% de riesgo y {1:F2} de capital.",
                    RiesgoPct, equity);
                Print("El bot NO abrira ninguna posicion. Tres salidas, de mejor a peor:");
                Print("  1. Usar simbolos con nocional minimo mas pequeño (pares FX,");
                Print("     indices con lote minimo bajo). Es la mejor via para una");
                Print("     cuenta pequeña y no cambia la estrategia.");
                Print("  2. Mas capital, segun la columna 'harian falta'.");
                Print("  3. Subir el riesgo por operacion. Es la peor: el drawdown");
                Print("     escala igual y con 1 solo simbolo no hay diversificacion,");
                Print("     que es lo unico que funciono en las doce pruebas.");
                _tel?.Alarma(Server.Time, "critico", "cuenta_insuficiente",
                    $"ningun simbolo operable con {RiesgoPct}% sobre {equity:F2}");
            }
            else if (viables < _mercados.Count)
            {
                Print("{0} de {1} simbolos operables. Los demas se saltaran en silencio.",
                    viables, _mercados.Count);
            }
            else
            {
                Print("Los {0} simbolos son operables con el riesgo pedido.", viables);
            }

            if (_mercados.Count < 5)
            {
                Print("AVISO: solo {0} simbolo(s) en la cesta. La ventaja medida " +
                      "(Sharpe 0.756) es de una cartera de 21; con uno solo el Sharpe " +
                      "cae a ~0.2 y esto es una apuesta direccional, no la estrategia.",
                    _mercados.Count);
            }
            Print("===========================================================");
        }

        private static double MedianaAtr(Mercado m)
        {
            if (m.Atr == null) return double.NaN;
            var validos = m.Atr.Where(x => !double.IsNaN(x) && x > 0).ToArray();
            if (validos.Length == 0) return double.NaN;
            Array.Sort(validos);
            return validos[validos.Length / 2];
        }

        private void ImprimirConfiguracion()
        {
            Print("PortfolioTrendBot v{0} | modo {1} | {2} simbolos",
                Version, Modo, _mercados.Count);
            Print("CESTA: {0}", string.Join(", ", _mercados.Select(m => m.Nombre)));
            Print("SEÑAL: TSMOM({0}) FIJO -- no reoptimizar, ver HALLAZGOS.md prueba 12", Lookback);
            Print("SALIDA: trailing {0}xATR, SIN objetivo fijo", TrailingAtr);
            Print("RIESGO: {0}% por operacion | stop {1}xATR({2})", RiesgoPct, StopAtr, PeriodoAtr);
            Print("HUELLA: {0}", Huella());
            Print("VIGILAR: el swap. En CFD es lo unico que puede matar esta estrategia; " +
                  "el bot lo mide y publica el veredicto en OnStop.");
        }

        // =================================================================
        // BUCLE PRINCIPAL  -  una pasada al dia
        // =================================================================

        protected override void OnBar()
        {
            var hoy = Server.Time.Date;
            if (hoy == _ultimoDiaProcesado) return;
            _ultimoDiaProcesado = hoy;

            SeguirDrawdown();

            if (PosicionesPropias().Any())
                _costes.RegistrarDiaConPosicion();

            if (_paradoPorDrawdown)
                return;

            foreach (var m in _mercados)
                ProcesarMercado(m);

            EmitirSnapshot();
        }

        private void ProcesarMercado(Mercado m)
        {
            // La ultima barra cerrada es Count-2: Count-1 aun esta abierta.
            int cerrada = m.Diarias.Count - 2;
            if (cerrada < Lookback + PeriodoAtr) return;

            m.RefrescarSiHaceFalta(PeriodoAtr);

            double atr = m.Atr != null && cerrada < m.Atr.Length ? m.Atr[cerrada] : double.NaN;
            if (double.IsNaN(atr) || atr <= 0) return;

            var abierta = PosicionEn(m.Nombre);

            if (abierta != null)
            {
                GestionarTrailing(m, abierta, cerrada, atr);
                return;
            }

            int señal = Signals.Tsmom(m.Close, cerrada, Lookback);
            if (señal == 0) return;
            if (Direccion == DireccionModo.SoloLargos && señal < 0) return;
            if (Direccion == DireccionModo.SoloCortos && señal > 0) return;

            AbrirPosicion(m, señal, atr);
        }

        // =================================================================
        // ENTRADA
        // =================================================================

        private void AbrirPosicion(Mercado m, int señal, double atr)
        {
            var s = m.Simbolo;
            double precio = señal > 0 ? s.Ask : s.Bid;
            if (precio <= 0) return;

            if (!_costes.SpreadAceptable(s.Spread, precio))
            {
                _vetosSpread++;
                if (LogDetallado)
                    Print("{0}: veto por spread ({1:P3} del precio)", m.Nombre, s.Spread / precio);
                return;
            }

            double distancia = atr * StopAtr;

            // Respetar el minimo del broker; si el stop calculado es menor,
            // ampliarlo (y el tamaño baja en consecuencia, no al reves).
            double minimo = DistanciaMinimaEnPrecio(s, precio);
            if (minimo > 0 && distancia < minimo) distancia = minimo;

            // valor de 1 punto de precio por 1 unidad de volumen
            double valorPunto = s.PipSize > 0 ? s.PipValue / s.PipSize : 0;

            var sz = _sizer.Calcular(Account.Equity, RiesgoPct, distancia, valorPunto,
                                     s.VolumeInUnitsMin, s.VolumeInUnitsMax,
                                     v => s.NormalizeVolumeInUnits(v, RoundingMode.Down));

            if (sz.Estado == SizingResultado.DatosInvalidos ||
                sz.Estado == SizingResultado.VolumenCero)
            {
                _vetosSizing++;
                if (LogDetallado) Print("{0}: sin sizing viable ({1})", m.Nombre, sz.Detalle);
                return;
            }

            if (sz.Estado == SizingResultado.RiesgoPorDebajoDelMinimo &&
                SiInfradimensionada == AccionInfradimensionada.Saltar)
            {
                _vetosSizing++;
                _saltadasPorSizing.TryGetValue(m.Nombre, out int n);
                _saltadasPorSizing[m.Nombre] = n + 1;

                if (_yaAvisadoSizing.Add(m.Nombre))
                    Print("{0}: SALTADO -- {1}. No se repetira este aviso; " +
                          "el total sale en el resumen.", m.Nombre, sz.Detalle);
                return;
            }

            var tipo = señal > 0 ? TradeType.Buy : TradeType.Sell;
            var r = ExecuteMarketOrder(tipo, m.Nombre, sz.Volumen, Etiqueta);

            if (!r.IsSuccessful)
            {
                _rechazos++;
                Print("{0}: orden rechazada -> {1}", m.Nombre, r.Error);
                _tel?.Alarma(Server.Time, "aviso", "orden_rechazada",
                    $"{m.Nombre}: {r.Error}");
                return;
            }

            double stop = señal > 0 ? r.Position.EntryPrice - distancia
                                    : r.Position.EntryPrice + distancia;
            r.Position.ModifyStopLossPrice(stop);

            _entradas++;
            _entradaDe[m.Nombre] = Server.Time;
            _riesgoInicialDe[m.Nombre] = sz.RiesgoEfectivo;

            if (LogDetallado)
                Print("{0} {1} vol={2} stop={3} riesgo={4:F2} ({5:P0} del pedido)",
                    m.Nombre, tipo, sz.Volumen, Math.Round(stop, s.Digits),
                    sz.RiesgoEfectivo, sz.Fidelidad);
        }

        /// <summary>
        /// Distancia minima de stop que admite el broker, convertida a PRECIO.
        ///
        /// Ojo: SymbolMinDistanceType tiene dos valores, Pips y Percentage.
        /// Tratar el porcentaje como si fueran pips da un minimo absurdo -en
        /// oro a 4000, un 0.1% son 4 dolares, no 0.1 pips- y es la clase de
        /// error de unidades que el FIX 27 del bot anterior ya documenta.
        /// </summary>
        private static double DistanciaMinimaEnPrecio(Symbol s, double precio)
        {
            if (s.MinStopLossDistance <= 0) return 0;

            return s.MinDistanceType == SymbolMinDistanceType.Percentage
                ? s.MinStopLossDistance / 100.0 * precio
                : s.MinStopLossDistance * s.PipSize;
        }

        // =================================================================
        // GESTION  -  el trailing es lo que gobierna el resultado
        // =================================================================

        private void GestionarTrailing(Mercado m, Position p, int cerrada, double atr)
        {
            bool largo = p.TradeType == TradeType.Buy;
            double stopActual = p.StopLoss ?? (largo ? double.MinValue : double.MaxValue);

            double nuevo = TrailingExit.NuevoStop(
                largo,
                m.High[cerrada],
                m.Low[cerrada],
                atr, TrailingAtr, stopActual);

            if (Math.Abs(nuevo - stopActual) < m.Simbolo.TickSize) return;

            // No mover el stop al lado equivocado del precio actual.
            double actual = largo ? m.Simbolo.Bid : m.Simbolo.Ask;
            if (largo && nuevo >= actual) return;
            if (!largo && nuevo <= actual) return;

            p.ModifyStopLossPrice(Math.Round(nuevo, m.Simbolo.Digits));
        }

        // =================================================================
        // CIERRES Y COSTES
        // =================================================================

        private void AlCerrarse(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != Etiqueta) return;

            _salidas++;

            double riesgo = _riesgoInicialDe.TryGetValue(p.SymbolName, out var ri) && ri > 0 ? ri : 0;
            double r = riesgo > 0 ? p.NetProfit / riesgo : 0;
            int dias = _entradaDe.TryGetValue(p.SymbolName, out var t0)
                ? (int)(Server.Time - t0).TotalDays : 0;

            _costes.RegistrarCierre(p.SymbolName, p.GrossProfit, p.Commissions, p.Swap);

            _tel?.Trade(Server.Time, p.SymbolName, p.TradeType.ToString(),
                p.EntryPrice, p.CurrentPrice, p.NetProfit, r,
                p.Commissions, p.Swap, dias, args.Reason.ToString());

            if (LogDetallado)
                Print("CIERRE {0} {1} neto={2:F2} R={3:F2} swap={4:F2} dias={5} motivo={6}",
                    p.SymbolName, p.TradeType, p.NetProfit, r, p.Swap, dias, args.Reason);

            _entradaDe.Remove(p.SymbolName);
            _riesgoInicialDe.Remove(p.SymbolName);

            RevisarCostes();
        }

        /// <summary>
        /// La alarma que justifica todo el instrumental: si el swap se esta
        /// comiendo el beneficio, hay que saberlo ANTES de que se lo coma
        /// entero, no al final del backtest.
        /// </summary>
        private void RevisarCostes()
        {
            if (!_costes.FueraDePresupuesto) return;

            string msg = _costes.Veredicto();
            Print("ALARMA DE COSTE: {0}", msg);
            foreach (var kv in _costes.PeoresPorSwap(3))
                Print("   swap acumulado {0}: {1:F2}", kv.Key, kv.Value);

            _tel?.Alarma(Server.Time, "critico", "coste_fuera_presupuesto", msg,
                new Dictionary<string, object>
                {
                    ["arrastre"] = _costes.ArrastreDeCoste,
                    ["swaps"] = _costes.Swaps,
                    ["comisiones"] = _costes.Comisiones,
                });
        }

        // =================================================================
        // CORTACIRCUITOS
        // =================================================================

        private void SeguirDrawdown()
        {
            double eq = Account.Equity;
            if (eq > _picoEquity) _picoEquity = eq;

            double dd = _picoEquity > 0 ? (_picoEquity - eq) / _picoEquity * 100.0 : 0;
            if (dd > _drawdownMaxPct) _drawdownMaxPct = dd;

            if (DrawdownMaxPct <= 0 || _paradoPorDrawdown || dd < DrawdownMaxPct) return;

            _paradoPorDrawdown = true;
            string msg = $"drawdown {dd:F1}% supera el tope de {DrawdownMaxPct:F1}%";
            Print("CORTACIRCUITO: {0}", msg);
            _tel?.Alarma(Server.Time, "critico", "drawdown_maximo", msg);

            if (CerrarAlTocarDrawdown)
                foreach (var p in PosicionesPropias().ToList())
                    ClosePosition(p);
        }

        // =================================================================
        // UTILIDADES
        // =================================================================

        private IEnumerable<Position> PosicionesPropias()
            => Positions.Where(p => p.Label == Etiqueta);

        private Position PosicionEn(string simbolo)
            => Positions.FirstOrDefault(p => p.Label == Etiqueta && p.SymbolName == simbolo);

        private void EmitirSnapshot()
        {
            double dd = _picoEquity > 0 ? (_picoEquity - Account.Equity) / _picoEquity * 100.0 : 0;
            _tel?.Snapshot(Server.Time, Account.Equity, Account.Balance,
                PosicionesPropias().Count(), dd, _costes.Swaps, _costes.Comisiones);
        }

        // =================================================================
        // CIERRE
        // =================================================================

        protected override void OnStop()
        {
            double retorno = _balanceInicial > 0
                ? (Account.Balance - _balanceInicial) / _balanceInicial * 100.0 : 0;

            Print("========== RESUMEN ==========");
            Print("Balance {0:F2} -> {1:F2}  ({2:+0.00;-0.00}%)",
                _balanceInicial, Account.Balance, retorno);
            Print("Drawdown maximo: {0:F1}%", _drawdownMaxPct);
            Print("Entradas {0} | salidas {1} | rechazos {2}", _entradas, _salidas, _rechazos);
            Print("Vetos: spread {0} | sizing {1}", _vetosSpread, _vetosSizing);
            foreach (var kv in _saltadasPorSizing.OrderByDescending(k => k.Value))
                Print("   saltadas por sizing en {0}: {1}", kv.Key, kv.Value);
            Print(_sizer.Resumen());
            Print(_costes.Resumen());
            Print("--------------------------------");
            Print("VEREDICTO SOBRE EL COSTE EN CFD:");
            Print("  {0}", _costes.Veredicto());
            foreach (var kv in _costes.PeoresPorSwap(5))
                Print("  swap acumulado {0}: {1:F2}", kv.Key, kv.Value);
            Print("--------------------------------");
            Print("HUELLA: {0}", Huella());

            _tel?.RunEnd(Server.Time, Account.Balance, retorno, _drawdownMaxPct,
                _salidas, _costes.Resumen(), _sizer.Resumen());
            _tel?.Dispose();
        }
    }

    public enum DireccionModo { Ambas, SoloLargos, SoloCortos }

    public enum AccionInfradimensionada { Saltar, UsarVolumenMinimo }
}
