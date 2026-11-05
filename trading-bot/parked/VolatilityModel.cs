using System;

namespace cAlgo.Robots
{
    // =====================================================================
    // VolatilityModel  -  componente compartido
    //
    // Pronostico de volatilidad para dimensionar la posicion y las distancias
    // de stop en unidades de sigma, no en pips fijos.
    //
    // Dos componentes, como en la econometria intradia estandar
    // (patron diurno de Andersen-Bollerslev):
    //
    //   sigma(t) = perfil_estacional(minuto_de_sesion) * componente_EWMA
    //
    //   - EWMA (RiskMetrics, lambda ~ 0.94 en datos diarios; mas alto en
    //     intradia) captura el nivel actual de volatilidad.
    //   - El perfil estacional captura que el oro se mueve mucho mas en la
    //     apertura de Londres y de NY que en el almuerzo europeo. Se estima
    //     OFFLINE en Python (research/volatility.py) y se embarca como un
    //     vector de multiplicadores por franja horaria dentro del model pack.
    //
    // Aqui solo esta la parte de ejecucion: barata y sin estado que derive.
    // =====================================================================

    public sealed class EwmaVolatility
    {
        private readonly double _lambda;
        private double _var;
        private bool _init;
        public int Count { get; private set; }

        /// <param name="lambda">Peso del pasado. 0.94 diario; 0.97-0.99 intradia M1-M5.</param>
        public EwmaVolatility(double lambda = 0.97)
        {
            _lambda = Math.Min(0.9999, Math.Max(0.80, lambda));
        }

        /// <summary>Alimenta un retorno logaritmico (o simple) de la barra cerrada.</summary>
        public void Update(double ret)
        {
            if (double.IsNaN(ret) || double.IsInfinity(ret)) return;
            if (!_init) { _var = ret * ret; _init = true; }
            else _var = _lambda * _var + (1 - _lambda) * ret * ret;
            Count++;
        }

        /// <summary>Sigma por barra, en las mismas unidades que el retorno alimentado.</summary>
        public double Sigma => _init ? Math.Sqrt(_var) : 0.0;

        public bool IsReady(int minBars = 50) => Count >= minBars;
    }

    /// <summary>
    /// Perfil diurno: multiplicadores por franja horaria (en la zona horaria
    /// del mercado). 24 valores (uno por hora) o mas fino si el model pack lo
    /// trae. Valor 1.0 = volatilidad media; 1.8 = 80% por encima de la media.
    /// </summary>
    public sealed class DiurnalProfile
    {
        private readonly double[] _mult;   // longitud = slotsPerDay
        private readonly int _slotsPerDay;

        public DiurnalProfile(double[] multipliers)
        {
            if (multipliers == null || multipliers.Length == 0)
                multipliers = new[] { 1.0 };
            _mult = (double[])multipliers.Clone();
            _slotsPerDay = _mult.Length;
        }

        public static DiurnalProfile Flat() => new DiurnalProfile(new[] { 1.0 });

        /// <param name="secondsIntoDayUtc">Segundos transcurridos del dia UTC.</param>
        public double Multiplier(double secondsIntoDayUtc)
        {
            double frac = Math.Max(0, Math.Min(1, secondsIntoDayUtc / 86400.0));
            int slot = (int)(frac * _slotsPerDay);
            if (slot >= _slotsPerDay) slot = _slotsPerDay - 1;
            double m = _mult[slot];
            return m > 0 && !double.IsNaN(m) ? m : 1.0;
        }
    }

    public sealed class VolatilityModel
    {
        private readonly EwmaVolatility _ewma;
        private readonly DiurnalProfile _diurnal;

        public VolatilityModel(double lambda, double[] diurnalMultipliers)
        {
            _ewma = new EwmaVolatility(lambda);
            _diurnal = new DiurnalProfile(diurnalMultipliers);
        }

        public void Update(double ret) => _ewma.Update(ret);

        public bool IsReady(int minBars = 50) => _ewma.IsReady(minBars);

        /// <summary>
        /// Sigma pronosticada para este instante, ajustada por el patron diurno.
        /// En las mismas unidades que el retorno alimentado (fraccion de precio).
        /// </summary>
        public double ForecastSigma(DateTime utcNow)
        {
            double secs = utcNow.TimeOfDay.TotalSeconds;
            return _ewma.Sigma * _diurnal.Multiplier(secs);
        }

        /// <summary>Sigma pronosticada expresada en pips.</summary>
        public double ForecastSigmaPips(DateTime utcNow, double price, double pipSize)
        {
            if (price <= 0 || pipSize <= 0) return 0.0;
            return ForecastSigma(utcNow) * price / pipSize;
        }
    }
}
