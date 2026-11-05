using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // =====================================================================
    // MarkowitzPortfolioBot  -  v1
    //
    // Teoria clasica de cartera (Markowitz, 1952) llevada a cTrader en su
    // forma mas desnuda posible. No hay indicadores, ni senales, ni filtros.
    // Solo hay dos numeros por activo y uno por pareja de activos:
    //
    //     mu     = retorno esperado       (media de los retornos)
    //     sigma  = riesgo                 (desviacion tipica)
    //     rho    = como se mueven juntos  (correlacion)
    //
    // La tesis de Markowitz es que el riesgo de una cartera NO es la suma de
    // los riesgos de sus partes: la covarianza se come un trozo. De ahi sale
    // la diversificacion, y de ahi que la cartera optima casi nunca sea
    // "todo al activo con mejor retorno".
    //
    // ---------------------------------------------------------------------
    // LAS CUATRO LINEAS QUE CONTIENEN TODA LA TEORIA
    //
    //   1) Riesgo de la cartera        var(w) = w' * Sigma * w
    //   2) Retorno de la cartera       E[r](w) = w' * mu
    //   3) Minima varianza             w  proporcional a  Sigma^-1 * 1
    //   4) Maximo Sharpe (tangencia)   w  proporcional a  Sigma^-1 * (mu - rf)
    //
    // Las lineas 3 y 4 son la solucion analitica del problema con la unica
    // restriccion sum(w) = 1. Ambas se resuelven aqui con una eliminacion
    // gaussiana de treinta lineas: no hace falta ningun optimizador.
    //
    // ---------------------------------------------------------------------
    // TEOREMA DE LOS DOS FONDOS
    //
    // Toda la frontera eficiente es combinacion lineal de esas dos carteras:
    //
    //     w(alpha) = w_minvar + alpha * (w_tangencia - w_minvar)
    //
    // Es lo que dibuja el grafico ASCII del log: variando alpha se barre la
    // frontera entera. Los digitos son los activos individuales; la curva
    // pasa siempre a su izquierda. Ese hueco horizontal ES la
    // diversificacion, medida en unidades de riesgo.
    //
    // ---------------------------------------------------------------------
    // FLUJO
    //
    //   Capa 0  Datos      Cierres alineados por fecha entre todos los
    //                      simbolos (una fecha que le falte a uno se descarta
    //                      para todos: Sigma exige simultaneidad).
    //   Capa 1  Momentos   mu, Sigma y shrinkage sobre las correlaciones.
    //   Capa 2  Optimo     Sigma^-1 * 1  o  Sigma^-1 * (mu - rf).
    //   Capa 3  Ejecucion  pesos -> nocional -> unidades -> ajuste al neto.
    //   Capa 4  Didactica  panel de pesos + frontera eficiente en ASCII.
    //
    // ---------------------------------------------------------------------
    // LO QUE ESTE ROBOT NO ESCONDE (limitaciones de la teoria clasica)
    //
    //   - mu estimado con la media muestral es ruido casi puro. Por eso el
    //     modo por defecto es MinimaVarianza: no usa mu en absoluto.
    //   - Sigma^-1 amplifica el error de estimacion. De ahi el shrinkage.
    //   - Markowitz es de un solo periodo: rebalanceamos cada N barras y
    //     asumimos que los momentos aguantan entre medias.
    //   - Sin restriccion de cortos los pesos pueden ser extremos. El modo
    //     solo-largos los recorta activo a activo y reoptimiza.
    //
    // Requiere backtesting multi-simbolo activado en cTrader.
    // =====================================================================

    public enum MarkowitzObjective
    {
        MinimaVarianza,
        MaximoSharpe
    }

    [Robot(AccessRights = AccessRights.None)]
    public class MarkowitzPortfolioBot : Robot
    {
        // -----------------------------------------------------------------
        // Parametros
        // -----------------------------------------------------------------

        [Parameter("Simbolos (separados por coma)", DefaultValue = "EURUSD,GBPUSD,USDJPY,XAUUSD", Group = "Universo")]
        public string SymbolList { get; set; }

        [Parameter("Timeframe de los retornos", DefaultValue = "Daily", Group = "Universo")]
        public TimeFrame ReturnsTimeFrame { get; set; }

        [Parameter("Ventana (numero de retornos)", DefaultValue = 120, MinValue = 20, MaxValue = 2000, Group = "Universo")]
        public int Lookback { get; set; }

        [Parameter("Periodos por anio (anualizacion)", DefaultValue = 252, MinValue = 1, Group = "Universo")]
        public int PeriodsPerYear { get; set; }

        [Parameter("Objetivo", DefaultValue = MarkowitzObjective.MinimaVarianza, Group = "Optimizacion")]
        public MarkowitzObjective Objective { get; set; }

        [Parameter("Permitir pesos negativos (cortos)", DefaultValue = false, Group = "Optimizacion")]
        public bool AllowShorts { get; set; }

        [Parameter("Tasa libre de riesgo anual %", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 20.0, Step = 0.25, Group = "Optimizacion")]
        public double RiskFreeAnnualPct { get; set; }

        [Parameter("Shrinkage de correlaciones 0-1", DefaultValue = 0.10, MinValue = 0.0, MaxValue = 0.95, Step = 0.05, Group = "Optimizacion")]
        public double Shrinkage { get; set; }

        [Parameter("Exposicion bruta (x equity)", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 20.0, Step = 0.25, Group = "Ejecucion")]
        public double GrossExposure { get; set; }

        [Parameter("Rebalanceo cada N barras", DefaultValue = 20, MinValue = 1, Group = "Ejecucion")]
        public int RebalanceEveryBars { get; set; }

        [Parameter("Banda de no-negociacion % del objetivo", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 90.0, Step = 5.0, Group = "Ejecucion")]
        public double NoTradeBandPct { get; set; }

        [Parameter("Etiqueta", DefaultValue = "Markowitz", Group = "Ejecucion")]
        public string TradeLabel { get; set; }

        [Parameter("Panel en el grafico", DefaultValue = true, Group = "Didactica")]
        public bool ShowPanel { get; set; }

        [Parameter("Frontera eficiente en el log", DefaultValue = true, Group = "Didactica")]
        public bool ShowFrontier { get; set; }

        // -----------------------------------------------------------------
        // Estado
        // -----------------------------------------------------------------

        private sealed class Asset
        {
            public string Name;
            public Symbol Symbol;
            public Bars Bars;
            public double Weight;      // peso objetivo (fraccion de la exposicion bruta)
            public double MuAnnual;    // retorno esperado anualizado
            public double SigmaAnnual; // riesgo anualizado
            public double TargetUnits; // unidades objetivo, con signo
        }

        private readonly List<Asset> _assets = new List<Asset>();
        private Bars _refBars;
        private int _barsSinceRebalance;

        // -----------------------------------------------------------------
        // Capa 0  -  Datos
        // -----------------------------------------------------------------

        protected override void OnStart()
        {
            var names = SymbolList
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count < 2)
            {
                Print("Markowitz necesita al menos 2 simbolos. Con uno solo no hay covarianza que explotar.");
                Stop();
                return;
            }

            foreach (var name in names)
            {
                Symbol symbol = null;

                try
                {
                    symbol = Symbols.GetSymbol(name);
                }
                catch (Exception e)
                {
                    Print("Simbolo descartado: {0} ({1})", name, e.Message);
                }

                if (symbol == null)
                    continue;

                var bars = MarketData.GetBars(ReturnsTimeFrame, name);
                LoadHistory(bars, Lookback + 5);

                _assets.Add(new Asset { Name = name, Symbol = symbol, Bars = bars });
            }

            if (_assets.Count < 2)
            {
                Print("Menos de 2 simbolos utilizables. Fin.");
                Stop();
                return;
            }

            _refBars = _assets[0].Bars;
            _refBars.BarOpened += OnReturnsBarOpened;

            Print("MarkowitzPortfolioBot: {0} activos | ventana {1} x {2} | objetivo {3} | cortos: {4}",
                _assets.Count, Lookback, ReturnsTimeFrame, Objective, AllowShorts ? "si" : "no");

            // Primer rebalanceo inmediato si ya hay historico suficiente.
            _barsSinceRebalance = RebalanceEveryBars;
            TryRebalance();
        }

        // El reloj del robot es el timeframe de los retornos, no el del grafico:
        // asi la ventana estadistica y el ciclo de rebalanceo hablan el mismo idioma.
        private void OnReturnsBarOpened(BarOpenedEventArgs args)
        {
            _barsSinceRebalance++;
            TryRebalance();
        }

        private static void LoadHistory(Bars bars, int wanted)
        {
            var guard = 0;
            while (bars.Count < wanted && guard++ < 50)
            {
                if (bars.LoadMoreHistory() <= 0)
                    break;
            }
        }

        // Sigma solo tiene sentido si los retornos son simultaneos. Alineamos por
        // fecha de apertura y descartamos toda fecha que le falte a algun activo.
        private double[][] BuildAlignedReturns(out int used, out int dropped)
        {
            dropped = 0;

            var n = _assets.Count;
            var series = new List<double>[n];
            for (var i = 0; i < n; i++)
                series[i] = new List<double>();

            // Hacia atras desde la ultima barra CERRADA de la referencia (Count - 2).
            var candidates = new List<DateTime>();
            for (var i = _refBars.Count - 2; i >= 1 && candidates.Count < Lookback; i--)
                candidates.Add(_refBars.OpenTimes[i]);
            candidates.Reverse();

            foreach (var time in candidates)
            {
                var row = new double[n];
                var complete = true;

                for (var i = 0; i < n; i++)
                {
                    var bars = _assets[i].Bars;
                    var idx = bars.OpenTimes.GetIndexByTime(time);

                    if (idx < 1 || idx > bars.Count - 2)
                    {
                        complete = false;
                        break;
                    }

                    var previous = bars.ClosePrices[idx - 1];
                    var current = bars.ClosePrices[idx];

                    if (previous <= 0 || current <= 0)
                    {
                        complete = false;
                        break;
                    }

                    // Retorno logaritmico: aditivo en el tiempo, simetrico en el signo.
                    row[i] = Math.Log(current / previous);
                }

                if (!complete)
                {
                    dropped++;
                    continue;
                }

                for (var i = 0; i < n; i++)
                    series[i].Add(row[i]);
            }

            used = series[0].Count;
            return series.Select(s => s.ToArray()).ToArray();
        }

        // -----------------------------------------------------------------
        // Capa 1  -  Momentos:  mu  y  Sigma
        // -----------------------------------------------------------------

        private static double[] Mean(double[][] returns)
        {
            return returns.Select(r => r.Average()).ToArray();
        }

        private static double[,] Covariance(double[][] returns, double[] mu)
        {
            var n = returns.Length;
            var k = returns[0].Length;
            var cov = new double[n, n];

            for (var i = 0; i < n; i++)
            {
                for (var j = i; j < n; j++)
                {
                    var acc = 0.0;
                    for (var t = 0; t < k; t++)
                        acc += (returns[i][t] - mu[i]) * (returns[j][t] - mu[j]);

                    var c = acc / (k - 1); // estimador insesgado
                    cov[i, j] = c;
                    cov[j, i] = c;
                }
            }

            return cov;
        }

        // Shrinkage: encogemos las CORRELACIONES hacia cero dejando intactas las
        // varianzas. Sigma^-1 amplifica el error de estimacion de las covarianzas;
        // esto es el seguro barato contra pesos disparatados.
        private static void ShrinkCorrelations(double[,] cov, double lambda)
        {
            if (lambda <= 0)
                return;

            var n = cov.GetLength(0);
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                    if (i != j)
                        cov[i, j] *= 1.0 - lambda;
        }

        // -----------------------------------------------------------------
        // Capa 2  -  El optimo cerrado de Markowitz
        // -----------------------------------------------------------------

        // Minima varianza:  w = Sigma^-1 * 1 / (1' * Sigma^-1 * 1)
        // No usa mu. Es la cartera que la teoria estima mejor, porque las medias
        // muestrales de los retornos son casi indistinguibles del ruido.
        private static double[] MinVarianceWeights(double[,] cov)
        {
            var n = cov.GetLength(0);
            var ones = Enumerable.Repeat(1.0, n).ToArray();

            var x = Solve(cov, ones);
            if (x == null)
                return null;

            var sum = x.Sum();
            if (Math.Abs(sum) < 1e-12)
                return null;

            return x.Select(v => v / sum).ToArray();
        }

        // Tangencia / maximo Sharpe:  w = Sigma^-1 * (mu - rf) / suma
        // Aqui si entra mu, con todo su ruido. Por eso no es el modo por defecto.
        private static double[] TangencyWeights(double[,] cov, double[] mu, double riskFree)
        {
            var n = cov.GetLength(0);
            var excess = new double[n];
            for (var i = 0; i < n; i++)
                excess[i] = mu[i] - riskFree;

            var x = Solve(cov, excess);
            if (x == null)
                return null;

            var sum = x.Sum();
            if (sum <= 1e-12) // suma nula o negativa: no hay tangencia por el lado largo
                return null;

            return x.Select(v => v / sum).ToArray();
        }

        // Restriccion de no-negatividad resuelta como se hacia antes de los
        // optimizadores numericos: se elimina el peso mas negativo, se reoptimiza
        // sobre el subconjunto restante, y se repite hasta que no quede ninguno.
        private static double[] ApplyLongOnly(Func<int[], double[]> solveOnSubset, int n)
        {
            var active = Enumerable.Range(0, n).ToArray();

            for (var guard = 0; guard < n; guard++)
            {
                var sub = solveOnSubset(active);
                if (sub == null)
                    return null;

                var worst = -1;
                var worstValue = -1e-9;
                for (var i = 0; i < sub.Length; i++)
                {
                    if (sub[i] < worstValue)
                    {
                        worstValue = sub[i];
                        worst = i;
                    }
                }

                if (worst < 0 || active.Length == 1)
                {
                    var full = new double[n];
                    for (var i = 0; i < active.Length; i++)
                        full[active[i]] = Math.Max(0.0, sub[i]);

                    var sum = full.Sum();
                    if (sum < 1e-12)
                        return null;

                    return full.Select(v => v / sum).ToArray();
                }

                active = active.Where((_, i) => i != worst).ToArray();
            }

            return null;
        }

        private static double[,] SubMatrix(double[,] m, int[] idx)
        {
            var s = new double[idx.Length, idx.Length];
            for (var i = 0; i < idx.Length; i++)
                for (var j = 0; j < idx.Length; j++)
                    s[i, j] = m[idx[i], idx[j]];
            return s;
        }

        private static double[] SubVector(double[] v, int[] idx)
        {
            return idx.Select(i => v[i]).ToArray();
        }

        // Eliminacion de Gauss-Jordan con pivoteo parcial: resuelve A x = b.
        // Es todo el algebra lineal que Markowitz necesita.
        private static double[] Solve(double[,] a, double[] b)
        {
            var n = b.Length;
            var m = new double[n, n + 1];

            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                    m[i, j] = a[i, j];
                m[i, n] = b[i];
            }

            for (var c = 0; c < n; c++)
            {
                var pivot = c;
                for (var r = c + 1; r < n; r++)
                    if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c]))
                        pivot = r;

                // Singular: activos redundantes, ventana demasiado corta, o ambas.
                if (Math.Abs(m[pivot, c]) < 1e-16)
                    return null;

                if (pivot != c)
                {
                    for (var k = c; k <= n; k++)
                    {
                        var tmp = m[c, k];
                        m[c, k] = m[pivot, k];
                        m[pivot, k] = tmp;
                    }
                }

                var d = m[c, c];
                for (var k = c; k <= n; k++)
                    m[c, k] /= d;

                for (var r = 0; r < n; r++)
                {
                    if (r == c)
                        continue;

                    var f = m[r, c];
                    if (Math.Abs(f) < 1e-300)
                        continue;

                    for (var k = c; k <= n; k++)
                        m[r, k] -= f * m[c, k];
                }
            }

            var x = new double[n];
            for (var i = 0; i < n; i++)
                x[i] = m[i, n];

            return x;
        }

        private static double PortfolioVariance(double[,] cov, double[] w)
        {
            var acc = 0.0;
            for (var i = 0; i < w.Length; i++)
                for (var j = 0; j < w.Length; j++)
                    acc += w[i] * cov[i, j] * w[j];
            return Math.Max(acc, 0.0);
        }

        private static double PortfolioReturn(double[] mu, double[] w)
        {
            var acc = 0.0;
            for (var i = 0; i < w.Length; i++)
                acc += w[i] * mu[i];
            return acc;
        }

        // -----------------------------------------------------------------
        // Orquestacion del rebalanceo
        // -----------------------------------------------------------------

        private void TryRebalance()
        {
            if (_barsSinceRebalance < RebalanceEveryBars)
                return;

            int used, dropped;
            var returns = BuildAlignedReturns(out used, out dropped);

            if (used < Math.Max(20, _assets.Count + 5))
            {
                Print("Historico alineado insuficiente ({0} retornos). Esperando.", used);
                return;
            }

            _barsSinceRebalance = 0;

            var n = _assets.Count;
            var mu = Mean(returns);
            var cov = Covariance(returns, mu);
            ShrinkCorrelations(cov, Shrinkage);

            var riskFree = RiskFreeAnnualPct / 100.0 / PeriodsPerYear;

            // Las dos carteras que generan la frontera entera (teorema de los dos fondos).
            var minVar = MinVarianceWeights(cov);
            var tangency = TangencyWeights(cov, mu, riskFree);

            double[] w;

            if (Objective == MarkowitzObjective.MaximoSharpe)
            {
                w = AllowShorts
                    ? tangency
                    : ApplyLongOnly(idx => TangencyWeights(SubMatrix(cov, idx), SubVector(mu, idx), riskFree), n);

                if (w == null)
                {
                    Print("Sin cartera de tangencia valida (excesos de retorno no positivos). Uso minima varianza.");
                    w = AllowShorts ? minVar : ApplyLongOnly(idx => MinVarianceWeights(SubMatrix(cov, idx)), n);
                }
            }
            else
            {
                w = AllowShorts
                    ? minVar
                    : ApplyLongOnly(idx => MinVarianceWeights(SubMatrix(cov, idx)), n);
            }

            if (w == null)
            {
                Print("Optimizacion fallida (Sigma singular). Sube la ventana o el shrinkage.");
                return;
            }

            // Normalizamos por exposicion BRUTA: con cortos, sum(w) = 1 no acota el
            // apalancamiento; sum(|w|) si.
            var gross = w.Sum(Math.Abs);
            if (gross < 1e-12)
                return;

            for (var i = 0; i < n; i++)
            {
                _assets[i].Weight = w[i] / gross;
                _assets[i].MuAnnual = mu[i] * PeriodsPerYear;
                _assets[i].SigmaAnnual = Math.Sqrt(Math.Max(cov[i, i], 0.0) * PeriodsPerYear);
            }

            var muPortfolio = PortfolioReturn(mu, w) * PeriodsPerYear;
            var sigmaPortfolio = Math.Sqrt(PortfolioVariance(cov, w) * PeriodsPerYear);
            var sharpe = sigmaPortfolio > 1e-12
                ? (muPortfolio - RiskFreeAnnualPct / 100.0) / sigmaPortfolio
                : 0.0;

            Rebalance();
            Report(used, dropped, muPortfolio, sigmaPortfolio, sharpe);

            if (ShowFrontier && minVar != null && tangency != null)
                PrintFrontier(cov, mu, minVar, tangency);
        }

        // -----------------------------------------------------------------
        // Capa 3  -  Ejecucion:  peso -> nocional -> unidades
        // -----------------------------------------------------------------

        // Valor en divisa de la cuenta de UNA unidad del simbolo:
        //   unidades * precio  = nocional en divisa de cotizacion
        //   PipValue / PipSize = tipo de cambio cotizacion -> cuenta
        private static double UnitValueInAccountCurrency(Symbol s)
        {
            if (s.PipSize <= 0 || s.PipValue <= 0 || s.Ask <= 0)
                return 0;

            return s.Ask * (s.PipValue / s.PipSize);
        }

        private void Rebalance()
        {
            var capital = Account.Equity * GrossExposure;

            foreach (var asset in _assets)
            {
                var unitValue = UnitValueInAccountCurrency(asset.Symbol);

                if (unitValue <= 0)
                {
                    Print("{0}: sin precio o PipValue todavia; lo dejo plano este ciclo.", asset.Name);
                    asset.TargetUnits = 0;
                }
                else
                {
                    var notional = capital * Math.Abs(asset.Weight);
                    var units = asset.Symbol.NormalizeVolumeInUnits(notional / unitValue, RoundingMode.ToNearest);

                    if (units < asset.Symbol.VolumeInUnitsMin)
                        units = 0;

                    asset.TargetUnits = asset.Weight < 0 ? -units : units;
                }

                SetNetUnits(asset);
            }
        }

        private double NetUnits(Asset asset)
        {
            var net = 0.0;
            foreach (var position in Positions.FindAll(TradeLabel, asset.Name))
                net += position.TradeType == TradeType.Buy ? position.VolumeInUnits : -position.VolumeInUnits;
            return net;
        }

        private void SetNetUnits(Asset asset)
        {
            var target = asset.TargetUnits;
            var net = NetUnits(asset);

            // Cambio de signo o salida total: cerramos limpio antes de reconstruir,
            // para no quedarnos con dos posiciones opuestas cubriendose entre si.
            if (net != 0 && (target == 0 || Math.Sign(target) != Math.Sign(net)))
            {
                foreach (var position in Positions.FindAll(TradeLabel, asset.Name))
                    ClosePosition(position);

                net = 0;
            }

            if (target == 0)
                return;

            var diff = target - net;

            // Banda de no-negociacion: rebalancear cada desviacion minima solo paga spread.
            var band = Math.Max(Math.Abs(target) * NoTradeBandPct / 100.0, asset.Symbol.VolumeInUnitsMin);
            if (Math.Abs(diff) < band)
                return;

            if (Math.Sign(diff) == Math.Sign(target))
                Increase(asset, Math.Abs(diff), target > 0 ? TradeType.Buy : TradeType.Sell);
            else
                Reduce(asset, Math.Abs(diff));
        }

        private void Increase(Asset asset, double units, TradeType side)
        {
            var volume = asset.Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (volume < asset.Symbol.VolumeInUnitsMin)
                return;

            var result = ExecuteMarketOrder(side, asset.Name, volume, TradeLabel);
            if (!result.IsSuccessful)
                Print("{0}: orden rechazada ({1})", asset.Name, result.Error);
        }

        private void Reduce(Asset asset, double units)
        {
            var remaining = units;

            foreach (var position in Positions.FindAll(TradeLabel, asset.Name))
            {
                if (remaining < asset.Symbol.VolumeInUnitsMin)
                    return;

                if (position.VolumeInUnits <= remaining + 1e-9)
                {
                    remaining -= position.VolumeInUnits;
                    ClosePosition(position);
                }
                else
                {
                    var volume = asset.Symbol.NormalizeVolumeInUnits(remaining, RoundingMode.Down);
                    if (volume >= asset.Symbol.VolumeInUnitsMin)
                        ClosePosition(position, volume);

                    return;
                }
            }
        }

        // -----------------------------------------------------------------
        // Capa 4  -  Didactica: panel y frontera eficiente
        // -----------------------------------------------------------------

        private void Report(int used, int dropped, double muPortfolio, double sigmaPortfolio, double sharpe)
        {
            var sb = new StringBuilder();

            sb.AppendLine("MARKOWITZ  " + Objective + (AllowShorts ? "  (cortos permitidos)" : "  (solo largos)"));
            sb.AppendLine(string.Format("ventana {0} retornos x {1}   descartados {2}   shrinkage {3:P0}",
                used, ReturnsTimeFrame, dropped, Shrinkage));
            sb.AppendLine(string.Format("CARTERA   E[r] {0:P2}   sigma {1:P2}   Sharpe {2:F2}   bruto {3:F2}x",
                muPortfolio, sigmaPortfolio, sharpe, GrossExposure));
            sb.AppendLine("---------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-10} {1,8} {2,9} {3,9} {4,12}", "activo", "peso", "E[r]", "sigma", "unidades"));

            foreach (var asset in _assets)
            {
                sb.AppendLine(string.Format("{0,-10} {1,8:P1} {2,9:P2} {3,9:P2} {4,12:N0}",
                    asset.Name, asset.Weight, asset.MuAnnual, asset.SigmaAnnual, asset.TargetUnits));
            }

            var text = sb.ToString();

            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0)
                    Print(line.TrimEnd());

            if (ShowPanel)
                Chart.DrawStaticText("markowitz", text, VerticalAlignment.Top, HorizontalAlignment.Left, Color.Gainsboro);
        }

        // Frontera eficiente por el teorema de los dos fondos: barremos
        // w(alpha) = w_mv + alpha * (w_tan - w_mv) y proyectamos cada cartera
        // sobre el plano (riesgo, retorno). Los digitos son los activos sueltos;
        // la curva pasa a su izquierda, y ese hueco es la diversificacion.
        //
        // Nota: la frontera se dibuja SIEMPRE sin restricciones (es la de la
        // teoria clasica), aunque la cartera ejecutada sea solo-largos.
        private void PrintFrontier(double[,] cov, double[] mu, double[] minVar, double[] tangency)
        {
            const int rows = 13;
            const int cols = 52;
            const int steps = 90;

            var n = _assets.Count;
            var xs = new List<double>();
            var ys = new List<double>();

            for (var s = 0; s <= steps; s++)
            {
                var alpha = -0.5 + 2.5 * s / steps;
                var w = new double[n];
                for (var i = 0; i < n; i++)
                    w[i] = minVar[i] + alpha * (tangency[i] - minVar[i]);

                xs.Add(Math.Sqrt(PortfolioVariance(cov, w) * PeriodsPerYear));
                ys.Add(PortfolioReturn(mu, w) * PeriodsPerYear);
            }

            var assetX = _assets.Select(a => a.SigmaAnnual).ToList();
            var assetY = _assets.Select(a => a.MuAnnual).ToList();

            var xMin = Math.Min(xs.Min(), assetX.Min());
            var xMax = Math.Max(xs.Max(), assetX.Max());
            var yMin = Math.Min(ys.Min(), assetY.Min());
            var yMax = Math.Max(ys.Max(), assetY.Max());

            if (xMax - xMin < 1e-9 || yMax - yMin < 1e-9)
                return;

            var grid = new char[rows, cols];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                    grid[r, c] = ' ';

            Action<double, double, char> plot = (x, y, mark) =>
            {
                var c = (int)Math.Round((x - xMin) / (xMax - xMin) * (cols - 1));
                var r = (int)Math.Round((yMax - y) / (yMax - yMin) * (rows - 1));

                if (c >= 0 && c < cols && r >= 0 && r < rows)
                    grid[r, c] = mark;
            };

            for (var i = 0; i < xs.Count; i++)
                plot(xs[i], ys[i], '.');

            for (var i = 0; i < n; i++)
                plot(assetX[i], assetY[i], (char)('1' + Math.Min(i, 8)));

            plot(Math.Sqrt(PortfolioVariance(cov, minVar) * PeriodsPerYear), PortfolioReturn(mu, minVar) * PeriodsPerYear, 'O');
            plot(Math.Sqrt(PortfolioVariance(cov, tangency) * PeriodsPerYear), PortfolioReturn(mu, tangency) * PeriodsPerYear, '*');

            Print("FRONTERA EFICIENTE   E[r] anual {0:P1} .. {1:P1}   sigma anual {2:P1} .. {3:P1}",
                yMin, yMax, xMin, xMax);

            for (var r = 0; r < rows; r++)
            {
                var line = new StringBuilder("  |");
                for (var c = 0; c < cols; c++)
                    line.Append(grid[r, c]);
                Print(line.ToString());
            }

            Print("  +" + new string('-', cols) + "  riesgo (sigma) ->");
            Print("  O = minima varianza   * = tangencia (max Sharpe)   " +
                  string.Join("  ", _assets.Select((a, i) => (i + 1) + "=" + a.Name)));
        }

        protected override void OnStop()
        {
            // Las posiciones se dejan abiertas a proposito: Markowitz es una
            // asignacion de cartera, no una operativa de entrada y salida.
            if (_refBars != null)
                _refBars.BarOpened -= OnReturnsBarOpened;
        }
    }
}
