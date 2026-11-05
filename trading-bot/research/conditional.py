"""
¿El estado del dia predice cuanto recorre la operacion?

Paso previo obligatorio antes de escribir ninguna regla adaptativa. La
tentacion es programar "si hay mucha volatilidad, sube el objetivo" y
backtestearlo. Eso es empezar por el final: añade parametros a un sistema
cuya ventaja ya es modesta, y el backtest dira que si porque siempre hay
alguna combinacion que funciona.

Aqui NO se cambia la estrategia. Se toman las operaciones que el sistema ya
hace, se anota el estado del mercado el dia de la ENTRADA, y se mira si ese
estado tiene relacion con lo que la operacion acabo haciendo.

  MFE  = cuanto llego a recorrer a favor, en multiplos del riesgo inicial.
         Es el techo que un objetivo podria haber capturado. ES LA VARIABLE
         QUE DECIDE el R:B: si el MFE no depende del estado, adaptar el R:B
         al estado no puede funcionar.
  MAE  = cuanto llego a ir en contra antes de resolverse.

Variables de estado, todas conocidas ANTES de entrar:
  vol_ratio    vol 10d / vol 60d. >1 expansion, <1 compresion.
  effic        ratio de Kaufman a 20d: 1 = linea recta, 0 = zigzag.
  vol_pct      percentil de la vol EWMA en el ultimo año.

Con ~1.700 operaciones sobre 21 mercados hay potencia estadistica de sobra
para responder. Si la relacion no aparece aqui, no existe.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .diversified import coste_relativo
from .volatility import efficiency_ratio, ewma_var, retornos, vol_ratio


def estados(df: pd.DataFrame) -> pd.DataFrame:
    """Variables de estado por barra, todas desplazadas para no ver el futuro."""
    close = df["close"].to_numpy(float)
    r = retornos(df["close"])
    r = np.concatenate([[0.0], r])            # alinear con close

    vr = vol_ratio(r, 10, 60)
    ef = efficiency_ratio(close, 20)

    v = ewma_var(r, 0.94)
    s = pd.Series(np.sqrt(v))
    vp = s.rolling(252).rank(pct=True).to_numpy()

    return pd.DataFrame({"vol_ratio": vr, "effic": ef, "vol_pct": vp},
                        index=df.index)


def recolectar(mercados: dict[str, pd.DataFrame], lookback: int = 120,
               trail: float = 3.0) -> pd.DataFrame:
    """Todas las operaciones de todos los mercados, con su estado de entrada."""
    filas = []
    for nombre, df in mercados.items():
        est = estados(df)
        sig = strategies.donchian(df, lookback)
        cfg = BacktestConfig(cost_usd_per_oz=coste_relativo(df), risk_pct=0.5,
                             atr_stop_mult=2.0, exit_mode="trail",
                             trail_atr_mult=trail, allow_fractional=True,
                             min_units=1e-9, cooldown_bars=2)
        res = run(df, sig, cfg)
        for t in res.trades:
            if t.entry_date not in est.index:
                continue
            e = est.loc[t.entry_date]
            filas.append({
                "mercado": nombre, "dir": t.direction,
                "vol_ratio": e["vol_ratio"], "effic": e["effic"],
                "vol_pct": e["vol_pct"],
                "mfe": t.mfe_r, "mae": t.mae_r, "r": t.r,
                "barras": t.bars_held, "gano": t.net > 0,
            })
    return pd.DataFrame(filas).dropna(subset=["vol_ratio", "effic", "vol_pct"])


def por_quintil(tr: pd.DataFrame, var: str) -> pd.DataFrame:
    """Estadisticos por quintil de la variable de estado."""
    q = pd.qcut(tr[var], 5, labels=[f"Q{i}" for i in range(1, 6)], duplicates="drop")
    g = tr.groupby(q, observed=True)
    out = g.agg(n=("r", "size"), mfe_med=("mfe", "median"),
                mfe_p75=("mfe", lambda s: s.quantile(0.75)),
                mae_med=("mae", "median"), r_med=("r", "median"),
                r_medio=("r", "mean"), acierto=("gano", "mean"),
                barras=("barras", "median"))
    out["rango"] = [f"{tr[var][q == i].min():.2f}-{tr[var][q == i].max():.2f}"
                    for i in out.index]
    return out


def tendencia_monotona(valores: np.ndarray) -> tuple[float, str]:
    """Correlacion de Spearman del estadistico contra el numero de quintil.

    Buscamos una relacion MONOTONA. Un patron en zigzag entre quintiles es
    ruido, aunque algun quintil destaque.
    """
    from scipy.stats import spearmanr
    x = np.arange(len(valores))
    rho, p = spearmanr(x, valores)
    if not np.isfinite(rho):
        return 0.0, "n/d"
    signo = "creciente" if rho > 0 else "decreciente"
    fuerza = "monotona" if abs(rho) >= 0.9 else ("parcial" if abs(rho) >= 0.6 else "sin patron")
    return float(rho), f"{fuerza} {signo}" if fuerza != "sin patron" else "sin patron"


def analizar(tr: pd.DataFrame, var: str, etiqueta: str) -> None:
    t = por_quintil(tr, var)
    print(f"\n--- {etiqueta}  ({var}) ---")
    print(f"  {'':4} {'rango':>14} {'n':>6} {'MFE med':>9} {'MFE p75':>9} "
          f"{'MAE med':>9} {'R medio':>9} {'acierto':>8} {'barras':>7}")
    for idx, row in t.iterrows():
        print(f"  {idx:4} {row['rango']:>14} {int(row['n']):6d} "
              f"{row['mfe_med']:9.2f} {row['mfe_p75']:9.2f} {row['mae_med']:9.2f} "
              f"{row['r_medio']:+9.3f} {row['acierto']:7.1%} {row['barras']:7.0f}")

    for col, nom in (("mfe_med", "MFE mediana"), ("r_medio", "R medio"),
                     ("acierto", "acierto")):
        rho, desc = tendencia_monotona(t[col].to_numpy())
        marca = "  <-- relacion util" if abs(rho) >= 0.9 else ""
        print(f"    {nom:12} rho={rho:+.2f}  {desc}{marca}")


def main() -> None:
    print("=" * 96)
    print("¿EL ESTADO DEL DIA PREDICE EL RECORRIDO DE LA OPERACION?")
    print("=" * 96)
    print("Donchian(120)+trailing sobre 21 futuros. No se cambia la estrategia:")
    print("se anota el estado el dia de la entrada y se mira que paso despues.")
    print("MFE = recorrido maximo a favor, en R. Es el techo que un objetivo")
    print("podria haber capturado, y por tanto lo que decide si adaptar el R:B")
    print("puede funcionar.\n")

    mercados = data.load_basket(start="2004-01-01")
    tr = recolectar(mercados)
    print(f"Operaciones recogidas: {len(tr)} sobre {tr['mercado'].nunique()} mercados")
    print(f"MFE mediana global: {tr['mfe'].median():.2f} R | "
          f"MAE mediana: {tr['mae'].median():.2f} R | "
          f"acierto: {tr['gano'].mean():.1%}")

    analizar(tr, "vol_ratio", "EXPANSION vs COMPRESION de volatilidad")
    analizar(tr, "effic", "EFICIENCIA del camino (Kaufman 20d)")
    analizar(tr, "vol_pct", "NIVEL de volatilidad (percentil a 1 año)")

    print("\n" + "=" * 96)
    print("""
COMO LEER ESTO

  Para que adaptar el R:B tenga sentido hace falta que el MFE cambie de forma
  MONOTONA con la variable de estado (|rho| >= 0.9). Si el MFE es plano entre
  quintiles, el recorrido de la operacion no depende del estado y cualquier
  regla adaptativa estaria ajustando ruido.

  Ojo con confundir dos cosas:
    - que el R medio cambie entre quintiles -> filtrar entradas
    - que el MFE cambie entre quintiles     -> adaptar el objetivo
  Son decisiones distintas y la segunda es la que se pidio.
""")


if __name__ == "__main__":
    main()
