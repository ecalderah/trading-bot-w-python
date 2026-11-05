using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // #####################################################################
    // MAYOR RIESGO  -  perfil agresivo congelado
    //
    // Copia del motor XauusdKalmanModularBot fijada en la configuracion que
    // produjo el mayor beneficio medido. Se separa en su propio proyecto para
    // que el perfil quede intacto mientras el bot principal sigue cambiando.
    //
    // ---------------------------------------------------------------------
    // RESULTADO MEDIDO  (backtest, no operativa real)
    //
    //   Periodo .............. 01/01/2025 - 31/12/2025  (12 meses)
    //   Instrumento .......... XAUUSD H1
    //   Capital inicial ...... 2.000 USD  ->  4.225,96 USD
    //   Retorno .............. +111,30%
    //   Drawdown maximo ...... 36,59%  (2.067,15 USD)
    //   Retorno / Drawdown ... 3,04
    //   Profit factor ........ 1,11        Operaciones ..... 233
    //   Aciertos ............. 42,5%       Esperanza ....... +0,065 R
    //   Largos ............... +4.305,57 (PF 1,38)
    //   Cortos ............... -2.071,73 (PF 0,79)
    //   Costes ............... -821,27 (comisiones + swaps)
    //   Riesgo efectivo ...... 94% del pedido, 0% al volumen minimo
    //
    // ---------------------------------------------------------------------
    // ADVERTENCIAS
    //
    //   - El drawdown del 36,59% es real: hay que tolerar ver la cuenta caer
    //     mas de un tercio desde maximos.
    //   - El profit factor 1,11 es estrecho. La esperanza es +0,065 R, apenas
    //     2,6 puntos de acierto por encima del equilibrio aritmetico.
    //   - Los cortos PIERDEN dinero (PF 0,79). El neto depende de que los
    //     largos compensen. El periodo medido fue alcista en oro.
    //   - Un solo instrumento, un solo periodo, sin walk-forward ni prueba
    //     fuera de muestra.
    //
    // ---------------------------------------------------------------------
    // EQUIVALENCIA CON EL RUN ORIGINAL
    //
    // El codigo base incorpora tres anadidos posteriores a aquella medicion
    // (FIX 25 ventana horaria, FIX 26 R:R explicito y veredicto de ventaja).
    // Los tres son INERTES en sus valores por defecto:
    //     RewardRiskRatio = 0        -> usa AtrTakeProfitMultiple/AtrStopMultiple = 1,5
    //     EnableSessionWindow = false -> la puerta y el cierre por sesion salen en la 1a linea
    //     MaxMinutesInPosition = 0    -> sin caducidad de posicion
    // Con esos defaults el comportamiento es identico al del run medido, y
    // ademas se gana el informe de ventaja al terminar.
    //
    // ---------------------------------------------------------------------
    // CONFIGURACION DEL RUN  (ver tambien "Mayor Riesgo.cbotset")
    //
    //   Grafico .............. XAUUSD, h1, capital 2.000
    //   Riesgo ............... 5% | SL 2xATR(14) | TP 3xATR (R:R 1,5)
    //   maxPos 1 | cooldown 4 barras | tope diario 2% | racha off
    //   P1 valoracion ........ ON (Veto, Z 2,0) contra XAGUSD
    //   P2 volumen ........... OFF
    //   P3 spread / sesion ... OFF
    //   P4 direccion ......... Both
    //   AdaptStopToAccount ... OFF | MaxStopLossPips 0
    // #####################################################################

    // =====================================================================
    // Motor base: XauusdKalmanModularBot  -  v5
    //
    // Estrategia de seguimiento de tendencia sobre XAUUSD H1, construida
    // sobre un principio unico:
    //
    //     UN SOLO ORIGINADOR, VARIAS PUERTAS QUE SOLO VETAN.
    //
    // El modulo de tendencia (Kalman local lineal) es lo unico que ABRE una
    // operacion. Todo lo demas -valoracion relativa, volumen, coste, sesion,
    // direccion- solo puede IMPEDIRLA. Nunca originarla.
    //
    // Esto no es una preferencia estetica: es la leccion del fallo de la v2.
    // Alli el modulo de hedge (reversion a la media) y el de tendencia
    // (momentum) competian por originar, y se exigia que coincidieran. Como
    // son opuestos por construccion, el bot descarto 1166 senales de 1166.
    // Con la regla "un originador, varias puertas" esa contradiccion no puede
    // volver a darse por muchos filtros que se anadan.
    //
    // ---------------------------------------------------------------------
    // CAPAS
    //
    //   Capa 0  Datos y arranque
    //           Precarga de historico (LoadMoreHistory) y cebado de filtros,
    //           para operar desde la primera barra del periodo en vez de
    //           quemar los primeros meses en warm-up.
    //
    //   Capa 1  Senal  (UNICO ORIGINADOR)
    //           Kalman nivel+pendiente -> z de la pendiente -> direccion.
    //
    //   Capa 2  Puertas de veto (independientes y conmutables una a una)
    //           P1  Valoracion relativa   Kalman hedge XAUUSD~XAGUSD
    //           P2  Volumen               Bars.TickVolumes
    //           P3  Coste y sesion        Symbol.Spread + MarketHours
    //           P4  Direccion             Both / LongOnly / ShortOnly
    //
    //   Capa 3  Riesgo y dimensionamiento
    //           Sizing que escala de verdad, validado contra los minimos del
    //           broker (MinStopLossDistance) y contra el coste de la operacion.
    //
    //   Capa 4  Gestion de la posicion abierta
    //           Salida por giro de tendencia + break-even y trailing opcionales.
    //
    //   Capa 5  Cortacircuitos de cartera
    //           Tope de perdida diaria + racha de perdidas consecutivas.
    //
    //   Capa 6  Observabilidad
    //           Un contador por puerta. Es lo que permitio diagnosticar los
    //           tres backtests fallidos anteriores; no es opcional.
    //
    // ---------------------------------------------------------------------
    // HISTORIAL DE CORRECCIONES (evidencia, no teoria)
    //
    //   FIX 1   z-score normalizado con ventana movil, suelo de sigma y clamp.
    //           El original dividia por la sigma del propio Kalman, que
    //           converge a 0: salian z de -821 y +550.
    //   FIX 2   [ERRONEO, sustituido por el FIX 8] acuerdo estricto trend/hedge.
    //   FIX 3   sizing por riesgo que escala y avisa al saturar en el minimo.
    //           El original hizo vol=1 en las 61 operaciones.
    //   FIX 4   maximo de posiciones simultaneas y cooldown entre entradas.
    //   FIX 5   tope de perdida diaria evaluado sobre equity y registrado.
    //   FIX 6   filtro de direccion (los cortos dieron PF 0.78 vs 2.57 largos).
    //   FIX 7   alineacion exacta de barras con el simbolo de referencia.
    //   FIX 8   el acuerdo pasa de VOTO a VETO. Ver el principio de diseno.
    //   FIX 9   guarda contra referencia degenerada (referencia == simbolo).
    //   FIX 10  la salida la manda el modulo que abrio la operacion.
    //   FIX 11  el centinela de cooldown era int.MinValue y la resta
    //           desbordaba: OverflowException en las 1088 senales validas.
    //   FIX 12  validacion de SL/TP contra los minimos del broker. Sin esto
    //           el broker rechaza ordenes en silencio cuando el ATR se comprime.
    //   FIX 13  puerta de volumen (Bars.TickVolumes).
    //   FIX 14  puerta de coste y sesion (Symbol.Spread, MarketHours).
    //           El original entraba a las 22:01 y 02:00, sesion asiatica fina.
    //   FIX 15  cortacircuito por racha de perdidas (el original encadeno 7).
    //   FIX 16  precarga de historico para no perder el inicio del periodo.
    //
    // v5 - tras el primer backtest que SI opero (+25.44%, 419 operaciones,
    // 0 errores, sizing verificado escalando de verdad):
    //   FIX 17  informe completo dentro del propio log. cTrader borro el
    //           report.html del run del +25% dos minutos despues de generarlo,
    //           y con el se perdieron PF, drawdown y el desglose largo/corto.
    //           El log sobrevive, asi que el bot ahora calcula y publica sus
    //           propias estadisticas: no dependemos de un fichero volatil.
    //   FIX 18  huella de configuracion al inicio y al final del log, para que
    //           un log guardado se identifique a si mismo. Cada backtest
    //           sobrescribe log.txt: sin esto, comparar variantes es adivinar.
    //   FIX 19  seguimiento de drawdown y de racha de perdidas reales, no solo
    //           del cortacircuito. El run del +25% encadeno 9 perdidas y eso
    //           solo se supo leyendo el report.html.
    //   FIX 22  el diagnostico de viabilidad usa la MEDIANA del SL del historico
