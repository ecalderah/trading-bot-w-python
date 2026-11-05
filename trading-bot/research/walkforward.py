"""
Walk-forward anclado: la prueba acida.

Responde a la objecion legitima al barrido en muestra: "yo no usaria una
configuracion fija, la reoptimizaria periodicamente".

Metodo, sin trampa posible:
  - Se optimiza usando SOLO datos hasta el 31/12 del año T.
  - Se opera el año T+1 con esa configuracion, congelada.
  - Se avanza un año y se repite.
  - Se encadenan los retornos FUERA DE MUESTRA y se juzgan esos.

Nada de lo que decide una operacion se conoce en el momento de decidirla.
Es lo mas parecido a haber operado el sistema en vivo durante 18 años.
"""

from __future__ import annotations

import itertools
import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .costs import BROKER_SCENARIOS
from .metrics import TradeStats, deflated_sharpe, equity_metrics

# La rejilla se parte en dos: los parametros que cambian la SEÑAL y los que
# solo cambian la EJECUCION. La señal se calcula una vez por combinacion de
# los primeros y se reutiliza para todas las de los segundos: 27 filtros de
# Kalman en vez de 243.
SIGNAL_GRID = {
    "slope_var":   [1e-7, 1e-6, 1e-5],
    "z_threshold": [0.75, 1.0, 1.5],
    "z_window":    [60, 120, 200],
}
EXEC_GRID = {
    "atr_stop": [1.5, 2.0, 3.0],
    "rr":       [1.0, 1.5, 2.5],
}
# Salida por defecto. El analisis de errores (research/errors.py) mostro que
# un objetivo fijo amputa la cola derecha, que es de donde vive la esperanza
# de un seguidor de tendencia. Ver research/exits.py.
EXIT_KW: dict = {"exit_mode": "tp"}
GRID = {**SIGNAL_GRID, **EXEC_GRID}


def _signal(df, p, kind):
    if kind == "kalman":
        return strategies.kalman_trend(
            df, slope_var=p["slope_var"], z_threshold=p["z_threshold"],
            z_window=p["z_window"], warmup=max(300, p["z_window"] + 50))
    if kind == "donchian":
        return strategies.donchian(df, int(p["z_window"]))
    raise ValueError(kind)


def optimise(df: pd.DataFrame, cost: float, kind: str) -> dict:
    """Mejor configuracion por Sharpe sobre el tramo dado. Solo ve el pasado."""
    skeys, ekeys = list(SIGNAL_GRID), list(EXEC_GRID)
    best, best_sharpe = None, -np.inf

    for scombo in itertools.product(*(SIGNAL_GRID[k] for k in skeys)):
        sp = dict(zip(skeys, scombo))
        sig = _signal(df, sp, kind)          # se calcula UNA vez
        if not sig.any():
            continue

        for ecombo in itertools.product(*(EXEC_GRID[k] for k in ekeys)):
            ep = dict(zip(ekeys, ecombo))
            cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5,
                                 atr_stop_mult=ep["atr_stop"], reward_risk=ep["rr"],
                                 allow_fractional=True, min_units=0.01, **EXIT_KW)
            res = run(df, sig, cfg)
            if res.stats.trades < 15:
                continue
            eq = res.equity.to_numpy()
            r = np.diff(eq) / eq[:-1]
            sd = r.std(ddof=1)
            if sd <= 0:
                continue
            sh = r.mean() / sd * np.sqrt(252)
            if sh > best_sharpe:
                best_sharpe, best = sh, {**sp, **ep, "in_sample_sharpe": sh}
    return best


