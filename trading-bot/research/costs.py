"""
Aritmetica de costes para un scalper.

Esta es la primera prueba que hay que pasar, y la mas barata: si la
esperanza neta despues de costes no puede ser positiva con parametros
realistas, no hay estrategia que salvar y no merece la pena escribir el bot.

Todo se calcula en USD por onza (unidad natural del XAUUSD), NO en pips.
Un "pip" depende del broker (el de Edgar usa pipSize = 0.1, es decir 1 pip =
$0.10/oz) y razonar en pips es como se cuelan los errores de instrumento que
el FIX 27 del bot actual documenta. La conversion a pips es solo para
presentar.
"""

from __future__ import annotations

from dataclasses import dataclass
import math


# ---------------------------------------------------------------------------
# Costes del broker
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class BrokerCosts:
    """Costes en USD por onza de oro.

    spread_usd:      diferencia bid/ask. Se paga UNA vez por operacion
                     completa (entras al ask, sales al bid).
    commission_usd:  comision ida y vuelta por onza.
                     Referencia: un ECN cobra ~$3.5-7 por lote de 100 oz y
                     por lado -> $0.07-0.14 por onza ida y vuelta.
    slippage_usd:    deslizamiento por lado. En stops y en noticias es mucho
                     peor que en entradas limpias.
    """

    spread_usd: float
    commission_usd: float
    slippage_usd: float

    @property
    def round_trip(self) -> float:
        """Coste total de abrir y cerrar una posicion, USD por onza."""
        return self.spread_usd + self.commission_usd + 2 * self.slippage_usd

    def in_pips(self, pip_size: float = 0.1) -> float:
        return self.round_trip / pip_size

    def describe(self, pip_size: float = 0.1) -> str:
        return (
            f"spread ${self.spread_usd:.2f} + comision ${self.commission_usd:.2f} "
            f"+ slippage 2x${self.slippage_usd:.2f} = ${self.round_trip:.2f}/oz "
            f"({self.in_pips(pip_size):.1f} pips)"
        )


# Escenarios realistas para XAUUSD retail, de mejor a peor.
BROKER_SCENARIOS: dict[str, BrokerCosts] = {
    "ecn_optimo":   BrokerCosts(spread_usd=0.12, commission_usd=0.07, slippage_usd=0.03),
    "ecn_tipico":   BrokerCosts(spread_usd=0.20, commission_usd=0.10, slippage_usd=0.05),
    "estandar":     BrokerCosts(spread_usd=0.35, commission_usd=0.00, slippage_usd=0.08),
    "estandar_malo": BrokerCosts(spread_usd=0.50, commission_usd=0.00, slippage_usd=0.15),
    "asia_o_news":  BrokerCosts(spread_usd=0.90, commission_usd=0.10, slippage_usd=0.30),
}


# ---------------------------------------------------------------------------
# Estadisticas de la estrategia (BRUTAS, antes de costes)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class StrategyStats:
    """Resultado bruto por operacion, en USD por onza.

    win_rate:  fraccion de aciertos (0-1)
    avg_win:   ganancia media de las ganadoras, bruta
    avg_loss:  perdida media de las perdedoras, bruta, en POSITIVO
    """

    win_rate: float
    avg_win: float
    avg_loss: float

    @property
    def rr(self) -> float:
        return self.avg_win / self.avg_loss if self.avg_loss > 0 else math.inf

    def gross_expectancy(self) -> float:
        return self.win_rate * self.avg_win - (1 - self.win_rate) * self.avg_loss

    def net_expectancy(self, costs: BrokerCosts) -> float:
        """Esperanza por operacion despues de costes. Esto es lo unico que importa."""
        return self.gross_expectancy() - costs.round_trip

    def expectancy_r(self, costs: BrokerCosts) -> float:
        """Esperanza neta en multiplos del riesgo (R)."""
        return self.net_expectancy(costs) / self.avg_loss if self.avg_loss > 0 else 0.0


