"""
PRUEBA DE SUPERVIVENCIA.

Pregunta unica: el motor de tendencia sobre oro, ¿tiene ventaja, o solo
capturo el tramo alcista 2023-2026 en el que se midio?

Metodo: correr el mismo motor, sin cambiar un parametro, sobre 22 años de
oro partidos por regimen, y compararlo con tres varas de medir tontas.

Lo que NO responde: nada sobre scalping intradia. Eso necesita datos de
tick de cTrader (ver research/costs.py para la parte que si se puede
decidir sin datos).
"""

from __future__ import annotations

import sys
import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .costs import BROKER_SCENARIOS


def _fmt(v, spec="7.2f", none="   n/d"):
    return none if v is None or (isinstance(v, float) and not np.isfinite(v)) else f"{v:{spec}}"


def build_strategies(oro: pd.DataFrame, plata: pd.DataFrame) -> dict:
    return {
        "kalman":        lambda d, r: strategies.kalman_trend(d),
        "kalman+hedge":  lambda d, r: strategies.kalman_trend_with_hedge_veto(d, r),
        "donchian(20)":  lambda d, r: strategies.donchian(d, 20),
        "ema(20,100)":   lambda d, r: strategies.ema_cross(d, 20, 100),
        "buy&hold":      lambda d, r: strategies.buy_and_hold(d),
    }


def table(rows: list[dict], title: str) -> None:
    print(f"\n{title}")
    print("-" * 108)
    print(f"{'estrategia':16} {'trades':>7} {'acierto':>8} {'PF':>7} {'espR':>7} "
          f"{'t':>6} {'retorno':>9} {'maxDD':>8} {'ret/DD':>7} {'Sharpe':>7} {'DSR':>6}")
    print("-" * 108)
    for r in rows:
        print(f"{r['name']:16} {r['trades']:7d} {r['win_rate']:7.1f}% "
              f"{_fmt(r['profit_factor'])} {r['expectancy_r']:7.3f} "
              f"{r['t_stat']:6.2f} {r['net_return_pct']:8.1f}% "
              f"{r['max_dd_pct']:7.1f}% {_fmt(r['ret_over_dd'])} "
              f"{r['sharpe']:7.2f} {r['dsr']:6.2f}")


def main() -> None:
    cost = BROKER_SCENARIOS["ecn_tipico"].round_trip

    print("=" * 108)
    print("PRUEBA DE SUPERVIVENCIA  -  motor de tendencia sobre oro, 2004-2026 (diario)")
    print("=" * 108)
    print(f"Coste ida y vuelta aplicado: ${cost:.2f}/oz (escenario 'ecn_tipico').")
    print("Entrada en la apertura de la barra SIGUIENTE. Si stop y objetivo caen")
    print("en la misma barra, se asume el STOP. Equity marcado a mercado.")
    print("DSR calculado con n_trials=5 (las 5 estrategias probadas aqui).")

    oro, plata = data.load_pair(start="2004-01-01")
    cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5,
                         atr_stop_mult=2.0, reward_risk=1.5,
                         allow_fractional=True, min_units=0.01)

    strats = build_strategies(oro, plata)

    # --- muestra completa ---
    rows = []
    cache = {}
    for name, fn in strats.items():
        sig = fn(oro, plata)
        cache[name] = sig
        res = run(oro, sig, cfg)
        rows.append({"name": name, **res.summary(n_trials=len(strats))})
    table(rows, "MUESTRA COMPLETA  2004-2026")

    # --- por regimen ---
    for rname, sl, note in data.regime_slices(oro):
        ref = plata.loc[sl.index]
        rows = []
        for name, fn in strats.items():
            sig = fn(sl, ref)
            res = run(sl, sig, cfg)
            rows.append({"name": name, **res.summary(n_trials=len(strats))})
        table(rows, f"REGIMEN  {rname}   ({note})")

    # --- veredicto ---
    print("\n" + "=" * 108)
    print("LECTURA")
    print("=" * 108)
    print("""
Que mirar, en este orden:

  1. La columna 't'. Es el estadistico de la esperanza en R. Con |t| < 2 la
     ventaja es indistinguible de cero por muchos que sean los trades.
  2. La columna 'DSR'. Por debajo de 0.95 el resultado no sobrevive a haber
     elegido lo mejor de varias pruebas.
  3. Compara cada estrategia con 'buy&hold' EN CADA REGIMEN. Si el Kalman
     solo gana en los tramos alcistas y pierde en 2011-2013 y 2014-2018,
     entonces lo que mide el backtest de 2023-2026 es el oro, no el motor.
  4. Compara 'kalman' con 'donchian(20)'. Si un sistema de un parametro
     empata, la maquinaria de espacio de estados no se esta ganando el sitio.
""")


if __name__ == "__main__":
    sys.exit(main())