def walk_forward(oro: pd.DataFrame, cost: float, kind: str,
                 first_test_year: int = 2009, min_train_years: int = 5) -> dict:
    years = sorted({d.year for d in oro.index})
    oos_returns: list[np.ndarray] = []
    oos_stats = TradeStats()
    log = []

    for y in years:
        if y < first_test_year:
            continue
        train = oro[oro.index.year < y]
        test = oro[oro.index.year == y]
        if len(train) < min_train_years * 200 or len(test) < 100:
            continue

        p = optimise(train, cost, kind)
        if p is None:
            continue

        # el warm-up del filtro necesita historia previa: se le da el tramo
        # de entrenamiento por delante, pero solo se contabiliza el año test
        ctx = oro[oro.index.year <= y].tail(len(test) + 400)
        sig = _signal(ctx, p, kind)
        cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5,
                             atr_stop_mult=p["atr_stop"], reward_risk=p["rr"],
                             allow_fractional=True, min_units=0.01, **EXIT_KW)
        res = run(ctx, sig, cfg)

        mask = res.equity.index.year == y
        eq = res.equity[mask].to_numpy()
        if len(eq) < 20:
            continue
        r = np.diff(eq) / eq[:-1]
        oos_returns.append(r)

        yr_trades = [t for t in res.trades if t.exit_date.year == y]
        for t in yr_trades:
            oos_stats.add(t.net, t.r, t.cost)

        log.append({
            "año": y,
            "z": p["z_threshold"], "ventana": p["z_window"],
            "SL": p["atr_stop"], "RR": p["rr"],
            "sharpe_IS": p["in_sample_sharpe"],
            "trades_OOS": len(yr_trades),
            "ret_OOS_pct": (eq[-1] / eq[0] - 1) * 100,
        })

    if not oos_returns:
        return {}

    all_r = np.concatenate(oos_returns)
    curve = np.cumprod(1 + all_r)
    m = equity_metrics(curve)
    d = deflated_sharpe(all_r, n_trials=1)   # OOS: no hubo seleccion aqui
    return {"log": pd.DataFrame(log), "stats": oos_stats,
            "returns": all_r, "metrics": m, "dsr": d}


def report(name: str, wf: dict) -> None:
    if not wf:
        print(f"\n{name}: sin resultados.")
        return
    print(f"\n{'=' * 96}")
    print(f"WALK-FORWARD  -  {name}")
    print("=" * 96)
    print(wf["log"].to_string(index=False, float_format=lambda x: f"{x:.3g}"))

    s, m, d = wf["stats"], wf["metrics"], wf["dsr"]
    pos = (wf["log"]["ret_OOS_pct"] > 0).sum()
    tot = len(wf["log"])
    print(f"\n  Años fuera de muestra ....... {tot}   (positivos: {pos}, "
          f"{100*pos/tot:.0f}%)")
    print(f"  Operaciones OOS ............. {s.trades}")
    print(f"  Acierto ..................... {s.win_rate*100:.1f}%")
    pf = s.profit_factor
    print(f"  Profit factor ............... {pf:.2f}" if pf else "  Profit factor ............... n/d")
    print(f"  Esperanza ................... {s.expectancy_r:+.3f} R")
    print(f"  t de la esperanza ........... {s.t_stat():.2f}"
          f"   {'(significativa)' if abs(s.t_stat()) > 2 else '(NO significativa)'}")
    print(f"  Retorno acumulado OOS ....... {m['net_return_pct']:+.1f}%")
    print(f"  Drawdown maximo ............. {m['max_dd_pct']:.1f}%")
    print(f"  Sharpe OOS .................. {d['sr']:.3f}")
    print(f"  DSR ......................... {d['dsr']:.3f}")


def main() -> None:
    cost = BROKER_SCENARIOS["ecn_tipico"].round_trip
    oro, _ = data.load_pair(start="2004-01-01")

    print("=" * 96)
    print("WALK-FORWARD ANCLADO  -  oro diario, reoptimizando cada año")
    print("=" * 96)
    print("Se optimiza con datos hasta el 31/12 del año anterior y se opera el")
    print("año siguiente con la configuracion congelada. Solo se juzgan los")
    print(f"retornos fuera de muestra. Coste ${cost:.2f}/oz.")
    print(f"Rejilla de optimizacion: "
          f"{int(np.prod([len(v) for v in GRID.values()]))} combinaciones por año.")

    for kind, label in (("kalman", "motor Kalman"), ("donchian", "Donchian (1 parametro)")):
        report(label, walk_forward(oro, cost, kind))


if __name__ == "__main__":
    main()