# ---------------------------------------------------------------------------
# Umbrales de supervivencia
# ---------------------------------------------------------------------------

def breakeven_win_rate(avg_win: float, avg_loss: float, costs: BrokerCosts) -> float:
    """Acierto minimo para no perder dinero, YA con el coste dentro.

        p*W - (1-p)*L - C = 0   =>   p = (L + C) / (W + L)

    Sin coste se reduce al clasico 1/(1+RR).
    """
    denom = avg_win + avg_loss
    if denom <= 0:
        return 1.0
    return (avg_loss + costs.round_trip) / denom


def cost_penalty_points(avg_win: float, avg_loss: float, costs: BrokerCosts) -> float:
    """Cuantos PUNTOS de acierto te cobra el coste (en %)."""
    denom = avg_win + avg_loss
    if denom <= 0:
        return 100.0
    return 100.0 * costs.round_trip / denom


def min_target_for_ratio(costs: BrokerCosts, ratio: float) -> float:
    """Objetivo minimo (USD/oz) para que el TP valga 'ratio' veces el coste."""
    return costs.round_trip * ratio


def required_gross_edge(costs: BrokerCosts, trades_per_year: int) -> float:
    """Ventaja bruta anual (USD/oz) que hay que generar solo para empatar."""
    return costs.round_trip * trades_per_year


# ---------------------------------------------------------------------------
# Presentacion
# ---------------------------------------------------------------------------

def verdict(stats: StrategyStats, costs: BrokerCosts) -> tuple[str, str]:
    """(etiqueta, explicacion) sobre la viabilidad."""
    net = stats.net_expectancy(costs)
    need = breakeven_win_rate(stats.avg_win, stats.avg_loss, costs)
    margin_pts = (stats.win_rate - need) * 100

    if net <= 0:
        return ("SIN VENTAJA",
                f"pierdes ${abs(net):.3f}/oz por operacion; te faltan "
                f"{abs(margin_pts):.1f} puntos de acierto")
    if margin_pts < 2.0:
        return ("MARGEN FRAGIL",
                f"solo {margin_pts:.1f} puntos sobre el equilibrio; "
                f"un empeoramiento del coste lo borra")
    return ("VIABLE", f"{margin_pts:.1f} puntos de margen sobre el equilibrio")


def cost_wall_table(costs: BrokerCosts, win_rate: float, rr: float,
                    targets_usd: list[float], pip_size: float = 0.1) -> str:
    """Como cambia la esperanza neta segun el tamaño del objetivo.

    Muestra el 'muro de coste': por debajo de cierto objetivo, ninguna tasa de
    acierto razonable salva la operacion.
    """
    lines = [
        f"  {'TP':>8} {'TP':>7} {'SL':>8} {'coste':>7} {'bruto':>8} {'neto':>8} {'neto':>7}",
        f"  {'(USD)':>8} {'(pips)':>7} {'(USD)':>8} {'/TP':>7} {'(USD)':>8} {'(USD)':>8} {'(R)':>7}",
        "  " + "-" * 56,
    ]
    for tp in targets_usd:
        sl = tp / rr if rr > 0 else tp
        s = StrategyStats(win_rate=win_rate, avg_win=tp, avg_loss=sl)
        gross = s.gross_expectancy()
        net = s.net_expectancy(costs)
        frac = costs.round_trip / tp if tp > 0 else math.inf
        flag = "" if net > 0 else "  <-- pierde"
        lines.append(
            f"  {tp:8.2f} {tp / pip_size:7.0f} {sl:8.2f} {frac:6.0%} "
            f"{gross:8.3f} {net:8.3f} {s.expectancy_r(costs):7.3f}{flag}"
        )
    return "\n".join(lines)


