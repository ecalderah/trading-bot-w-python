using System;

namespace cAlgo.Robots
{
    // =====================================================================
    // CostGate  -  componente compartido
    //
    // En un scalper el coste NO es una capa mas: es el juego. Con coste ida
    // y vuelta ~25-35 centavos en XAUUSD retail y objetivos de 30-60 pips,
    // la mayoria de sistemas pierden por aritmetica, no por señal:
    //
    //   neto = acierto*ganancia - (1-acierto)*perdida - coste
    //
    // Este componente hace dos cosas:
    //   1. Veta la entrada si:
    //        - el spread actual supera el maximo de la sesion, o
    //        - el TP planificado no cubre 'MinTpToCostRatio' veces el coste
    //          ida y vuelta estimado.
    //   2. Lleva la cuenta del "cost drag" realizado (spread pagado + comision
    //        + swap) para que el bot y el dashboard sepan cuanto del bruto se
    //        esta comiendo el coste, y disparen alarma si pasa del presupuesto.
    //
    // El slippage real NO se conoce hasta operar en vivo. En backtest se usa
    // 'AssumedSlippagePips' como colchon explicito; en vivo se mide y se
    // realimenta.
    // =====================================================================

    public sealed class CostGate
    {
        public double MinTpToCostRatio = 7.0;   // el TP debe valer >= 7x el coste
        public double AssumedSlippagePips = 1.0; // colchon de backtest
        public double CommissionPerLotRoundTrip = 0.0; // en unidades de precio*lote; 0 si va aparte

        // presupuesto: fraccion del bruto ganado que el coste puede consumir
        public double CostBudgetFraction = 0.30;

        // --- acumuladores (los lee el reporte y las alarmas) ---
        public double PaidSpread { get; private set; }
        public double PaidCommission { get; private set; }
        public double PaidSwap { get; private set; }
        public double GrossWon { get; private set; }
        public int Vetoed { get; private set; }
        public int Evaluated { get; private set; }

        public double TotalCost => PaidSpread + PaidCommission + PaidSwap;
        public double CostDragFraction => GrossWon > 0 ? TotalCost / GrossWon : 0.0;
        public bool OverBudget => GrossWon > 0 && CostDragFraction > CostBudgetFraction;

        /// <summary>
        /// Coste ida y vuelta estimado en pips: spread actual + slippage asumido.
        /// (La comision se trata aparte porque muchos brokers la cobran en
        /// moneda, no en pips.)
        /// </summary>
        public double EstimatedRoundTripPips(double currentSpreadPips)
            => Math.Max(0, currentSpreadPips) + Math.Max(0, AssumedSlippagePips);

        public struct Verdict
        {
            public bool Pass;
            public string Reason;
        }

        public Verdict Check(double currentSpreadPips, double maxSpreadPips, double plannedTpPips)
        {
            Evaluated++;

            if (maxSpreadPips > 0 && currentSpreadPips > maxSpreadPips)
            {
                Vetoed++;
                return new Verdict { Pass = false, Reason = $"spread {currentSpreadPips:F1}p > cap {maxSpreadPips:F0}p" };
            }

            double rt = EstimatedRoundTripPips(currentSpreadPips);
            if (rt > 0 && plannedTpPips < rt * MinTpToCostRatio)
            {
                Vetoed++;
                return new Verdict
                {
                    Pass = false,
                    Reason = $"TP {plannedTpPips:F0}p < {MinTpToCostRatio:F0}x coste ({rt:F1}p)"
                };
            }

            return new Verdict { Pass = true, Reason = "coste ok" };
        }

        /// <summary>Registrar el resultado de una operacion cerrada.</summary>
        public void RecordClosedTrade(double grossPnl, double spreadCostPrice, double commission, double swap)
        {
            if (grossPnl > 0) GrossWon += grossPnl;
            PaidSpread += Math.Max(0, spreadCostPrice);
            PaidCommission += Math.Abs(commission);
            PaidSwap += Math.Abs(swap);
        }

        public string Summary()
            => $"coste total {TotalCost:F2} (spread {PaidSpread:F2} + com {PaidCommission:F2} + swap {PaidSwap:F2}) " +
               $"= {CostDragFraction * 100:F1}% del bruto ganado | vetos de coste {Vetoed}/{Evaluated}";
    }
}