//           precargado y juzga por granularidad, no el ATR de un instante.
//   FIX 23  medicion del riesgo EFECTIVO tras redondear el volumen.
//   FIX 24  la deteccion de riesgo insuficiente comprueba rawUnits, no el
//           volumen normalizado: NormalizeVolumeInUnits sube al minimo por su
//           cuenta, asi que la comprobacion anterior nunca se cumplia.
//   FIX 20  contabilidad de costes (comisiones + swaps). En ese run se
    //           comieron el 13.9% del beneficio bruto, con los swaps
    //           concentrados en los largos.
    // =====================================================================

    // ---------------------------------------------------------------------
    // PRESET SCALPER - SESION AMERICANA  (punto de partida, NO validado)
    //
    // El bot corre 24h: los filtros de Kalman nunca dejan de actualizarse,
    // porque interrumpir la serie destruiria el estado del filtro y al reabrir
    // la sesion arrancaria en frio. Lo que se restringe es la APERTURA.
    //
    //   SignalTimeFrame ......... m5
    //   WarmupBars .............. 300      TrendZWindow ....... 200
    //   TrendSlopeZThreshold .... 1.2      (mas selectivo que en H1)
    //   AtrStopMultiple ......... 1.2      AtrTakeProfitMultiple .. 1.8
    //   CooldownBars ............ 3        MaxOpenPositions ....... 1
    //   EnableSpreadGate ........ true     MaxSpreadPips .......... 5
    //   MinTpToSpreadRatio ...... 8        (el TP debe cubrir 8x el spread)
    //   EnableVolumeGate ........ true     MinVolumeFactor ........ 1.0
    //   EnableSessionWindow ..... true     09:30-16:00 Eastern Standard Time
    //   NoNewTradesLastMinutes .. 30       FlattenBeforeCloseMinutes .. 5
    //   MaxMinutesInPosition .... 120
    //   MaxDailyLossPct ......... 3        MaxConsecutiveLosses ... 4
    //
    // Por que el scalping ayuda en cuentas pequenas: el SL en pips se encoge
    // con el timeframe. En H1 la mediana era 276 pips, que a 20 USD de riesgo
    // pedia 0.72 unidades -por debajo del minimo del broker-. En M5 el ATR es
    // del orden de ATR_H1/sqrt(12), asi que un SL de 1.2xATR ronda los 50 pips
    // y pide ~4 unidades: el dimensionamiento por riesgo vuelve a funcionar.
    // (Estimacion por escalado raiz-del-tiempo, no medida: hay que backtestearlo.)
    //
    // A cambio, el coste pesa mucho mas: un spread de 1 pip contra un objetivo
    // de 75 pips es el 1.3% del recorrido, y en H1 era el 0.2%. Por eso la
    // Puerta 3 pasa a ser obligatoria, no opcional.
    // ---------------------------------------------------------------------

    public enum TradeDirectionMode
    {
        Both,
        LongOnly,
        ShortOnly
    }

    public enum UndersizedTradeAction
    {
        SkipTrade,
        UseMinimumVolume
    }

    /// <summary>
    /// Como participa el modulo de valoracion relativa (hedge).
    ///
    /// Veto        -> (por defecto) solo puede impedir la entrada de la
    ///                tendencia cuando el spread esta estirado en contra.
    ///                No compres oro que ya esta caro frente a la plata.
    /// Confirmation-> exige coincidencia explicita. Solo coherente si el
    ///                hedge es el originador (UseTrendAsPrimarySignal=false).
    /// Disabled    -> la puerta no participa.
    /// </summary>
    public enum HedgeAgreementMode
    {
        Veto,
        Confirmation,
        Disabled
    }

    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC, AddIndicators = true)]
    public class MayorRiesgo : Robot
    {
        // =================================================================
        // CAPA 1 - SENAL
        // =================================================================

        #region Estrategia

        [Parameter("Usar tendencia como senal principal", Group = "Estrategia", DefaultValue = true, IsValueVisibleInTitle = true)]
        public bool UseTrendAsPrimarySignal { get; set; }

        [Parameter("Direccion permitida (Puerta 4)", Group = "Estrategia", DefaultValue = TradeDirectionMode.Both, IsValueVisibleInTitle = true)]
        public TradeDirectionMode DirectionMode { get; set; }

        [Parameter("Etiqueta de posiciones", Group = "Estrategia", DefaultValue = "KalmanModular")]
        public string PositionLabel { get; set; }

        #endregion

        #region Datos

        [Parameter("Simbolo de referencia (hedge ratio)", Group = "Datos", DefaultValue = "XAGUSD", IsValueVisibleInTitle = true)]
        public string ReferenceSymbolName { get; set; }

        [Parameter("Timeframe de la estrategia", Group = "Datos", DefaultValue = "h1", IsValueVisibleInTitle = true)]
        public TimeFrame SignalTimeFrame { get; set; }

        [Parameter("Barras de warm-up para los filtros", Group = "Datos", DefaultValue = 300, MinValue = 50, MaxValue = 2000, Step = 1, IsValueVisibleInTitle = true)]
        public int WarmupBars { get; set; }

        // FIX 16
        [Parameter("Precargar historico para el warm-up", Group = "Datos", DefaultValue = true)]
        public bool PreloadHistory { get; set; }

        [Parameter("Maximo de cargas de historico", Group = "Datos", DefaultValue = 20, MinValue = 1, MaxValue = 100, Step = 1)]
        public int MaxHistoryLoads { get; set; }

        #endregion

        #region Kalman Trend

        [Parameter("Trend: var. de proceso (nivel)", Group = "Kalman Trend", DefaultValue = 1e-5, MinValue = 1e-8, MaxValue = 0.01)]
        public double TrendLevelProcessVar { get; set; }

        [Parameter("Trend: var. de proceso (pendiente)", Group = "Kalman Trend", DefaultValue = 1e-7, MinValue = 1e-10, MaxValue = 0.001)]
        public double TrendSlopeProcessVar { get; set; }

        [Parameter("Trend: var. de observacion", Group = "Kalman Trend", DefaultValue = 1e-4, MinValue = 1e-8, MaxValue = 0.1)]
        public double TrendObservationVar { get; set; }

        [Parameter("Trend: umbral Z de la pendiente", Group = "Kalman Trend", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1)]
        public double TrendSlopeZThreshold { get; set; }

        [Parameter("Trend: ventana de normalizacion Z", Group = "Kalman Trend", DefaultValue = 200, MinValue = 30, MaxValue = 1000, Step = 10)]
        public int TrendZWindow { get; set; }

        #endregion

        // =================================================================
        // CAPA 2 - PUERTAS DE VETO
        // =================================================================

        #region Puerta 1 - Valoracion relativa (Kalman Hedge)

        [Parameter("P1: activar puerta de valoracion", Group = "Puerta 1 - Valoracion", DefaultValue = true, IsValueVisibleInTitle = true)]
        public bool RequireTrendAgreement { get; set; }

        [Parameter("P1: modo", Group = "Puerta 1 - Valoracion", DefaultValue = HedgeAgreementMode.Veto, IsValueVisibleInTitle = true)]
        public HedgeAgreementMode AgreementMode { get; set; }

        [Parameter("P1: var. de proceso (alpha)", Group = "Puerta 1 - Valoracion", DefaultValue = 1e-6, MinValue = 1e-12, MaxValue = 1.0)]
        public double HedgeAlphaProcessVar { get; set; }

        [Parameter("P1: var. de proceso (beta)", Group = "Puerta 1 - Valoracion", DefaultValue = 1e-6, MinValue = 1e-12, MaxValue = 1.0)]
        public double HedgeBetaProcessVar { get; set; }

        [Parameter("P1: var. de observacion", Group = "Puerta 1 - Valoracion", DefaultValue = 1e-3, MinValue = 1e-10, MaxValue = 100.0)]
        public double HedgeObservationVar { get; set; }

        [Parameter("P1: Z de veto", Group = "Puerta 1 - Valoracion", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 4.0, Step = 0.1)]
        public double HedgeEntryZ { get; set; }

        [Parameter("P1: Z de salida (solo si hedge origina)", Group = "Puerta 1 - Valoracion", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 2.0, Step = 0.1)]
        public double HedgeExitZ { get; set; }

        [Parameter("P1: ventana de normalizacion Z", Group = "Puerta 1 - Valoracion", DefaultValue = 200, MinValue = 30, MaxValue = 1000, Step = 10)]
        public int HedgeZWindow { get; set; }

        [Parameter("P1: suelo de sigma (% del precio)", Group = "Puerta 1 - Valoracion", DefaultValue = 0.02, MinValue = 0.0, MaxValue = 1.0, Step = 0.01)]
        public double HedgeSigmaFloorPct { get; set; }

        [Parameter("Z maximo permitido (clamp)", Group = "Puerta 1 - Valoracion", DefaultValue = 10.0, MinValue = 3.0, MaxValue = 50.0, Step = 0.5)]
        public double MaxAbsZ { get; set; }

        #endregion

        #region Puerta 2 - Volumen

        // FIX 13. Bars.TickVolumes es volumen de ticks, no volumen negociado
        // real (en CFD no existe volumen centralizado), pero como proxy de
        // actividad sirve y viene en el historico, asi que se backtestea.
        [Parameter("P2: activar puerta de volumen", Group = "Puerta 2 - Volumen", DefaultValue = false, IsValueVisibleInTitle = true)]
        public bool EnableVolumeGate { get; set; }

        [Parameter("P2: ventana de referencia", Group = "Puerta 2 - Volumen", DefaultValue = 100, MinValue = 20, MaxValue = 500, Step = 10)]
        public int VolumeWindow { get; set; }

        [Parameter("P2: volumen minimo (x mediana)", Group = "Puerta 2 - Volumen", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1)]
        public double MinVolumeFactor { get; set; }

        #endregion

        #region Puerta 3 - Coste y sesion

        // FIX 14. El bot original entraba a las 22:01 y a las 02:00: sesion
        // asiatica y rollover, justo donde el oro tiene el spread mas ancho.
        [Parameter("P3: activar puerta de spread", Group = "Puerta 3 - Coste y sesion", DefaultValue = false, IsValueVisibleInTitle = true)]
        public bool EnableSpreadGate { get; set; }

        [Parameter("P3: spread maximo (pips)", Group = "Puerta 3 - Coste y sesion", DefaultValue = 30.0, MinValue = 1.0, MaxValue = 500.0, Step = 1.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("P3: TP minimo en multiplos del spread", Group = "Puerta 3 - Coste y sesion", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.5)]
        public double MinTpToSpreadRatio { get; set; }

        [Parameter("P3: activar puerta de sesion", Group = "Puerta 3 - Coste y sesion", DefaultValue = false, IsValueVisibleInTitle = true)]
        public bool EnableSessionGate { get; set; }

        [Parameter("P3: minutos minimos antes del cierre", Group = "Puerta 3 - Coste y sesion", DefaultValue = 60, MinValue = 0, MaxValue = 720, Step = 15)]
        public int MinMinutesBeforeClose { get; set; }

        #endregion

        // =================================================================
        // PUERTA 5 - VENTANA HORARIA  (FIX 25)
        //
        // El bot corre 24h -los filtros de Kalman nunca dejan de actualizarse,
        // porque cortar la serie destruiria el estado del filtro- pero solo
        // ABRE posiciones dentro de la ventana configurada.
        //
        // La hora se evalua en la zona horaria elegida, no en UTC, de modo que
        // el horario de verano se ajusta solo: 09:30 de Nueva York son las
        // 14:30 UTC en invierno y las 13:30 en verano. Fijar la ventana en UTC
        // desplazaria la sesion media hora dos veces al ano.
        // =================================================================

        #region Puerta 5 - Ventana horaria

        [Parameter("P5: operar solo en una ventana horaria", Group = "Puerta 5 - Sesion", DefaultValue = false, IsValueVisibleInTitle = true)]
        public bool EnableSessionWindow { get; set; }

        [Parameter("P5: zona horaria", Group = "Puerta 5 - Sesion", DefaultValue = "Eastern Standard Time")]
        public string SessionTimeZoneId { get; set; }

        [Parameter("P5: apertura (HH:mm)", Group = "Puerta 5 - Sesion", DefaultValue = "09:30")]
        public string SessionOpen { get; set; }

        [Parameter("P5: cierre (HH:mm)", Group = "Puerta 5 - Sesion", DefaultValue = "16:00")]
        public string SessionClose { get; set; }

        [Parameter("P5: sin entradas en los ultimos N min", Group = "Puerta 5 - Sesion", DefaultValue = 30, MinValue = 0, MaxValue = 240, Step = 5)]
        public int NoNewTradesLastMinutes { get; set; }

        [Parameter("P5: cerrar todo N min antes del cierre", Group = "Puerta 5 - Sesion", DefaultValue = 5, MinValue = 0, MaxValue = 120, Step = 5)]
        public int FlattenBeforeCloseMinutes { get; set; }

        #endregion

        #region Scalping

        // Un scalp que se queda abierto medio dia deja de ser un scalp: pasa a
        // ser una posicion de tendencia sin haberlo decidido.
        [Parameter("Minutos maximos en posicion (0=off)", Group = "Scalping", DefaultValue = 0, MinValue = 0, MaxValue = 10080, Step = 15)]
        public int MaxMinutesInPosition { get; set; }

        #endregion

        // =================================================================
        // CAPA 3 - RIESGO
        // =================================================================

        #region Riesgo

        // PERFIL AGRESIVO: 5% por operacion es el valor con el que se midio el
        // +111,30%. Es cinco veces el riesgo del perfil conservador y produjo
        // un drawdown del 36,59%.
        [Parameter("Riesgo % por operacion", Group = "Riesgo", DefaultValue = 5.0, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Periodo ATR", Group = "Riesgo", DefaultValue = 14, MinValue = 5, MaxValue = 50, Step = 1)]
        public int AtrPeriod { get; set; }

        [Parameter("SL en multiplos de ATR", Group = "Riesgo", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double AtrStopMultiple { get; set; }

        [Parameter("TP en multiplos de ATR", Group = "Riesgo", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 15.0, Step = 0.1)]
        public double AtrTakeProfitMultiple { get; set; }

        // FIX 26 - el R:R como control directo.
        //
        // Antes el R:R era una propiedad EMERGENTE de dos multiplos de ATR
        // (TP/SL), lo que obliga a mover dos parametros a la vez para ajustar
        // una sola cosa. Con esto se fija el objetivo en multiplos del riesgo,
        // que es como se razona de verdad: "arriesgo 1 para ganar 2".
        //
        // Importa porque el R:R impone un suelo matematico a la tasa de acierto:
        //   acierto minimo = 1 / (1 + R:R)
        // A R:R 1.5 hace falta acertar el 40%. El backtest de M5 acerto el
        // 38.9%: perdia antes de pagar la primera comision.
        [Parameter("R:R objetivo (0 = usar multiplos ATR)", Group = "Riesgo", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 10.0, Step = 0.1)]
        public double RewardRiskRatio { get; set; }

        [Parameter("Maximo de posiciones simultaneas", Group = "Riesgo", DefaultValue = 1, MinValue = 1, MaxValue = 10, Step = 1)]
        public int MaxOpenPositions { get; set; }

        [Parameter("Barras de espera entre entradas", Group = "Riesgo", DefaultValue = 4, MinValue = 0, MaxValue = 100, Step = 1)]
        public int CooldownBars { get; set; }

        [Parameter("Si el riesgo < volumen minimo", Group = "Riesgo", DefaultValue = UndersizedTradeAction.SkipTrade)]
        public UndersizedTradeAction UndersizedAction { get; set; }

        #endregion

        // =================================================================
        // CUENTAS PEQUENAS  (FIX 21)
        //
        // El limite duro no es la estrategia: es el volumen minimo del broker.
        // Con VolumeInUnitsMin=1 y PipValue=0.1 en XAUUSD, la perdida minima
        // posible de una operacion es SL_pips * $0.10. Si el riesgo permitido
        // no llega a esa cifra, la operacion no se puede abrir.
        //
        // Medido sobre las 411 operaciones del backtest de 20 meses:
        //   SL mediana 276 pips, p90 520, p99 1426, maximo 2296.
        //   $10000 @ 0.5% -> 88.1% de las senales son operables
        //   $1000  @ 0.5% ->  0.0%   (ni una sola)
        //
        // Recortar el SL a lo que la cuenta puede pagar hace viable el resto.
        // No es gratis: cambia la estrategia, y hay que revalidarla.
        // =================================================================

        #region Cuentas pequenas

        [Parameter("Ajustar el SL al tamano de la cuenta", Group = "Cuentas pequenas", DefaultValue = false, IsValueVisibleInTitle = true)]
        public bool AdaptStopToAccount { get; set; }

        [Parameter("SL maximo absoluto en pips (0=off)", Group = "Cuentas pequenas", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 5000.0, Step = 10.0)]
        public double MaxStopLossPips { get; set; }

        // FIX 27 - por defecto 0: un suelo ABSOLUTO en pips depende del instrumento
        // (30 pips son 3 USD en XAUUSD y un stop enorme en un par FX). El suelo
        // instrumento-neutral es MinStopToSpreadRatio. En un backtest sobre
        // EURUSD este 30 descarto las 124 senales sin que nada lo avisara.
        [Parameter("SL minimo absoluto en pips (0=off)", Group = "Cuentas pequenas", DefaultValue = 0.0, MinValue = 1.0, MaxValue = 500.0, Step = 5.0)]
        public double MinStopLossPips { get; set; }

        [Parameter("SL minimo en multiplos del spread", Group = "Cuentas pequenas", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 50.0, Step = 0.5)]
        public double MinStopToSpreadRatio { get; set; }

        #endregion

        // =================================================================
        // CAPA 4 - GESTION DE LA POSICION
        // =================================================================

        #region Gestion de posicion

        [Parameter("Break-even al alcanzar R", Group = "Gestion", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 5.0, Step = 0.1)]
        public double BreakEvenAtR { get; set; }

        [Parameter("Trailing stop en multiplos de ATR (0=off)", Group = "Gestion", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 10.0, Step = 0.1)]
        public double TrailingAtrMultiple { get; set; }

        #endregion

        // =================================================================
        // CAPA 5 - CORTACIRCUITOS
        // =================================================================

        #region Cortacircuitos

        [Parameter("Perdida maxima diaria %", Group = "Cortacircuitos", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 20.0, Step = 0.1)]
        public double MaxDailyLossPct { get; set; }

        [Parameter("Cerrar todo al tocar el tope diario", Group = "Cortacircuitos", DefaultValue = true)]
        public bool CloseAllOnDailyStop { get; set; }

        // FIX 15. El backtest original encadeno 7 perdidas consecutivas en cortos.
        [Parameter("Perdidas consecutivas antes de pausar (0=off)", Group = "Cortacircuitos", DefaultValue = 0, MinValue = 0, MaxValue = 20, Step = 1)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Barras de pausa tras la racha", Group = "Cortacircuitos", DefaultValue = 24, MinValue = 1, MaxValue = 500, Step = 1)]
        public int PauseBarsAfterStreak { get; set; }

        #endregion

        #region Diagnostico

        [Parameter("Log de diagnostico", Group = "Diagnostico", DefaultValue = true)]
        public bool VerboseDiagnostics { get; set; }

        [Parameter("Resumen cada N barras", Group = "Diagnostico", DefaultValue = 500, MinValue = 0, MaxValue = 10000, Step = 100)]
        public int DiagnosticEveryNBars { get; set; }

        #endregion

        // =================================================================
        // ESTADO
        // =================================================================

        private Bars _signalBars;
        private Bars _referenceBars;
        private Symbol _referenceSymbol;
        private AverageTrueRange _atr;

        private KalmanTrendFilter _trend;
        private KalmanHedgeFilter _hedge;
        private RollingStats _trendSlopeStats;
        private RollingStats _hedgeResidualStats;
        private RollingStats _volumeStats;

        // FIX 22 - distribucion del SL que pide el ATR a lo largo del tiempo.
        // El diagnostico de la v5 miraba el ATR del instante de arranque y daba
        // un OK que solo valia para ese momento: en el backtest de 2025-2026 el
        // SL tipico era 104 pips en enero de 2025 y 276 de mediana en todo el
        // periodo, porque el oro paso de 2624 a 4600 y el ATR crecio con el.
        private RollingStats _stopPipsStats;

        private bool _hedgeEnabled = true;
        private bool _degenerateHedgeReported;

        private const int NoEntryYet = -1;
        private int _processedBars;
        private int _lastEntryBarIndex = NoEntryYet;
        private int _pauseUntilBarIndex = NoEntryYet;

        private DateTime _currentDay;
        private double _dayStartBalance;
        private bool _dailyStopHit;
        private int _consecutiveLosses;

        // Capa 6 - un contador por puerta.
        private int _signalsGenerated;
        private int _entriesTaken;
        private int _vetoHedge;
        private int _vetoHedgeStrict;
        private int _vetoVolume;
        private int _vetoSpread;
        private int _vetoCost;
        private int _vetoSession;
        private int _vetoDirection;
        private int _vetoDailyStop;
        private int _vetoLossStreak;
        private int _vetoMaxPositions;
        private int _vetoCooldown;
        private int _vetoUndersized;
        private int _vetoOpposite;
        private int _slRaisedToMinimum;
        private int _stopsCapped;
        private int _vetoStopTooTight;

        // FIX 23 - riesgo realmente asumido tras redondear el volumen.
        // El contador de "volumen < minimo" solo ve el caso en que el calculo
        // cae POR DEBAJO del minimo. No ve el caso que arruino el run de $2000:
        // el calculo pide 1.8 unidades, RoundingMode.Down lo deja en 1, y
        // arriesgas el 55% de lo que creias. 400 de 415 operaciones asi.
        // FIX 25 - ventana horaria resuelta una sola vez en OnStart.
        private TimeZoneInfo _sessionTz;
        private TimeSpan _sessionOpen;
        private TimeSpan _sessionClose;
        private bool _sessionWindowValid;
        private int _vetoOutOfSession;
        private int _vetoSessionTail;
        private int _flattenedAtClose;
        private int _closedByMaxTime;

        private int _volumeQuantized;
        private int _tradesAtMinVolume;
        private double _effectiveRiskSum;
        private double _effectiveRiskMin = double.MaxValue;
        private double _effectiveRiskMax;
        private int _effectiveRiskCount;
        private int _dailyStopTriggerCount;
        private int _zClampCount;
        private int _ordersRejected;
        private readonly Dictionary<string, int> _closeReasons = new Dictionary<string, int>();

        // FIX 17/19/20 - estadisticas propias, para no depender del report.html.
        private readonly TradeStats _all = new TradeStats();
        private readonly TradeStats _long = new TradeStats();
        private readonly TradeStats _short = new TradeStats();
        private double _startingBalance;
        private double _peakEquity;
        private double _maxDrawdownAbs;
        private double _maxDrawdownPct;

        // =================================================================
        // CAPA 0 - ARRANQUE
        // =================================================================

        protected override void OnStart()
        {
            _signalBars = MarketData.GetBars(SignalTimeFrame);

            _referenceSymbol = Symbols.GetSymbol(ReferenceSymbolName);
            if (_referenceSymbol == null)
            {
                Print("ERROR: no se pudo cargar el simbolo de referencia '{0}'. El bot se detiene.", ReferenceSymbolName);
                Stop();
                return;
            }

            // FIX 9 - referencia degenerada.
            if (string.Equals(ReferenceSymbolName, SymbolName, StringComparison.OrdinalIgnoreCase))
            {
                _hedgeEnabled = false;
                Print("AVISO: la referencia '{0}' es el propio simbolo operado. La regresion daria residual 0 " +
                      "y la Puerta 1 no podria evaluar nada. Se DESACTIVA. Para el par oro/plata usa XAGUSD.",
                    ReferenceSymbolName);
            }

            _referenceBars = MarketData.GetBars(SignalTimeFrame, ReferenceSymbolName);
            _atr = Indicators.AverageTrueRange(_signalBars, AtrPeriod, MovingAverageType.Exponential);

            _trend = new KalmanTrendFilter(TrendLevelProcessVar, TrendSlopeProcessVar, TrendObservationVar);
            _hedge = new KalmanHedgeFilter(HedgeAlphaProcessVar, HedgeBetaProcessVar, HedgeObservationVar);
            _trendSlopeStats = new RollingStats(TrendZWindow);
            _hedgeResidualStats = new RollingStats(HedgeZWindow);
            _volumeStats = new RollingStats(VolumeWindow);
            _stopPipsStats = new RollingStats(5000);

            _currentDay = Server.Time.Date;
            _dayStartBalance = Account.Balance;
            _startingBalance = Account.Balance;
            _peakEquity = Account.Equity;

            SetupSessionWindow();
            PrintConfiguration();

            if (PreloadHistory)
                PreloadAndPrime();

            // Despues de la precarga, para que el ATR ya tenga valor.
            DiagnoseAccountSize();

            _signalBars.BarOpened += OnSignalBarOpened;
            Positions.Closed += OnPositionClosed;
        }

        private void PrintConfiguration()
        {
            Print("Mayor Riesgo (perfil agresivo) iniciado. Primario={0} | referencia={1} | TF={2}",
                SymbolName, ReferenceSymbolName, SignalTimeFrame);
            Print("ORIGINADOR: {0}", UseTrendAsPrimarySignal ? "TENDENCIA (Kalman)" : "VALORACION (hedge)");
            Print("PUERTAS -> P1 valoracion={0}({1}) | P2 volumen={2} | P3 spread={3} sesion={4} | P4 direccion={5}",
                RequireTrendAgreement && _hedgeEnabled, AgreementMode,
                EnableVolumeGate, EnableSpreadGate, EnableSessionGate, DirectionMode);
            Print("RIESGO -> {0}% | SL={1}xATR({2}) | TP={3}xATR | maxPos={4} | cooldown={5}",
                RiskPerTradePct, AtrStopMultiple, AtrPeriod, AtrTakeProfitMultiple, MaxOpenPositions, CooldownBars);
            Print("CORTACIRCUITOS -> diario={0}% | racha={1} (pausa {2} barras)",
                MaxDailyLossPct, MaxConsecutiveLosses == 0 ? "off" : MaxConsecutiveLosses.ToString(), PauseBarsAfterStreak);
            Print("SIMBOLO -> pipSize={0} | volMin={1} | volMax={2} | minSL={3} ({4}) | spread actual={5:F1}p",
                Symbol.PipSize, Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax,
                Symbol.MinStopLossDistance, Symbol.MinDistanceType, Symbol.Spread / Symbol.PipSize);
            if (EnableSessionWindow && _sessionWindowValid)
            {
                // Se imprime la equivalencia en UTC del dia de arranque para que
                // se pueda contrastar de un vistazo con las horas del log.
                DateTime local = SessionLocalNow();
                var openUtc = TimeZoneInfo.ConvertTimeToUtc(local.Date + _sessionOpen, _sessionTz);
                var closeUtc = TimeZoneInfo.ConvertTimeToUtc(local.Date + _sessionClose, _sessionTz);

                Print("SESION -> {0}-{1} ({2}) = {3:HH:mm}-{4:HH:mm} UTC hoy | sin entradas ult. {5} min | " +
                      "cierre forzado {6} min antes | max en posicion {7}",
                    SessionOpen, SessionClose, SessionTimeZoneId,
                    openUtc, closeUtc, NoNewTradesLastMinutes, FlattenBeforeCloseMinutes,
                    MaxMinutesInPosition == 0 ? "off" : MaxMinutesInPosition + " min");
                Print("       (el horario de verano se aplica solo; los filtros siguen actualizandose 24h)");
            }

            Print("HUELLA: {0}", ConfigFingerprint());
        }

        /// <summary>
        /// FIX 18 - huella compacta de la configuracion.
        ///
        /// Cada backtest sobrescribe log.txt en la misma carpeta. Sin una linea
        /// que identifique la variante, un log guardado no se distingue de
        /// otro y comparar experimentos se vuelve adivinar. Se imprime al
        /// principio y al final para que sobreviva a cualquier recorte.
        /// </summary>
        private string ConfigFingerprint()
        {
            return string.Format(
                "orig={0} zT={1} | P1={2} zH={3} | P2={4} x{5} | P3 spr={6}/{7}p ses={8}/{9}m | P4={10} | " +
                "riesgo={11}% SL={12}xATR RR={13} maxPos={14} cd={15} | diario={16}% racha={17} | " +
                "BE={18}R trail={19}xATR | adaptSL={20} maxSL={21}p | P5={22} {23}-{24} {25} maxMin={26}",
                UseTrendAsPrimarySignal ? "trend" : "hedge", TrendSlopeZThreshold,
                RequireTrendAgreement ? AgreementMode.ToString() : "off", HedgeEntryZ,
                EnableVolumeGate, MinVolumeFactor,
                EnableSpreadGate, MaxSpreadPips, EnableSessionGate, MinMinutesBeforeClose,
                DirectionMode,
                RiskPerTradePct, AtrStopMultiple,
                RewardRiskRatio > 0 ? RewardRiskRatio : (AtrStopMultiple > 0 ? AtrTakeProfitMultiple / AtrStopMultiple : 0),
                MaxOpenPositions, CooldownBars,
                MaxDailyLossPct, MaxConsecutiveLosses == 0 ? "off" : MaxConsecutiveLosses.ToString(),
                BreakEvenAtR, TrailingAtrMultiple, AdaptStopToAccount, MaxStopLossPips,
                EnableSessionWindow && _sessionWindowValid, SessionOpen, SessionClose,
                SessionTimeZoneId, MaxMinutesInPosition);
        }

        /// <summary>FIX 19 - drawdown sobre el pico de equity.</summary>
        private void TrackDrawdown()
        {
            double equity = Account.Equity;

            if (equity > _peakEquity)
                _peakEquity = equity;

            double drop = _peakEquity - equity;
            if (drop > _maxDrawdownAbs)
            {
                _maxDrawdownAbs = drop;
                _maxDrawdownPct = _peakEquity > 0 ? drop / _peakEquity * 100.0 : 0.0;
            }
        }

        /// <summary>
        /// FIX 16 - precarga de historico y cebado de los filtros.
        ///
        /// Sin esto, las 300 barras de warm-up se comen el principio del
        /// periodo: en el backtest original la primera operacion no llego
        /// hasta el 04/04 de un test que empezaba el 01/01. Cargando historico
        /// hacia atras y alimentando los filtros antes de empezar, se opera
        /// desde la primera barra del periodo.
        /// </summary>
        private void PreloadAndPrime()
        {
            int needed = Math.Max(WarmupBars, Math.Max(TrendZWindow, Math.Max(HedgeZWindow, VolumeWindow))) + AtrPeriod + 10;

            int loads = 0;
            while (_signalBars.Count < needed && loads < MaxHistoryLoads)
            {
                int added = _signalBars.LoadMoreHistory();
                loads++;
                if (added <= 0)
                    break;
            }

            loads = 0;
            while (_referenceBars.Count < needed && loads < MaxHistoryLoads)
            {
                int added = _referenceBars.LoadMoreHistory();
                loads++;
                if (added <= 0)
                    break;
            }

            // Cebado: se alimentan los filtros con el historico disponible,
            // sin operar. La ultima barra queda fuera porque aun no ha cerrado.
            // El bucle llega hasta Count-2 (la ultima barra aun no ha cerrado),
            // asi que el arranque descuenta esa barra: sin el -1 se cebaba una
            // barra de menos y el log decia "323 de 324".
            int first = Math.Max(1, _signalBars.Count - 1 - needed);
            for (int i = first; i < _signalBars.Count - 1; i++)
                UpdateFilters(i);

            Print("Precarga: {0} barras propias, {1} de referencia. Filtros cebados con {2} barras " +
                  "(hacian falta {3}). Listo para operar: {4}",
                _signalBars.Count, _referenceBars.Count, _processedBars, needed, FiltersReady());
        }

        protected override void OnStop()
        {
            Print("========== RESUMEN ==========");
            Print("Senales generadas por el originador: {0}", _signalsGenerated);
            Print("Entradas ejecutadas: {0}", _entriesTaken);
            Print("--- vetos por puerta ---");
            Print("P1 valoracion (veto): {0} | P1 (modo estricto): {1}", _vetoHedge, _vetoHedgeStrict);
            Print("P2 volumen insuficiente: {0}", _vetoVolume);
            Print("P3 spread ancho: {0} | TP no cubre coste: {1} | sesion: {2}", _vetoSpread, _vetoCost, _vetoSession);
            Print("P5 fuera de ventana horaria: {0} | cola de sesion: {1}", _vetoOutOfSession, _vetoSessionTail);
            Print("   cerradas por fin de sesion: {0} | por tiempo maximo: {1}", _flattenedAtClose, _closedByMaxTime);
            Print("P4 direccion: {0}", _vetoDirection);
            Print("--- riesgo ---");
            Print("Tope diario: {0} (disparado {1} veces) | racha de perdidas: {2}",
                _vetoDailyStop, _dailyStopTriggerCount, _vetoLossStreak);
            Print("Max posiciones: {0} | cooldown: {1} | direccion opuesta abierta: {2}",
                _vetoMaxPositions, _vetoCooldown, _vetoOpposite);
            Print("Volumen < minimo (riesgo insuficiente): {0}", _vetoUndersized);
            Print("SL elevado al minimo del broker: {0} | ordenes rechazadas: {1}", _slRaisedToMinimum, _ordersRejected);
            Print("SL recortado (cuenta/tope): {0} | descartadas por SL demasiado ajustado: {1}",
                _stopsCapped, _vetoStopTooTight);

            // FIX 23 - la fidelidad del riesgo, que es lo que el run de $2000
            // ocultaba: 96.4% de las operaciones al volumen minimo con el
            // contador de "volumen insuficiente" marcando 0.
            if (_effectiveRiskCount > 0)
            {
                double avg = _effectiveRiskSum / _effectiveRiskCount;
                Print("RIESGO EFECTIVO -> pedido {0}% | real medio {1:F3}% (min {2:F3}% max {3:F3}%) = {4:F0}% del pedido",
                    RiskPerTradePct, avg, _effectiveRiskMin, _effectiveRiskMax,
                    RiskPerTradePct > 0 ? avg / RiskPerTradePct * 100.0 : 0.0);
                Print("Operaciones al volumen MINIMO: {0} de {1} ({2:F1}%) | recortadas >20% por redondeo: {3}",
                    _tradesAtMinVolume, _effectiveRiskCount,
                    100.0 * _tradesAtMinVolume / _effectiveRiskCount, _volumeQuantized);

                if (_tradesAtMinVolume > _effectiveRiskCount * 0.5)
                {
                    Print("ATENCION: mas de la mitad de las operaciones fueron al volumen minimo. El " +
                          "dimensionamiento por riesgo NO esta gobernando la estrategia y los resultados " +
                          "no escalan con el capital. Es el fallo del bot original.");
                }
            }
            Print("--- otros ---");
            Print("Puerta 1 operativa al terminar: {0} | clamps de z: {1}", _hedgeEnabled, _zClampCount);
            foreach (var kv in _closeReasons.OrderByDescending(k => k.Value))
                Print("Cierres por {0}: {1}", kv.Key, kv.Value);

            // El informe va aislado: en la v5 un {1,>7} mal escrito lanzo
            // FormatException en OnStop y se llevo el desglose por direccion
            // y la huella final. Un fallo al imprimir no puede costar el run.
            try
            {
                PrintPerformanceReport();
            }
            catch (Exception ex)
            {
                Print("ERROR al generar el informe: {0}. Datos crudos -> trades={1} neto={2:F2} " +
                      "largos={3}/{4:F2} cortos={5}/{6:F2} DD={7:F2}",
                    ex.Message, _all.Trades, _all.Net,
                    _long.Trades, _long.Net, _short.Trades, _short.Net, _maxDrawdownAbs);
            }

            Print("HUELLA: {0}", ConfigFingerprint());
            Print("Mayor Riesgo detenido.");
        }

        /// <summary>
        /// FIX 17 - informe de rendimiento dentro del log.
        ///
        /// cTrader borro el report.html del run del +25% dos minutos despues
        /// de generarlo. El log sobrevive, asi que todo lo que hace falta para
        /// comparar dos variantes se publica aqui.
        /// </summary>
        private void PrintPerformanceReport()
        {
            double finalBalance = Account.Balance;
            double netReturn = _startingBalance > 0
                ? (finalBalance - _startingBalance) / _startingBalance * 100.0
                : 0.0;

            Print("========== RENDIMIENTO ==========");
            Print("Balance {0:F2} -> {1:F2}  ({2:+0.00;-0.00}%)", _startingBalance, finalBalance, netReturn);
            Print("Drawdown maximo: {0:F2} ({1:F2}%) | pico de equity {2:F2}",
                _maxDrawdownAbs, _maxDrawdownPct, _peakEquity);
            Print("Retorno / Drawdown: {0}",
                _maxDrawdownPct > 0 ? (netReturn / _maxDrawdownPct).ToString("F2") : "n/d");
            Print("Costes: comisiones {0:F2} + swaps {1:F2} = {2:F2} ({3})",
                _all.Commissions, _all.Swaps, _all.Commissions + _all.Swaps,
                _all.GrossProfit > 0
                    ? string.Format("{0:F1}% del bruto ganado", -(_all.Commissions + _all.Swaps) / _all.GrossProfit * 100.0)
                    : "n/d");
            Print("---------- por direccion ----------");
            // Ojo: la alineacion en C# es {1,7} / {1,-7}. Un {1,>7} lanza
            // FormatException y se lleva por delante el resto del informe.
            Print("{0,-8} {1,7} {2,10} {3,7} {4,8} {5,9} {6,7}",
                "", "trades", "neto", "PF", "aciertos", "media", "rachaL");
            PrintStatsRow("TOTAL", _all);
            PrintStatsRow("LARGOS", _long);
            PrintStatsRow("CORTOS", _short);
            Print("Mayor ganancia {0:F2} | mayor perdida {1:F2}", _all.LargestWin, _all.LargestLoss);
            PrintEdgeVerdict();
        }

        /// <summary>
        /// FIX 26 - el veredicto aritmetico sobre la ventaja.
        ///
        /// Con R:R 1.5 hace falta acertar el 40% solo para empatar. El backtest
        /// de M5 acerto el 38.9% y perdio el 63.9%: la causa estaba en esta
        /// linea, no en los filtros. Publicarla evita volver a diagnosticarlo
        /// a mano.
        /// </summary>
        private void PrintEdgeVerdict()
        {
            Print("---------- ventaja ----------");

            double planned = RewardRiskRatio > 0
                ? RewardRiskRatio
                : (AtrStopMultiple > 0 ? AtrTakeProfitMultiple / AtrStopMultiple : 0);

            var rr = _all.RealizedRR;
            if (!rr.HasValue)
            {
                Print("Sin ganadoras y perdedoras suficientes para evaluar la ventaja.");
                return;
            }

            double need = _all.BreakEvenWinRate.Value;
            double got = _all.WinRate;
            double margin = got - need;

            Print("R:R planificado {0:F2} | R:R realizado {1:F2}{2}",
                planned, rr.Value,
                planned > 0 && rr.Value < planned * 0.85
                    ? "  (las salidas anticipadas estan recortando las ganadoras)"
                    : "");
            Print("Acierto necesario {0:F1}% | acierto real {1:F1}% | margen {2:+0.0;-0.0} puntos",
                need, got, margin);
            Print("Esperanza por operacion: {0:+0.000;-0.000} R  ({1:F2} media / {2:F2} media)",
                _all.ExpectancyR.Value, _all.AverageWin, _all.AverageLoss);

            if (margin < 0)
            {
                Print("VEREDICTO: SIN VENTAJA. Se acierta por debajo del minimo aritmetico que exige " +
                      "este R:R. Ninguna puerta lo arregla: los filtros quitan operaciones, no mejoran " +
                      "la senal. Sube el R:R o cambia de timeframe.");
            }
            else if (margin < 3)
            {
                Print("VEREDICTO: MARGEN FRAGIL. Solo {0:F1} puntos por encima del equilibrio; " +
                      "un aumento de deslizamiento o comisiones lo elimina.", margin);
            }
            else
            {
                Print("VEREDICTO: VENTAJA POSITIVA con {0:F1} puntos de margen sobre el equilibrio.", margin);
            }

            foreach (var d in new[] { new { N = "LARGOS", S = _long }, new { N = "CORTOS", S = _short } })
            {
                if (d.S.RealizedRR.HasValue)
                {
                    Print("  {0}: R:R {1:F2} | necesita {2:F1}% | logra {3:F1}% | esperanza {4:+0.000;-0.000} R",
                        d.N, d.S.RealizedRR.Value, d.S.BreakEvenWinRate.Value, d.S.WinRate, d.S.ExpectancyR.Value);
                }
            }
        }

        private void PrintStatsRow(string name, TradeStats s)
        {
            Print("{0,-8} {1,7} {2,10:F2} {3,7} {4,7:F1}% {5,9:F2} {6,7}",
                name, s.Trades, s.Net,
                s.ProfitFactor.HasValue ? s.ProfitFactor.Value.ToString("F2") : "n/d",
                s.WinRate, s.AverageTrade, s.MaxConsecutiveLosses);
        }

        // =================================================================
        // BUCLE PRINCIPAL
        // =================================================================

        private void OnSignalBarOpened(BarOpenedEventArgs args)
        {
            int closedIndex = args.Bars.Count - 2;
            if (closedIndex < 1)
                return;

            RollDayIfNeeded();
            TrackDrawdown();
            UpdateFilters(closedIndex);

            if (DiagnosticEveryNBars > 0 && _processedBars % DiagnosticEveryNBars == 0)
                PrintDiagnostics(args.Bars.ClosePrices[closedIndex]);

            if (!FiltersReady())
                return;

            double price = args.Bars.ClosePrices[closedIndex];
            bool hedgeValid = _hedgeEnabled && _hedgeResidualStats.IsReady;

            double zTrend = NormalizeZ(_trend.Slope, _trendSlopeStats, price * 1e-6);
            double zHedge = hedgeValid
                ? NormalizeZ(_hedgeResidualStats.Last, _hedgeResidualStats, price * HedgeSigmaFloorPct / 100.0)
                : 0.0;

            // Las salidas obligatorias van ANTES de gestionar y de evaluar
            // entradas: una posicion que debe cerrarse por sesion o por tiempo
            // se cierra aunque la senal siga viva.
            EnforceSessionExit();
            EnforceMaxHoldingTime();

            ManagePositions(zTrend, zHedge, hedgeValid);
            EvaluateEntry(closedIndex, zTrend, zHedge, hedgeValid);
        }

        /// <summary>Alimenta todos los filtros con una barra cerrada. No opera.</summary>
        private void UpdateFilters(int index)
        {
            double price = _signalBars.ClosePrices[index];
            DateTime barTime = _signalBars.OpenTimes[index];

            _trend.Update(price);
            _trendSlopeStats.Add(_trend.Slope);
            _volumeStats.Add(_signalBars.TickVolumes[index]);

            // FIX 22 - el SL que el ATR habria pedido en esta barra.
            double atrHere = _atr.Result[index];
            if (!double.IsNaN(atrHere) && atrHere > 0)
                _stopPipsStats.Add(atrHere * AtrStopMultiple / Symbol.PipSize);

            // FIX 7 - solo se alimenta el hedge si existe la barra exacta de
            // la referencia; un hueco en XAGUSD desalinearia la regresion.
            if (_hedgeEnabled)
            {
                int refIndex = _referenceBars.OpenTimes.GetIndexByExactTime(barTime);
                if (refIndex >= 0)
                {
                    double refPrice = _referenceBars.ClosePrices[refIndex];
                    if (refPrice > 0)
                        _hedgeResidualStats.Add(_hedge.Update(price, refPrice));
                }
            }

            _processedBars++;

            // FIX 9 en caliente - regresion degenerada pese a otra referencia.
            if (_hedgeEnabled && _processedBars >= WarmupBars &&
                _hedgeResidualStats.IsReady && _hedgeResidualStats.StandardDeviation <= 0 &&
                !_degenerateHedgeReported)
            {
                _degenerateHedgeReported = true;
                _hedgeEnabled = false;
                Print("AVISO: residuales del hedge con desviacion 0 tras {0} barras. Regresion degenerada " +
                      "contra '{1}'. Se desactiva la Puerta 1.", _processedBars, ReferenceSymbolName);
            }
        }

        private bool FiltersReady()
        {
            if (_processedBars < WarmupBars || !_trendSlopeStats.IsReady)
                return false;
            if (EnableVolumeGate && !_volumeStats.IsReady)
                return false;
            return true;
        }

        /// <summary>
        /// FIX 1 - normalizacion del z-score.
        /// Sigma de una ventana movil de valores reales, con suelo absoluto y
        /// clamp. El original dividia por la sigma del Kalman, que converge a
        /// cero, y producia z de tres cifras que disparaban siempre.
        /// </summary>
        private double NormalizeZ(double value, RollingStats stats, double sigmaFloor)
        {
            double sigma = Math.Max(stats.StandardDeviation, Math.Max(sigmaFloor, double.Epsilon));
            double z = (value - stats.Mean) / sigma;

            if (double.IsNaN(z) || double.IsInfinity(z))
                return 0.0;

            if (Math.Abs(z) > MaxAbsZ)
            {
                _zClampCount++;
                z = Math.Sign(z) * MaxAbsZ;
            }

            return z;
        }

        // =================================================================
        // CAPA 1 + CAPA 2 - originador y puertas
        // =================================================================

        private void EvaluateEntry(int barIndex, double zTrend, double zHedge, bool hedgeValid)
        {
            // --- CAPA 1: el unico originador ---
            TradeType? direction = Originate(zTrend, zHedge, hedgeValid);
            if (direction == null)
                return;

            _signalsGenerated++;

            // --- CAPA 2: puertas de veto, de la mas barata a la mas cara ---
            if (!PassDirectionGate(direction.Value))
                return;
            if (!PassValuationGate(direction.Value, zTrend, zHedge, hedgeValid))
                return;
            if (!PassVolumeGate())
                return;
            if (!PassSessionWindowGate())
                return;
            if (!PassSessionGate())
                return;
            if (!PassSpreadGate())
                return;
            if (!PassRiskGates(barIndex, direction.Value))
                return;

            // --- CAPA 3: dimensionamiento ---
            OpenPosition(barIndex, direction.Value, zTrend, zHedge);
        }

        /// <summary>CAPA 1. Lo unico que puede ABRIR una operacion.</summary>
        private TradeType? Originate(double zTrend, double zHedge, bool hedgeValid)
        {
            if (UseTrendAsPrimarySignal)
            {
                if (zTrend >= TrendSlopeZThreshold)
                    return TradeType.Buy;
                if (zTrend <= -TrendSlopeZThreshold)
                    return TradeType.Sell;
                return null;
            }

            if (!hedgeValid)
                return null;

            // Spread caro => oro sobrevalorado frente a la plata => vender.
            if (zHedge >= HedgeEntryZ)
                return TradeType.Sell;
            if (zHedge <= -HedgeEntryZ)
                return TradeType.Buy;
            return null;
        }

        /// <summary>PUERTA 4 - direccion. Cortos: PF 0.78 vs 2.57 en largos.</summary>
        private bool PassDirectionGate(TradeType direction)
        {
            if ((DirectionMode == TradeDirectionMode.LongOnly && direction == TradeType.Sell) ||
                (DirectionMode == TradeDirectionMode.ShortOnly && direction == TradeType.Buy))
            {
                _vetoDirection++;
                return false;
            }
            return true;
        }

        /// <summary>
        /// PUERTA 1 - valoracion relativa. FIX 8: veta, no vota.
        /// Un voto ausente significa "sin objecion", no "sin permiso".
        /// </summary>
        private bool PassValuationGate(TradeType direction, double zTrend, double zHedge, bool hedgeValid)
        {
            if (!RequireTrendAgreement || AgreementMode == HedgeAgreementMode.Disabled)
                return true;

            TradeType? opinion = UseTrendAsPrimarySignal
                ? HedgeOpinion(zHedge, hedgeValid)
                : TrendOpinion(zTrend);

            if (AgreementMode == HedgeAgreementMode.Confirmation)
            {
                if (opinion == null || opinion.Value != direction)
                {
                    _vetoHedgeStrict++;
                    return false;
                }
                return true;
            }

            // Veto: solo bloquea si contradice explicitamente.
            if (opinion != null && opinion.Value != direction)
            {
                _vetoHedge++;
                return false;
            }
            return true;
        }

        private TradeType? HedgeOpinion(double zHedge, bool hedgeValid)
        {
            if (!hedgeValid)
                return null;
            if (zHedge >= HedgeEntryZ)
                return TradeType.Sell;
            if (zHedge <= -HedgeEntryZ)
                return TradeType.Buy;
            return null;
        }

        private TradeType? TrendOpinion(double zTrend)
        {
            if (zTrend >= TrendSlopeZThreshold)
                return TradeType.Buy;
            if (zTrend <= -TrendSlopeZThreshold)
                return TradeType.Sell;
            return null;
        }

        /// <summary>PUERTA 2 - FIX 13. Una ruptura sin volumen detras suele fallar.</summary>
        private bool PassVolumeGate()
        {
            if (!EnableVolumeGate)
                return true;

            double reference = _volumeStats.Median;
            if (reference <= 0)
                return true;

            if (_volumeStats.Last < reference * MinVolumeFactor)
            {
                _vetoVolume++;
                return false;
            }
            return true;
        }

        /// <summary>PUERTA 3a - FIX 14. Sesion abierta y con margen hasta el cierre.</summary>
        private bool PassSessionGate()
        {
            if (!EnableSessionGate)
                return true;

            var hours = Symbol.MarketHours;
            if (!hours.IsOpened())
            {
                _vetoSession++;
                return false;
            }

            if (MinMinutesBeforeClose > 0 &&
                hours.TimeTillClose().TotalMinutes < MinMinutesBeforeClose)
            {
                _vetoSession++;
                return false;
            }
            return true;
        }

        // =================================================================
        // PUERTA 5 - VENTANA HORARIA (FIX 25)
        // =================================================================

        /// <summary>
        /// Resuelve zona horaria y horas de apertura/cierre. Si algo no parsea,
        /// la puerta se desactiva con un aviso en vez de operar 24h en silencio
        /// creyendo que esta restringida.
        /// </summary>
        private void SetupSessionWindow()
        {
            _sessionWindowValid = false;

            if (!EnableSessionWindow)
                return;

            try
            {
                _sessionTz = TimeZoneInfo.FindSystemTimeZoneById(SessionTimeZoneId);
            }
            catch (Exception)
            {
                Print("AVISO: zona horaria '{0}' no reconocida. La Puerta 5 queda DESACTIVADA. " +
                      "Usa un identificador valido de Windows, por ejemplo 'Eastern Standard Time'.",
                    SessionTimeZoneId);
                EnableSessionWindow = false;
                return;
            }

            if (!TimeSpan.TryParse(SessionOpen, out _sessionOpen) ||
                !TimeSpan.TryParse(SessionClose, out _sessionClose))
            {
                Print("AVISO: horas '{0}' / '{1}' no validas (formato HH:mm). La Puerta 5 queda DESACTIVADA.",
                    SessionOpen, SessionClose);
                EnableSessionWindow = false;
                return;
            }

            if (_sessionOpen == _sessionClose)
            {
                Print("AVISO: apertura y cierre coinciden ({0}). La Puerta 5 queda DESACTIVADA.", SessionOpen);
                EnableSessionWindow = false;
                return;
            }

            _sessionWindowValid = true;
        }

        private DateTime SessionLocalNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(Server.Time, _sessionTz);
        }

        /// <summary>Contempla ventanas que cruzan la medianoche local.</summary>
        private bool InSessionWindow(TimeSpan t)
        {
            return _sessionOpen <= _sessionClose
                ? t >= _sessionOpen && t < _sessionClose
                : t >= _sessionOpen || t < _sessionClose;
        }

        /// <summary>Minutos que faltan para el cierre. Solo valido dentro de la ventana.</summary>
        private double MinutesToSessionClose(TimeSpan t)
        {
            if (_sessionOpen <= _sessionClose)
                return (_sessionClose - t).TotalMinutes;

            return t >= _sessionOpen
                ? (TimeSpan.FromHours(24) - t + _sessionClose).TotalMinutes
                : (_sessionClose - t).TotalMinutes;
        }

        /// <summary>
        /// PUERTA 5 - entrada permitida solo dentro de la ventana y con margen
        /// suficiente hasta el cierre. Los filtros siguen actualizandose fuera;
        /// esto solo bloquea la APERTURA.
        /// </summary>
        private bool PassSessionWindowGate()
        {
            if (!EnableSessionWindow || !_sessionWindowValid)
                return true;

            TimeSpan t = SessionLocalNow().TimeOfDay;

            if (!InSessionWindow(t))
            {
                _vetoOutOfSession++;
                return false;
            }

            if (NoNewTradesLastMinutes > 0 && MinutesToSessionClose(t) <= NoNewTradesLastMinutes)
            {
                _vetoSessionTail++;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Cierra posiciones al acercarse el fin de sesion. Un scalper no lleva
        /// posiciones al overnight: el gap de apertura no tiene nada que ver con
        /// la senal que abrio la operacion.
        /// </summary>
        private void EnforceSessionExit()
        {
            if (!EnableSessionWindow || !_sessionWindowValid)
                return;

            TimeSpan t = SessionLocalNow().TimeOfDay;
            bool inside = InSessionWindow(t);
            bool nearClose = inside && FlattenBeforeCloseMinutes > 0 &&
                             MinutesToSessionClose(t) <= FlattenBeforeCloseMinutes;

            if (inside && !nearClose)
                return;

            foreach (var position in OwnPositions())
            {
                _flattenedAtClose++;
                ClosePositionWithReason(position, inside ? "cierre de sesion inminente" : "fuera de sesion");
            }
        }

        /// <summary>Un scalp con caducidad: si no se ha resuelto, se cierra.</summary>
        private void EnforceMaxHoldingTime()
        {
            if (MaxMinutesInPosition <= 0)
                return;

            foreach (var position in OwnPositions())
            {
                if ((Server.Time - position.EntryTime).TotalMinutes >= MaxMinutesInPosition)
                {
                    _closedByMaxTime++;
                    ClosePositionWithReason(position, "tiempo maximo en posicion");
                }
            }
        }

        /// <summary>PUERTA 3b - FIX 14. No pagar un spread desproporcionado.</summary>
        private bool PassSpreadGate()
        {
            if (!EnableSpreadGate)
                return true;

            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips)
            {
                _vetoSpread++;
                return false;
            }
            return true;
        }

        /// <summary>Puertas de riesgo: cortacircuitos, exposicion y cooldown.</summary>
        private bool PassRiskGates(int barIndex, TradeType direction)
        {
            if (IsDailyStopActive())
            {
                _vetoDailyStop++;
                return false;
            }

            // FIX 15 - cortacircuito por racha.
            if (_pauseUntilBarIndex != NoEntryYet && barIndex < _pauseUntilBarIndex)
            {
                _vetoLossStreak++;
                return false;
            }

            var open = OwnPositions();
            if (open.Count >= MaxOpenPositions)
            {
                _vetoMaxPositions++;
                return false;
            }

            // FIX 11 - sin entradas previas no se resta contra el centinela.
            if (_lastEntryBarIndex != NoEntryYet && barIndex - _lastEntryBarIndex < CooldownBars)
            {
                _vetoCooldown++;
                return false;
            }

            if (open.Any(p => p.TradeType != direction))
            {
                _vetoOpposite++;
                return false;
            }

            return true;
        }

        // =================================================================
        // CAPA 3 - DIMENSIONAMIENTO Y ORDEN
        // =================================================================

        private void OpenPosition(int barIndex, TradeType direction, double zTrend, double zHedge)
        {
            double atr = _atr.Result.LastValue;
            if (double.IsNaN(atr) || atr <= 0)
                return;

            double slPips = atr * AtrStopMultiple / Symbol.PipSize;

            // FIX 21 - recorte del SL. Se preserva la relacion TP/SL: si se
            // recorta el stop pero se deja el objetivo donde estaba, el R:R
            // se dispara y la tasa de acierto se hunde.
            // FIX 26 - si se fija un R:R explicito, manda sobre los multiplos ATR.
            double rewardRatio = RewardRiskRatio > 0
                ? RewardRiskRatio
                : (AtrStopMultiple > 0 ? AtrTakeProfitMultiple / AtrStopMultiple : 1.5);
            double cap = double.MaxValue;

            if (MaxStopLossPips > 0)
                cap = MaxStopLossPips;

            if (AdaptStopToAccount)
                cap = Math.Min(cap, AffordableStopPips());

            if (slPips > cap)
            {
                slPips = cap;
                _stopsCapped++;
            }

            // Un stop recortado por debajo del ruido del spread es una perdida
            // programada: mejor no abrir. Este es el punto donde una cuenta
            // demasiado pequena deja de poder operar la estrategia.
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            double floor = Math.Max(MinStopLossPips, spreadPips * MinStopToSpreadRatio);

            if (slPips < floor)
            {
                _vetoStopTooTight++;
                if (VerboseDiagnostics && _vetoStopTooTight <= 3)
                {
                    Print("Descartada: el SL asequible ({0:F1}p) queda por debajo del suelo ({1:F1}p = " +
                          "max de {2:F0}p y {3:F1}x spread). La cuenta es pequena para este SL.",
                        slPips, floor, MinStopLossPips, MinStopToSpreadRatio);
                }
                return;
            }

            double tpPips = slPips * rewardRatio;

            // FIX 12 - minimos del broker. Sin esto, cuando el ATR se comprime
            // el broker rechaza la orden y pierdes la operacion en silencio.
            double minSl = MinDistanceInPips(Symbol.MinStopLossDistance);
            double minTp = MinDistanceInPips(Symbol.MinTakeProfitDistance);

            if (slPips < minSl)
            {
                slPips = minSl;
                _slRaisedToMinimum++;
            }
            if (tpPips < minTp)
                tpPips = minTp;

            if (slPips <= 0)
                return;

            // PUERTA 3c - el TP debe cubrir el coste con margen.
            if (MinTpToSpreadRatio > 0 && spreadPips > 0 && tpPips < spreadPips * MinTpToSpreadRatio)
            {
                _vetoCost++;
                return;
            }

            double rawUnits;
            bool saturated;
            double volume = CalculateVolume(slPips, out rawUnits, out saturated);
            if (volume <= 0)
                return;

            var result = ExecuteMarketOrder(direction, SymbolName, volume, PositionLabel, slPips, tpPips);

            if (!result.IsSuccessful)
            {
                _ordersRejected++;
                Print("Orden rechazada: {0} | vol={1} SL={2:F1}p TP={3:F1}p", result.Error, volume, slPips, tpPips);
                return;
            }

            _entriesTaken++;
            _lastEntryBarIndex = barIndex;

            Print("Entrada {0} | vol={1} (riesgo pedia {2:F0} u.{3}) | SL={4:F1}p TP={5:F1}p | " +
                  "z-trend={6:F2} z-hedge={7:F2} | vol.tick={8:F0} (mediana {9:F0}) | spread={10:F1}p",
                direction, volume, rawUnits, saturated ? " SATURADO" : "",
                slPips, tpPips, zTrend, zHedge,
                _volumeStats.Last, _volumeStats.Median, Symbol.Spread / Symbol.PipSize);
        }

        /// <summary>
        /// FIX 21 - el SL mas amplio que la cuenta puede pagar respetando el
        /// riesgo configurado y el volumen minimo del broker.
        ///
        ///   riesgo_permitido = balance * riesgo%
        ///   perdida_minima   = volumen_minimo * pipValue * SL_pips
        ///   => SL_max = riesgo_permitido / (volumen_minimo * pipValue)
        /// </summary>
        private double AffordableStopPips()
        {
            double riskPerPipAtMinVolume = Symbol.VolumeInUnitsMin * Symbol.PipValue;
            if (riskPerPipAtMinVolume <= 0)
                return double.MaxValue;

            return Account.Balance * RiskPerTradePct / 100.0 / riskPerPipAtMinVolume;
        }

        /// <summary>
        /// FIX 22 - veredicto sobre si la cuenta da para la estrategia.
        ///
        /// Dos correcciones sobre la version anterior, ambas destapadas por el
        /// backtest de $2000:
        ///
        /// 1. Usa la MEDIANA del SL sobre el historico precargado, no el ATR
        ///    del instante de arranque. Aquel diagnostico dijo "OK" mirando
        ///    104 pips de enero de 2025, cuando la mediana del periodo fue 276:
        ///    el oro paso de 2624 a 4600 y el ATR crecio con el.
        ///
        /// 2. Juzga por GRANULARIDAD, no solo por viabilidad. Que la cuenta
        ///    pueda pagar el SL no basta: si el volumen calculado sale entre
        ///    1 y 2 unidades, el redondeo a la baja lo aplasta a 1 y el riesgo
        ///    real deja de ser el configurado. En aquel run el 96.4% de las
        ///    operaciones acabaron a volumen minimo sin que nada lo avisara.
        /// </summary>
        private void DiagnoseAccountSize()
        {
            double affordable = AffordableStopPips();
            double riskAmount = Account.Balance * RiskPerTradePct / 100.0;

            Print("---------- VIABILIDAD DE LA CUENTA ----------");
            Print("Balance {0:F2} | riesgo {1}% = {2:F2} por operacion | volumen min {3} | pipValue {4}",
                Account.Balance, RiskPerTradePct, riskAmount, Symbol.VolumeInUnitsMin, Symbol.PipValue);
            Print("SL maximo asequible al volumen minimo: {0:F1} pips", affordable);

            if (_stopPipsStats.Count < 20)
            {
                Print("Sin historico suficiente para estimar el SL tipico ({0} barras).", _stopPipsStats.Count);
            }
            else
            {
                double medianSl = _stopPipsStats.Median;
                double p90Sl = _stopPipsStats.Percentile(0.90);

                Print("SL segun {0} barras de historico: mediana {1:F1}p | p90 {2:F1}p | max {3:F1}p",
                    _stopPipsStats.Count, medianSl, p90Sl, _stopPipsStats.Percentile(1.0));

                // Granularidad: cuantas unidades pide el riesgo en el caso tipico.
                double unitsAtMedian = medianSl > 0
                    ? riskAmount / (medianSl * Symbol.PipValue)
                    : 0;

                Print("Unidades que pide el riesgo con el SL mediano: {0:F2} (minimo del broker: {1})",
                    unitsAtMedian, Symbol.VolumeInUnitsMin);

                double capitalFor10Units = medianSl * Symbol.PipValue * 10.0 / (RiskPerTradePct / 100.0);

                if (unitsAtMedian < Symbol.VolumeInUnitsMin)
                {
                    Print("VEREDICTO: NO VIABLE. La operacion tipica ni siquiera alcanza el volumen minimo, " +
                          "asi que se descartara. Activa AdaptStopToAccount, sube el riesgo, baja " +
                          "AtrStopMultiple, o usa una cuenta de ~{0:F0} para tener margen real.",
                        capitalFor10Units);
                }
                else if (unitsAtMedian < 2.0)
                {
                    Print("VEREDICTO: CUANTIZACION SEVERA. El volumen tipico caera entre {0} y {1} unidades y " +
                          "el redondeo a la baja lo dejara casi siempre en el minimo: el riesgo real NO sera " +
                          "el {2}% configurado, sino lo que salga. Es el fallo del bot original reaparecido " +
                          "por capital insuficiente. Cuenta recomendada: ~{3:F0}",
                        Math.Floor(unitsAtMedian), Math.Ceiling(unitsAtMedian), RiskPerTradePct, capitalFor10Units);
                }
                else if (unitsAtMedian < 10.0)
                {
                    Print("VEREDICTO: CUANTIZACION NOTABLE. Con {0:F1} unidades tipicas, cada redondeo mueve el " +
                          "riesgo real hasta un {1:F0}%. Utilizable, pero impreciso. Cuenta comoda: ~{2:F0}",
                        unitsAtMedian, 100.0 / unitsAtMedian, capitalFor10Units);
                }
                else
                {
                    Print("VEREDICTO: OK. {0:F0} unidades tipicas dan granularidad suficiente para que el " +
                          "riesgo configurado se respete.", unitsAtMedian);
                }

                // FIX 27 - el suelo del SL contra el SL tipico del instrumento.
                double floorPips = Math.Max(MinStopLossPips,
                    Symbol.Spread / Symbol.PipSize * MinStopToSpreadRatio);

                if (floorPips >= medianSl)
                {
                    Print("AVISO: el suelo del SL ({0:F1}p) iguala o supera el SL tipico ({1:F1}p). " +
                          "Casi todas las senales se descartaran por 'SL demasiado ajustado'.",
                        floorPips, medianSl);
                    Print("       MinStopLossPips es ABSOLUTO en pips y depende del instrumento: con " +
                          "pipSize={0} aqui, 30 pips no significan lo mismo que en oro. Ponlo a 0 y " +
                          "deja que mande MinStopToSpreadRatio ({1}x spread).",
                        Symbol.PipSize, MinStopToSpreadRatio);
                }

                if (p90Sl > affordable && !AdaptStopToAccount && MaxStopLossPips <= 0)
                {
                    Print("AVISO: el 10% de las senales pide un SL mayor que el asequible ({0:F0}p > {1:F0}p). " +
                          "Sin AdaptStopToAccount ni MaxStopLossPips, esas operaciones se descartaran o " +
                          "saturaran en el minimo.", p90Sl, affordable);
                }
            }

            Print("AdaptStopToAccount={0} | SL max absoluto={1} | suelo={2}p o {3}x spread",
                AdaptStopToAccount, MaxStopLossPips == 0 ? "off" : MaxStopLossPips.ToString(),
                MinStopLossPips, MinStopToSpreadRatio);
            Print("---------------------------------------------");
        }

        /// <summary>FIX 12 - convierte la distancia minima del broker a pips.</summary>
        private double MinDistanceInPips(double rawDistance)
        {
            if (rawDistance <= 0)
                return 0;

            if (Symbol.MinDistanceType == SymbolMinDistanceType.Pips)
                return rawDistance;

            // Percentage: porcentaje sobre el precio actual.
            return Symbol.Bid * rawDistance / 100.0 / Symbol.PipSize;
        }

        /// <summary>
        /// FIX 3 - dimensionamiento que escala de verdad y se audita.
        /// En el backtest original el volumen fue 1 en las 61 operaciones: el
        /// calculo caia bajo el minimo del simbolo y saturaba ahi, con lo que
        /// RiskPerTradePct era decorativo.
        /// </summary>
        private double CalculateVolume(double slPips, out double rawUnits, out bool saturated)
        {
            saturated = false;
            rawUnits = 0;

            double riskPerUnit = slPips * Symbol.PipValue;
            if (riskPerUnit <= 0)
                return 0;

            rawUnits = Account.Balance * RiskPerTradePct / 100.0 / riskPerUnit;

            // FIX 24 - la comprobacion se hace sobre rawUnits, NO sobre el
            // volumen ya normalizado.
            //
            // NormalizeVolumeInUnits(0.4, RoundingMode.Down) no devuelve 0:
            // devuelve el volumen MINIMO del simbolo. Es decir, la API ya sube
            // al minimo por su cuenta, asi que la condicion
            // "normalized < VolumeInUnitsMin" no se cumple JAMAS y la deteccion
            // de riesgo insuficiente nunca llego a funcionar.
            //
            // Consecuencia medida en el run de $2000: 29 operaciones que pedian
            // menos de media unidad se abrieron igualmente a volumen 1, sin
            // marcador SATURADO y con el contador a 0. Con SL de 520-620 pips
            // eso son 52-62 USD de riesgo real sobre una cuenta de ~2500: un
            // 2.5% cuando estaba configurado el 1%.
            double normalized = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);

            if (rawUnits < Symbol.VolumeInUnitsMin)
            {
                saturated = true;

                if (UndersizedAction == UndersizedTradeAction.SkipTrade)
                {
                    _vetoUndersized++;
                    if (VerboseDiagnostics && _vetoUndersized <= 5)
                    {
                        double implied = Symbol.VolumeInUnitsMin * riskPerUnit / Account.Balance * 100.0;
                        Print("Descartada: el {0}% de riesgo pide {1:F0} unidades, el minimo es {2:F0} " +
                              "(seria {3:F2}% real). Sube capital, baja el SL o usa UseMinimumVolume.",
                            RiskPerTradePct, rawUnits, Symbol.VolumeInUnitsMin, implied);
                    }
                    return 0;
                }

                normalized = Symbol.VolumeInUnitsMin;
            }

            normalized = Math.Min(normalized, Symbol.VolumeInUnitsMax);

            // FIX 23 - contabilizar el riesgo que de verdad se asume.
            double effectiveRiskPct = normalized * riskPerUnit / Account.Balance * 100.0;
            _effectiveRiskSum += effectiveRiskPct;
            _effectiveRiskCount++;
            if (effectiveRiskPct < _effectiveRiskMin)
                _effectiveRiskMin = effectiveRiskPct;
            if (effectiveRiskPct > _effectiveRiskMax)
                _effectiveRiskMax = effectiveRiskPct;

            if (normalized <= Symbol.VolumeInUnitsMin)
                _tradesAtMinVolume++;

            // Perder mas del 20% de lo pedido por el redondeo ya distorsiona
            // la gestion de riesgo de forma material.
            if (rawUnits > 0 && normalized < rawUnits * 0.8)
            {
                _volumeQuantized++;
                if (VerboseDiagnostics && _volumeQuantized == 1)
                {
                    Print("AVISO: el redondeo del volumen esta recortando el riesgo. Pedia {0:F2} unidades, " +
                          "se opera {1} => riesgo real {2:F2}% en vez de {3}%. Si esto se repite, " +
                          "RiskPerTradePct deja de significar nada (ver resumen final).",
                        rawUnits, normalized, effectiveRiskPct, RiskPerTradePct);
                }
            }

            return normalized;
        }

        // =================================================================
        // CAPA 4 - GESTION DE LA POSICION
        // =================================================================

        /// <summary>FIX 10 - la salida la manda el modulo que abrio la operacion.</summary>
        private void ManagePositions(double zTrend, double zHedge, bool hedgeValid)
        {
            double atr = _atr.Result.LastValue;

            foreach (var position in OwnPositions())
            {
                if (UseTrendAsPrimarySignal)
                {
                    bool flipped =
                        (position.TradeType == TradeType.Buy && zTrend <= -TrendSlopeZThreshold) ||
                        (position.TradeType == TradeType.Sell && zTrend >= TrendSlopeZThreshold);

                    if (flipped)
                    {
                        ClosePositionWithReason(position, "la tendencia giro en contra");
                        continue;
                    }
                }
                else if (hedgeValid && Math.Abs(zHedge) <= HedgeExitZ)
                {
                    ClosePositionWithReason(position, "z-hedge en la banda de salida");
                    continue;
                }

                ApplyBreakEven(position);
                ApplyTrailing(position, atr);
            }
        }

        private void ApplyBreakEven(Position position)
        {
            if (BreakEvenAtR <= 0 || position.StopLoss == null)
                return;

            double riskPips = Math.Abs(position.EntryPrice - position.StopLoss.Value) / Symbol.PipSize;
            if (riskPips <= 0 || position.Pips < riskPips * BreakEvenAtR)
                return;

            // Ya movido a break-even o mejor.
            bool alreadySafe = position.TradeType == TradeType.Buy
                ? position.StopLoss.Value >= position.EntryPrice
                : position.StopLoss.Value <= position.EntryPrice;

            if (!alreadySafe)
                ModifyPosition(position, position.EntryPrice, position.TakeProfit, ProtectionType.Absolute);
        }

        private void ApplyTrailing(Position position, double atr)
        {
            if (TrailingAtrMultiple <= 0 || double.IsNaN(atr) || atr <= 0)
                return;

            double distance = atr * TrailingAtrMultiple;
            double candidate = position.TradeType == TradeType.Buy
                ? Symbol.Bid - distance
                : Symbol.Ask + distance;

            bool better = position.StopLoss == null ||
                (position.TradeType == TradeType.Buy && candidate > position.StopLoss.Value) ||
                (position.TradeType == TradeType.Sell && candidate < position.StopLoss.Value);

            if (better)
                ModifyPosition(position, candidate, position.TakeProfit, ProtectionType.Absolute);
        }

        private void ClosePositionWithReason(Position position, string reason)
        {
            double pnl = position.NetProfit;
            int id = position.Id;

            var result = ClosePosition(position);
            if (result.IsSuccessful && VerboseDiagnostics)
                Print("Cierre PID{0} ({1}) | P/L={2:F2}", id, reason, pnl);
        }

        private List<Position> OwnPositions()
        {
            return Positions
                .Where(p => p.SymbolName == SymbolName && p.Label == PositionLabel)
                .ToList();
        }

        // =================================================================
        // CAPA 5 - CORTACIRCUITOS
        // =================================================================

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.SymbolName != SymbolName || position.Label != PositionLabel)
                return;

            string reason = args.Reason.ToString();
            _closeReasons[reason] = _closeReasons.TryGetValue(reason, out int n) ? n + 1 : 1;

            // FIX 17/20 - contabilidad propia, global y por direccion.
            _all.Add(position);
            (position.TradeType == TradeType.Buy ? _long : _short).Add(position);
            TrackDrawdown();

            // FIX 15 - racha de perdidas. El original encadeno 7 en cortos.
            if (position.NetProfit < 0)
            {
                _consecutiveLosses++;

                if (MaxConsecutiveLosses > 0 && _consecutiveLosses >= MaxConsecutiveLosses)
                {
                    _pauseUntilBarIndex = _signalBars.Count + PauseBarsAfterStreak;
                    _consecutiveLosses = 0;
                    Print("CORTACIRCUITO: {0} perdidas consecutivas. Pausa de {1} barras.",
                        MaxConsecutiveLosses, PauseBarsAfterStreak);
                }
            }
            else
            {
                _consecutiveLosses = 0;
            }
        }

        private void RollDayIfNeeded()
        {
            if (Server.Time.Date == _currentDay)
                return;

            _currentDay = Server.Time.Date;
            _dayStartBalance = Account.Balance;
            _dailyStopHit = false;
        }

        /// <summary>FIX 5 - tope diario sobre equity, con rastro en el log.</summary>
        private bool IsDailyStopActive()
        {
            if (_dailyStopHit)
                return true;
            if (_dayStartBalance <= 0)
                return false;

            double lossPct = -(Account.Equity - _dayStartBalance) / _dayStartBalance * 100.0;
            if (lossPct < MaxDailyLossPct)
                return false;

            _dailyStopHit = true;
            _dailyStopTriggerCount++;
            Print("TOPE DIARIO: perdida {0:F2}% >= {1}%. Sin nuevas entradas hasta manana.", lossPct, MaxDailyLossPct);

            if (CloseAllOnDailyStop)
            {
                foreach (var position in OwnPositions())
                    ClosePositionWithReason(position, "tope de perdida diaria");
            }

            return true;
        }

        // =================================================================
        // CAPA 6 - OBSERVABILIDAD
        // =================================================================

        private void PrintDiagnostics(double price)
        {
            if (!VerboseDiagnostics)
                return;

            Print("[diag {0}] precio={1:F2} | trend pend={2:F5} sigma={3:F5} | hedge a={4:F1} b={5:F2} " +
                  "res={6:F2} sigma={7:F2} | vol.tick={8:F0} mediana={9:F0} | spread={10:F1}p | clamps={11}",
                _processedBars, price,
                _trend.Slope, _trendSlopeStats.StandardDeviation,
                _hedge.Alpha, _hedge.Beta, _hedgeResidualStats.Last, _hedgeResidualStats.StandardDeviation,
                _volumeStats.Last, _volumeStats.Median,
                Symbol.Spread / Symbol.PipSize, _zClampCount);
        }
    }

    // =====================================================================
    // FIX 17 - estadisticas de operaciones cerradas.
    // Replica lo esencial del report.html de cTrader dentro del bot, porque
    // ese fichero se borra y el log no.
    // =====================================================================
    internal sealed class TradeStats
    {
        private int _currentLossStreak;

        public int Trades { get; private set; }
        public int Wins { get; private set; }
        public int Losses { get; private set; }
        public double Net { get; private set; }
        public double GrossProfit { get; private set; }
        public double GrossLoss { get; private set; }
        public double Commissions { get; private set; }
        public double Swaps { get; private set; }
        public double LargestWin { get; private set; }
        public double LargestLoss { get; private set; }
        public int MaxConsecutiveLosses { get; private set; }

        public void Add(Position position)
        {
            double net = position.NetProfit;

            Trades++;
            Net += net;
            Commissions += position.Commissions;
            Swaps += position.Swap;

            if (net >= 0)
            {
                Wins++;
                GrossProfit += net;
                _currentLossStreak = 0;
                if (net > LargestWin)
                    LargestWin = net;
            }
            else
            {
                Losses++;
                GrossLoss += -net;
                _currentLossStreak++;
                if (_currentLossStreak > MaxConsecutiveLosses)
                    MaxConsecutiveLosses = _currentLossStreak;
                if (net < LargestLoss)
                    LargestLoss = net;
            }
        }

        /// <summary>Nulo cuando no hay perdidas: un PF infinito no informa.</summary>
        public double? ProfitFactor
        {
            get { return GrossLoss > 0 ? GrossProfit / GrossLoss : (double?)null; }
        }

        public double WinRate
        {
            get { return Trades > 0 ? 100.0 * Wins / Trades : 0.0; }
        }

        public double AverageTrade
        {
            get { return Trades > 0 ? Net / Trades : 0.0; }
        }

        public double AverageWin
        {
            get { return Wins > 0 ? GrossProfit / Wins : 0.0; }
        }

        public double AverageLoss
        {
            get { return Losses > 0 ? GrossLoss / Losses : 0.0; }
        }

        /// <summary>
        /// FIX 26 - R:R REALIZADO: ganancia media dividida por perdida media.
        ///
        /// No es el R:R configurado. Las salidas por giro de tendencia, por
        /// tiempo o por fin de sesion cortan ganadoras antes del objetivo, asi
        /// que el realizado suele quedar por debajo del planificado. La brecha
        /// entre ambos mide cuanto estan recortando esas salidas.
        /// </summary>
        public double? RealizedRR
        {
            get { return Losses > 0 && Wins > 0 ? AverageWin / AverageLoss : (double?)null; }
        }

        /// <summary>
        /// Tasa de acierto por debajo de la cual el sistema pierde, dado el R:R
        /// realizado:  1 / (1 + R:R).  Es un limite aritmetico, no una opinion.
        /// </summary>
        public double? BreakEvenWinRate
        {
            get
            {
                var rr = RealizedRR;
                return rr.HasValue ? 100.0 / (1.0 + rr.Value) : (double?)null;
            }
        }

        /// <summary>
        /// Esperanza por operacion en unidades de R (riesgo medio).
        /// Positiva = ventaja real. Comparable entre timeframes y capitales.
        /// </summary>
        public double? ExpectancyR
        {
            get
            {
                var rr = RealizedRR;
                if (!rr.HasValue)
                    return null;

                double p = (double)Wins / Trades;
                return p * rr.Value - (1.0 - p);
            }
        }
    }

    // =====================================================================
    // Kalman de tendencia local lineal (nivel + pendiente)
    //   x = [nivel, pendiente], F = [[1,1],[0,1]], H = [1,0]
    // =====================================================================
    internal sealed class KalmanTrendFilter
    {
        private readonly double _qLevel;
        private readonly double _qSlope;
        private readonly double _r;

        private double _p00 = 1.0, _p01, _p10, _p11 = 1.0;
        private bool _initialized;

        public double Level { get; private set; }
        public double Slope { get; private set; }

        public KalmanTrendFilter(double levelProcessVar, double slopeProcessVar, double observationVar)
        {
            _qLevel = levelProcessVar;
            _qSlope = slopeProcessVar;
            _r = Math.Max(observationVar, double.Epsilon);
        }

        public void Update(double observation)
        {
            if (!_initialized)
            {
                Level = observation;
                Slope = 0.0;
                _initialized = true;
                return;
            }

            Level += Slope;

            double p00 = _p00 + _p01 + _p10 + _p11 + _qLevel;
            double p01 = _p01 + _p11;
            double p10 = _p10 + _p11;
            double p11 = _p11 + _qSlope;

            double innovation = observation - Level;
            double s = p00 + _r;
            if (s <= 0)
                return;

            double k0 = p00 / s;
            double k1 = p10 / s;

            Level += k0 * innovation;
            Slope += k1 * innovation;

            _p00 = p00 - k0 * p00;
            _p01 = p01 - k0 * p01;
            _p10 = p10 - k1 * p00;
            _p11 = p11 - k1 * p01;
        }
    }

    // =====================================================================
    // Regresion dinamica por Kalman:  y = alpha + beta * x + e
    //   x = [alpha, beta], F = I, H = [1, x_t].  Devuelve el residual e_t.
    // =====================================================================
    internal sealed class KalmanHedgeFilter
    {
        private readonly double _qAlpha;
        private readonly double _qBeta;
        private readonly double _r;

        private double _p00 = 1.0, _p01, _p10, _p11 = 1.0;
        private bool _initialized;

        public double Alpha { get; private set; }
        public double Beta { get; private set; }

        public KalmanHedgeFilter(double alphaProcessVar, double betaProcessVar, double observationVar)
        {
            _qAlpha = alphaProcessVar;
            _qBeta = betaProcessVar;
            _r = Math.Max(observationVar, double.Epsilon);
        }

        public double Update(double y, double x)
        {
            if (!_initialized)
            {
                Beta = x != 0 ? y / x : 1.0;
                Alpha = 0.0;
                _initialized = true;
                return 0.0;
            }

            double p00 = _p00 + _qAlpha;
            double p01 = _p01;
            double p10 = _p10;
            double p11 = _p11 + _qBeta;

            double residual = y - (Alpha + Beta * x);

            double s = p00 + x * (p01 + p10) + x * x * p11 + _r;
            if (s <= 0 || double.IsNaN(s))
                return residual;

            double k0 = (p00 + x * p01) / s;
            double k1 = (p10 + x * p11) / s;

            Alpha += k0 * residual;
            Beta += k1 * residual;

            double n00 = p00 - k0 * (p00 + x * p10);
            double n01 = p01 - k0 * (p01 + x * p11);
            double n10 = p10 - k1 * (p00 + x * p10);
            double n11 = p11 - k1 * (p01 + x * p11);

            _p00 = n00;
            _p01 = n01;
            _p10 = n10;
            _p11 = n11;

            return residual;
        }
    }

    // =====================================================================
    // Media, desviacion tipica y mediana sobre ventana movil.
    // Sin una sigma medida sobre valores reales, un z-score no significa nada:
    // esa fue la causa raiz del bot original.
    // =====================================================================
    internal sealed class RollingStats
    {
        private readonly Queue<double> _window;
        private readonly int _capacity;
        private double _sum;
        private double _sumSquares;

        public RollingStats(int capacity)
        {
            _capacity = Math.Max(2, capacity);
            _window = new Queue<double>(_capacity);
        }

        public double Last { get; private set; }

        public int Count
        {
            get { return _window.Count; }
        }

        public bool IsReady
        {
            get { return _window.Count >= _capacity; }
        }

        public double Mean
        {
            get { return _window.Count == 0 ? 0.0 : _sum / _window.Count; }
        }

        public double StandardDeviation
        {
            get
            {
                int n = _window.Count;
                if (n < 2)
                    return 0.0;

                double variance = (_sumSquares - _sum * _sum / n) / (n - 1);
                return variance > 0 ? Math.Sqrt(variance) : 0.0;
            }
        }

        /// <summary>
        /// Mediana de la ventana. Para volumen es mucho mejor referencia que
        /// la media: una sola barra de noticias dispara la media y desactiva
        /// el filtro justo cuando mas falta hace.
        /// </summary>
        public double Median
        {
            get
            {
                int n = _window.Count;
                if (n == 0)
                    return 0.0;

                var sorted = _window.ToArray();
                Array.Sort(sorted);

                return n % 2 == 1
                    ? sorted[n / 2]
                    : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            }
        }

        /// <summary>Percentil por interpolacion sobre la ventana ordenada.</summary>
        public double Percentile(double p)
        {
            int n = _window.Count;
            if (n == 0)
                return 0.0;

            var sorted = _window.ToArray();
            Array.Sort(sorted);

            double pos = (n - 1) * Math.Min(Math.Max(p, 0.0), 1.0);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);

            return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        public void Add(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            Last = value;
            _window.Enqueue(value);
            _sum += value;
            _sumSquares += value * value;

            if (_window.Count > _capacity)
            {
                double removed = _window.Dequeue();
                _sum -= removed;
                _sumSquares -= removed * removed;
            }
        }
    }
}
