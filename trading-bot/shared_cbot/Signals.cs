using System;

namespace cAlgo.Robots
{
    // =====================================================================
    // Signals  -  la señal validada, en C#
    //
    // Port de research/strategies.py::tsmom y research/kalman.py::atr. Es la
    // configuracion que sobrevivio a las doce pruebas del arnes:
    //
    //     TSMOM con lookback FIJO de 252 barras diarias
    //     salida por trailing de 3xATR, SIN objetivo fijo
    //     stop inicial de 2xATR
    //
    // Dos cosas que NO hay que "mejorar" sin volver a medir:
    //
    //   1. El lookback NO se reoptimiza. Fijarlo en 252 dio Sharpe 0.756
    //      fuera de muestra; reoptimizarlo cada año lo bajo a 0.591. Buscar
    //      parametros añade ruido, no ventaja. Hay meseta ancha: 252, 378 y
    //      504 dan 0.73-0.76, asi que el valor exacto no es critico -- lo
    //      critico es no ir cambiandolo.
    //
    //   2. La salida NO lleva objetivo fijo. Un take-profit amputa la cola
    //      derecha, que es de donde vive toda la esperanza de un seguidor de
    //      tendencia: con objetivo fijo la asimetria de R cae a 0.2 y ninguna
    //      ganadora supera el R:R configurado. Con trailing, una sola
    //      operacion del backtest llego a +96R.
    //
    // Estas clases son .NET puro, sin dependencia de cAlgo.API, para poder
    // probarlas fuera de cTrader (shared_cbot/tests).
    // =====================================================================

    public static class Signals
    {
        /// <summary>
        /// Time-series momentum (Moskowitz, Ooi & Pedersen 2012).
        /// Devuelve +1, -1 o 0 comparando el cierre actual con el de hace
        /// `lookback` barras. Un parametro y ninguna suposicion.
        /// </summary>
        public static int Tsmom(double[] close, int index, int lookback)
        {
            if (close == null || lookback <= 0) return 0;
            if (index < lookback || index >= close.Length) return 0;

            double previo = close[index - lookback];
            if (previo <= 0 || double.IsNaN(previo)) return 0;

            double actual = close[index];
            if (actual > previo) return 1;
            if (actual < previo) return -1;
            return 0;
        }

        /// <summary>
        /// ATR exponencial. Equivale a Indicators.AverageTrueRange(bars, n,
        /// MovingAverageType.Exponential), pero se calcula aqui para que la
        /// logica sea identica a la del backtest de Python y se pueda fijar
        /// con vectores de prueba.
        /// </summary>
        public static double[] Atr(double[] high, double[] low, double[] close, int period)
        {
            int n = close.Length;
            var salida = new double[n];
            for (int i = 0; i < n; i++) salida[i] = double.NaN;
            if (n <= period || period < 1) return salida;

            var tr = new double[n];
            tr[0] = high[0] - low[0];
            for (int i = 1; i < n; i++)
            {
                double a = high[i] - low[i];
                double b = Math.Abs(high[i] - close[i - 1]);
                double c = Math.Abs(low[i] - close[i - 1]);
                tr[i] = Math.Max(a, Math.Max(b, c));
            }

            double suma = 0;
            for (int i = 0; i < period; i++) suma += tr[i];
            double acc = suma / period;
            salida[period - 1] = acc;

            double alpha = 2.0 / (period + 1.0);
            for (int i = period; i < n; i++)
            {
                acc = alpha * tr[i] + (1 - alpha) * acc;
                salida[i] = acc;
            }
            return salida;
        }
    }

    /// <summary>
    /// Stop que persigue al precio y NUNCA retrocede. Es la pieza que mas
    /// influyo en el resultado de todo el proyecto: cambiar el objetivo fijo
    /// por esto casi triplico el rendimiento de la misma señal.
    /// </summary>
    public static class TrailingExit
    {
        /// <summary>
        /// Nuevo precio de stop, o el actual si no debe moverse.
        /// </summary>
        /// <param name="esLargo">true para compras.</param>
        /// <param name="barHigh">maximo de la barra cerrada.</param>
        /// <param name="barLow">minimo de la barra cerrada.</param>
        /// <param name="atr">ATR de la barra cerrada.</param>
        /// <param name="multiplo">distancia del trailing, en ATR (3.0 validado).</param>
        /// <param name="stopActual">stop vigente.</param>
        public static double NuevoStop(bool esLargo, double barHigh, double barLow,
                                       double atr, double multiplo, double stopActual)
        {
            if (double.IsNaN(atr) || atr <= 0 || multiplo <= 0) return stopActual;
            double dist = atr * multiplo;

            if (esLargo)
            {
                double cand = barHigh - dist;
                return cand > stopActual ? cand : stopActual;
            }
            else
            {
                double cand = barLow + dist;
                return cand < stopActual ? cand : stopActual;
            }
        }
    }
}
