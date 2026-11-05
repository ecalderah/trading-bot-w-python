"""
Estres del unico resultado que paso las pruebas.

El walk-forward dejo al Donchian con t = 2.25 y DSR = 0.972. Antes de
tratarlo como una ventaja real hay que intentar romperlo. Un hallazgo
positivo merece MAS escrutinio que uno negativo, no menos: es el que te
hace arriesgar dinero.

Cuatro ataques:
  1. Concentracion: ¿lo sostiene un solo año?
  2. Coste: ¿aguanta un broker peor?
  3. Correccion honesta por busqueda: el DSR se calculo con n_trials=1, y
     eso ignora que se eligio Donchian DESPUES de mirar tambien el Kalman,
     y que la rejilla y el instrumento tambien se eligieron.
  4. Sensibilidad al parametro: ¿es un pico aislado o una meseta?
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .costs import BROKER_SCENARIOS
from .metrics import deflated_sharpe
from .walkforward import walk_forward


def ataque_1_concentracion(wf: dict) -> None:
    print("\n--- 1. ¿Lo sostiene un solo año? ---")
    log = wf["log"].copy().sort_values("ret_OOS_pct", ascending=False)
    total = log["ret_OOS_pct"].sum()
    print(f"  Suma de retornos anuales OOS: {total:+.1f}%")
    print("  Los 3 mejores años:")
    for _, r in log.head(3).iterrows():
        print(f"    {int(r['año'])}: {r['ret_OOS_pct']:+6.2f}%  "
              f"({100*r['ret_OOS_pct']/total:.0f}% del total)")
    sin_mejor = total - log.iloc[0]["ret_OOS_pct"]
    print(f"  Sin el mejor año: {sin_mejor:+.1f}%  "
          f"({17} años, {sin_mejor/17:+.2f}%/año)")
    pos = (log["ret_OOS_pct"] > 0).sum()
    print(f"  Años positivos: {pos}/18 ({100*pos/18:.0f}%)")


def ataque_2_coste(oro: pd.DataFrame) -> None:
    print("\n--- 2. ¿Aguanta un broker peor? ---")
    print(f"  {'escenario':16} {'coste':>7} {'trades':>7} {'PF':>6} {'espR':>7} "
          f"{'t':>6} {'ret':>7} {'DSR':>6}")
    for name in ("ecn_optimo", "ecn_tipico", "estandar", "estandar_malo", "asia_o_news"):
        c = BROKER_SCENARIOS[name].round_trip
        wf = walk_forward(oro, c, "donchian")
        if not wf:
            continue
        s, m, d = wf["stats"], wf["metrics"], wf["dsr"]
        print(f"  {name:16} ${c:5.2f} {s.trades:7d} "
              f"{(s.profit_factor or 0):6.2f} {s.expectancy_r:+7.3f} "
              f"{s.t_stat():6.2f} {m['net_return_pct']:+6.1f}% {d['dsr']:6.3f}")


def ataque_3_busqueda(wf: dict) -> None:
    print("\n--- 3. Correccion honesta por busqueda ---")
    print("  El DSR del walk-forward se calculo con n_trials=1: correcto para")
    print("  la eleccion de parametros DENTRO de cada año, pero ignora que")
    print("  tambien se eligieron la estrategia, la rejilla y el instrumento.")
    print()
    r = wf["returns"]
    print(f"  {'n_trials':>10} {'que representa':38} {'DSR':>7}")
    for n, why in [(1, "solo el walk-forward interno"),
                   (2, "+ se comparo con el Kalman"),
                   (5, "+ las 5 estrategias de survival.py"),
                   (20, "+ variantes de rejilla y coste probadas"),
                   (100, "+ todo lo que se probo en el proyecto")]:
        d = deflated_sharpe(r, n_trials=n)
        flag = "  <-- deja de pasar" if d["dsr"] < 0.95 else ""
        print(f"  {n:10d} {why:38} {d['dsr']:7.3f}{flag}")


def ataque_4_meseta(oro: pd.DataFrame, cost: float) -> None:
    print("\n--- 4. ¿Pico aislado o meseta? ---")
    print("  Donchian con lookback FIJO (sin reoptimizar), muestra completa.")
    print("  Una ventaja real es una meseta ancha; un pico solitario es ruido.")
    print()
    print(f"  {'lookback':>9} {'trades':>7} {'acierto':>8} {'PF':>6} {'espR':>7} {'t':>6} {'ret':>8}")
    cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5, atr_stop_mult=1.5,
                         reward_risk=1.5, allow_fractional=True, min_units=0.01)
    for lb in (10, 20, 30, 40, 55, 80, 120, 160, 200):
        sig = strategies.donchian(oro, lb)
        res = run(oro, sig, cfg)
        s = res.stats
        eq = res.equity.to_numpy()
        print(f"  {lb:9d} {s.trades:7d} {s.win_rate*100:7.1f}% "
              f"{(s.profit_factor or 0):6.2f} {s.expectancy_r:+7.3f} "
              f"{s.t_stat():6.2f} {(eq[-1]/eq[0]-1)*100:+7.1f}%")


def main() -> None:
    cost = BROKER_SCENARIOS["ecn_tipico"].round_trip
    oro, _ = data.load_pair(start="2004-01-01")

    print("=" * 88)
    print("ESTRES DEL RESULTADO POSITIVO  -  Donchian, walk-forward")
    print("=" * 88)

    wf = walk_forward(oro, cost, "donchian")
    s = wf["stats"]
    print(f"\nPunto de partida: {s.trades} operaciones OOS, PF "
          f"{s.profit_factor:.2f}, esperanza {s.expectancy_r:+.3f} R, "
          f"t = {s.t_stat():.2f}, DSR = {wf['dsr']['dsr']:.3f}")

    ataque_1_concentracion(wf)
    ataque_3_busqueda(wf)
    ataque_4_meseta(oro, cost)
    ataque_2_coste(oro)

    print("\n" + "=" * 88)
    print("Un hallazgo positivo que no sobrevive a los cuatro ataques no es")
    print("una ventaja: es la mejor de las cosas que probaste.")


if __name__ == "__main__":
    main()
