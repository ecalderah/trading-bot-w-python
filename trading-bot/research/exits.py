"""
CORRECCION: el modo de salida estaba invalidando las pruebas 2-5.

El motor de backtest original cerraba SIEMPRE con un objetivo fijo en
multiplos del riesgo (1.5R por defecto). Para un seguidor de tendencia eso
es un error de diseño, no un parametro: toda la esperanza de un trend
follower vive en la cola derecha -unas pocas ganadoras de +10R o +50R que
pagan muchas perdidas de -1R- y un objetivo fijo la amputa por construccion.

El analisis de errores lo destapo: en las tres estrategias, el 0% de las
perdedoras alcanzaba +1.5R... porque a +1.5R dejaban de ser perdedoras. Las
ganadoras duraban 8 barras. Eso no es seguir una tendencia.

Este modulo compara los tres modos de salida sobre las mismas señales, para
saber cuanto de las conclusiones anteriores era del motor y cuanto de la
estrategia.

Aviso estadistico importante: con salida "signal" la distribucion de R queda
enormemente sesgada a la derecha (asimetria > 4). El estadistico t asume
normalidad aproximada y ahi deja de ser fiable: subestima la significancia.
Por eso se reporta tambien la asimetria y el bootstrap.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .costs import BROKER_SCENARIOS

MODOS = [
    ("tp 1.5R",        dict(exit_mode="tp", reward_risk=1.5)),
    ("tp 3R",          dict(exit_mode="tp", reward_risk=3.0)),
    ("trailing 3xATR", dict(exit_mode="trail", trail_atr_mult=3.0)),
    ("trailing 5xATR", dict(exit_mode="trail", trail_atr_mult=5.0)),
    ("solo señal",     dict(exit_mode="signal")),
]


def bootstrap_p(r_multiples: list[float], n_iter: int = 20000,
                seed: int = 7) -> float:
    """P(esperanza <= 0) por remuestreo. No asume normalidad.

    Con distribuciones muy sesgadas el t de Student no vale; el bootstrap
    sobre la media si, porque no supone nada sobre la forma.
    """
    r = np.asarray(r_multiples, dtype=float)
    n = len(r)
    if n < 5:
        return 1.0
    rng = np.random.default_rng(seed)

    # Por lotes: la matriz (n_iter x n) completa son cientos de MB en cuanto
    # la cartera junta unos miles de operaciones.
    lote = max(1, min(n_iter, 4_000_000 // max(n, 1)))
    malas = 0
    hechas = 0
    while hechas < n_iter:
        k = min(lote, n_iter - hechas)
        idx = rng.integers(0, n, size=(k, n))
        malas += int((r[idx].mean(axis=1) <= 0).sum())
        hechas += k
    return malas / n_iter


def evaluar(df: pd.DataFrame, sig: np.ndarray, cost: float) -> pd.DataFrame:
    filas = []
    for label, kw in MODOS:
        cfg = BacktestConfig(cost_usd_per_oz=cost, risk_pct=0.5,
                             atr_stop_mult=2.0, allow_fractional=True,
                             min_units=0.01, **kw)
        res = run(df, sig, cfg)
        s = res.stats
        if s.trades == 0:
            continue
        eq = res.equity.to_numpy()
        peak = np.maximum.accumulate(eq)
        dd = float(((peak - eq) / peak).max())
        rs = np.asarray(s.r_multiples)
        skew = float(((rs - rs.mean()) ** 3).mean() / rs.std() ** 3) if rs.std() > 0 else 0.0
        filas.append({
            "modo": label,
            "trades": s.trades,
            "acierto": s.win_rate * 100,
            "PF": s.profit_factor or np.inf,
            "espR": s.expectancy_r,
            "t": s.t_stat(),
            "p_boot": bootstrap_p(s.r_multiples),
            "asim": skew,
            "maxR": float(rs.max()),
            "ret": (eq[-1] / eq[0] - 1) * 100,
            "maxDD": dd * 100,
        })
    return pd.DataFrame(filas)


def tabla(nombre: str, df: pd.DataFrame) -> None:
    print(f"\n  {nombre}")
    print(f"  {'modo':16} {'n':>5} {'acierto':>8} {'PF':>7} {'espR':>8} "
          f"{'t':>6} {'p_boot':>8} {'asim':>6} {'maxR':>7} {'ret':>9} {'maxDD':>7}")
    print("  " + "-" * 96)
    for _, r in df.iterrows():
        pf = "  inf" if not np.isfinite(r["PF"]) else f"{r['PF']:7.2f}"
        marca = " *" if r["p_boot"] < 0.05 else "  "
        print(f"  {r['modo']:16} {int(r['trades']):5d} {r['acierto']:7.1f}% {pf} "
              f"{r['espR']:+8.3f} {r['t']:6.2f} {r['p_boot']:8.4f}{marca}"
              f"{r['asim']:5.1f} {r['maxR']:7.1f} {r['ret']:+8.1f}% {r['maxDD']:6.1f}%")


def main() -> None:
    cost = BROKER_SCENARIOS["ecn_tipico"].round_trip
    oro, plata = data.load_pair(start="2004-01-01")

    print("=" * 100)
    print("EFECTO DEL MODO DE SALIDA  -  oro diario 2004-2026")
    print("=" * 100)
    print("Las mismas señales, tres formas de salir. 'p_boot' es P(esperanza <= 0)")
    print("por remuestreo, sin suponer normalidad; * marca p < 0.05.")

    for nombre, sig in (
        ("KALMAN (el motor actual)", strategies.kalman_trend(oro)),
        ("DONCHIAN 20", strategies.donchian(oro, 20)),
        ("DONCHIAN 120", strategies.donchian(oro, 120)),
        ("EMA 20/100", strategies.ema_cross(oro, 20, 100)),
    ):
        tabla(nombre, evaluar(oro, sig, cost))

    print("\n" + "=" * 100)
    print("""
QUE MIRAR

  - Compara 'tp 1.5R' con 'trailing 3xATR' en la MISMA fila de estrategia.
    La diferencia es puramente del motor de salida, no de la señal.
  - 'maxR' dice si la cola derecha existe. Con objetivo fijo vale exactamente
    el RR configurado: el motor la estaba recortando.
  - 'asim' (asimetria) por encima de 3-4 es la firma de seguir tendencias.
    Ahi el estadistico t deja de ser fiable y manda 'p_boot'.
  - Si el Kalman NO mejora con trailing y el Donchian SI, la conclusion de
    que el Kalman no tiene ventaja se mantiene. Si mejoran los dos, parte de
    las pruebas 2-5 hay que rehacerlas.
""")


if __name__ == "__main__":
    main()