def breakeven_grid(costs: BrokerCosts, targets_usd: list[float],
                   rrs: list[float]) -> str:
    """Acierto necesario (%) para cada combinacion objetivo x R:R."""
    head = "  " + f"{'TP (USD)':>10}" + "".join(f"{'RR ' + f'{r:g}':>9}" for r in rrs)
    lines = [head, "  " + "-" * (10 + 9 * len(rrs))]
    for tp in targets_usd:
        row = f"  {tp:10.2f}"
        for rr in rrs:
            sl = tp / rr
            p = breakeven_win_rate(tp, sl, costs) * 100
            row += f"{p:8.1f}%" if p <= 100 else f"{'imposible':>9}"
        lines.append(row)
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> None:
    pip = 0.1  # pipSize del broker de Edgar en XAUUSD: 1 pip = $0.10/oz

    print("=" * 72)
    print("ARITMETICA DE COSTES  -  XAUUSD scalping")
    print("=" * 72)
    print("Todo en USD por onza. pipSize asumido = 0.1 (1 pip = $0.10/oz).")
    print()

    print("--- 1. Coste ida y vuelta por escenario de broker ---")
    for name, c in BROKER_SCENARIOS.items():
        print(f"  {name:15} {c.describe(pip)}")
    print()

    print("--- 2. El muro de coste (acierto 55%, R:R 1.5) ---")
    print("Cuanto tiene que valer el objetivo para que la operacion sobreviva")
    print("al coste. 'coste/TP' por encima de ~15% es zona de muerte.")
    print()
    targets = [0.5, 1.0, 2.0, 3.0, 5.0, 8.0, 12.0, 20.0]
    for name in ("ecn_tipico", "estandar", "estandar_malo"):
        print(f"  [{name}]  coste ${BROKER_SCENARIOS[name].round_trip:.2f}/oz")
        print(cost_wall_table(BROKER_SCENARIOS[name], 0.55, 1.5, targets, pip))
        print()

    print("--- 3. Acierto necesario para empatar (broker ecn_tipico) ---")
    print("Incluye el coste. Compara con lo que tu motor logra de verdad:")
    print("el XauusdKalmanModularBot acerto 42.5% en H1 y 38.9% en M5.")
    print()
    print(breakeven_grid(BROKER_SCENARIOS["ecn_tipico"],
                         [1.0, 2.0, 3.0, 5.0, 10.0, 20.0],
                         [1.0, 1.5, 2.0, 3.0]))
    print()

    print("--- 4. Tu configuracion actual, llevada a scalping ---")
    print("'Mayor Riesgo' (H1): 233 operaciones, 42.5% acierto, +0.065 R bruto.")
    print("Si el mismo motor produjera esa estadistica con objetivos de scalper:")
    print()
    for tp in (1.0, 2.0, 5.0, 10.0):
        s = StrategyStats(win_rate=0.425, avg_win=tp, avg_loss=tp / 1.5)
        for name in ("ecn_tipico", "estandar"):
            c = BROKER_SCENARIOS[name]
            lab, why = verdict(s, c)
            print(f"  TP ${tp:5.2f} ({tp/pip:3.0f}p) | {name:12} | "
                  f"neto {s.net_expectancy(c):+7.3f} USD/oz | {lab:14} {why}")
        print()

    print("--- 5. Ventaja bruta anual necesaria solo para empatar ---")
    print("(coste ida y vuelta x numero de operaciones)")
    print()
    print(f"  {'trades/año':>12} " + "".join(f"{n:>16}" for n in ("ecn_tipico", "estandar")))
    print("  " + "-" * 46)
    for n in (250, 500, 1000, 2000, 4000):
        row = f"  {n:12,}"
        for name in ("ecn_tipico", "estandar"):
            row += f"{required_gross_edge(BROKER_SCENARIOS[name], n):15.0f} "
        print(row + " USD/oz")
    print()
    print("Contexto: el rango diario del oro ronda los $30-60/oz. Una ventaja")
    print("bruta anual de $1.000/oz equivale a capturar ~25 dias de rango")
    print("completo al año, solo para pagar al broker.")


if __name__ == "__main__":
    main()
