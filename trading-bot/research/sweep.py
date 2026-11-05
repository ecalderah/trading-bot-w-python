"""
La prueba JUSTA.

Objecion legitima a survival.py: los parametros del Kalman estan calibrados
para H1, y ahi se corrieron sobre barras diarias. Este script le da al motor
su mejor oportunidad: barre el espacio de parametros en diario, se queda con
el mejor, y DESPUES le cobra el precio de haber buscado.

Ese ultimo paso es el que casi nadie hace y el que decide. Si pruebas 200
configuraciones sobre la misma muestra, el mejor Sharpe esta inflado aunque
no exista ninguna ventaja. El Deflated Sharpe Ratio descuenta exactamente
ese sesgo usando el numero real de pruebas.
"""

from __future__ import annotations

import itertools
import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .costs import BROKER_SCENARIOS
from .metrics import deflated_sharpe


GRID = {
    "slope_var":   [1e-8, 1e-7, 1e-6, 1e-5],
    "obs_var":     [1e-4, 1e-3],
    "z_threshold": [0.5, 0.75, 1.0, 1.5, 2.0],
    "z_window":    [60, 120, 200],
    "atr_stop":    [1.5, 2.0, 3.0],
    "rr":          [1.0, 1.5, 2.5],
}


def sweep(df: pd.DataFrame, cost: float, verbose: bool = True) -> pd.DataFrame:
    keys = list(GRID)
    combos = list(itertools.product(*(GRID[k] for k in keys)))
    rows = []

    for n, combo in enumerate(combos, 1):
        p = dict(zip(keys, combo))
        sig = strategies.kalman_trend(
            df, slope_var=p["slope_var"], obs_var=p["obs_var"],
            z_threshold=p["z_threshold"], z_window=p["z_window"],
            warmup=max(300, p["z_window"] + 50),
        )
        cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5,
                             atr_stop_mult=p["atr_stop"], reward_risk=p["rr"],
                             allow_fractional=True, min_units=0.01)
        res = run(df, sig, cfg)
        if res.stats.trades < 20:
            continue
        eq = res.equity.to_numpy()
        rets = np.diff(eq) / eq[:-1]
        rows.append({
            **p,
            "trades": res.stats.trades,
            "win_rate": res.stats.win_rate * 100,
            "pf": res.stats.profit_factor or 0.0,
            "exp_r": res.stats.expectancy_r,
            "t": res.stats.t_stat(),
            "ret_pct": (eq[-1] / eq[0] - 1) * 100,
            "sharpe": rets.mean() / rets.std(ddof=1) * np.sqrt(252) if rets.std(ddof=1) > 0 else 0.0,
            "_rets": rets,
        })
        if verbose and n % 60 == 0:
            print(f"  ... {n}/{len(combos)} combinaciones", flush=True)

    return pd.DataFrame(rows)


def main() -> None:
    cost = BROKER_SCENARIOS["ecn_tipico"].round_trip
    oro, plata = data.load_pair(start="2004-01-01")

    print("=" * 100)
    print("BARRIDO DE PARAMETROS + CORRECCION POR BUSQUEDA")
    print("=" * 100)
    n_total = int(np.prod([len(v) for v in GRID.values()]))
    print(f"Rejilla: {n_total} combinaciones sobre oro diario 2004-2026.")
    print(f"Coste ${cost:.2f}/oz. Se descartan las que dan menos de 20 operaciones.\n")

    df = sweep(oro, cost)
    if df.empty:
        print("Ninguna combinacion produjo operaciones suficientes.")
        return

    n_trials = len(df)
    print(f"\n{n_trials} combinaciones validas.\n")

    df = df.sort_values("sharpe", ascending=False).reset_index(drop=True)

    # dispersion REAL de los Sharpe entre pruebas: es el input honesto del DSR
    trial_std = float(df["sharpe"].std(ddof=1) / np.sqrt(252))

    print("--- Top 10 por Sharpe (en muestra) ---")
    cols = ["slope_var", "obs_var", "z_threshold", "z_window", "atr_stop", "rr",
            "trades", "win_rate", "pf", "exp_r", "t", "ret_pct", "sharpe"]
    print(df[cols].head(10).to_string(index=False,
          float_format=lambda x: f"{x:.3g}"))

    best = df.iloc[0]
    d = deflated_sharpe(best["_rets"], n_trials=n_trials,
                        trial_sharpe_std=trial_std)

    print("\n" + "=" * 100)
    print("EL PRECIO DE HABER BUSCADO")
    print("=" * 100)
    print(f"Mejor configuracion encontrada:")
    print(f"  slope_var={best['slope_var']:g}  obs_var={best['obs_var']:g}  "
          f"z={best['z_threshold']}  ventana={best['z_window']:g}  "
          f"SL={best['atr_stop']}xATR  RR={best['rr']}")
    print(f"  {best['trades']:.0f} operaciones | acierto {best['win_rate']:.1f}% | "
          f"PF {best['pf']:.2f} | esperanza {best['exp_r']:.3f} R | t = {best['t']:.2f}")
    print()
    print(f"  Sharpe observado ............ {d['sr']:.3f}")
    print(f"  Sharpe esperado por AZAR .... {d['sr0']:.3f}   "
          f"(el mejor de {n_trials} pruebas sin ninguna ventaja)")
    print(f"  Deflated Sharpe Ratio ....... {d['dsr']:.3f}")
    print()
    if d["dsr"] > 0.95:
        print("  VEREDICTO: la ventaja SOBREVIVE a la correccion por busqueda.")
    elif d["dsr"] > 0.5:
        print("  VEREDICTO: NO sobrevive. El resultado es compatible con haber")
        print("  elegido la mejor de N configuraciones sin ventaja real.")
    else:
        print("  VEREDICTO: el mejor resultado de la rejilla es PEOR que lo que")
        print("  cabria esperar del azar con este numero de pruebas.")

    # cuantas combinaciones son siquiera significativas por su cuenta
    sig = (df["t"].abs() > 2).sum()
    print(f"\n  Combinaciones con |t| > 2 (significativas por si solas): "
          f"{sig} de {n_trials} ({100*sig/n_trials:.1f}%)")
    print(f"  Por puro azar se esperarian ~5%: {int(0.05*n_trials)}")
    print(f"  Mediana del t en toda la rejilla: {df['t'].median():.2f}")
    print(f"  Mediana del retorno: {df['ret_pct'].median():.1f}%  "
          f"| Mediana del PF: {df['pf'].median():.2f}")


if __name__ == "__main__":
    main()
