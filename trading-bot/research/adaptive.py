"""
R:B adaptativo al estado del dia, validado fuera de muestra.

Solo se llega aqui despues de dos pasos previos:

  1. research/volatility.py demostro que la volatilidad SE PREDICE fuera de
     muestra (b cerca de 1, persistencia 0.97-0.999). Condicionar a ella es
     legitimo.
  2. research/conditional.py midio QUE variable predice el recorrido de la
     operacion. Resultado contraintuitivo y importante:

       - vol_ratio (expansion/compresion): MFE PLANO. rho=+0.30.
         La idea intuitiva -"si hay mas volatilidad, sube el objetivo"- NO
         esta respaldada por los datos.
       - efficiency ratio de Kaufman: MFE de 0.63 a 1.16 R, rho=+0.90.
         Y el quintil mas zigzagueante es el UNICO que pierde dinero.

Asi que la variable de estado es la eficiencia del camino, no la volatilidad.
Se prueban dos usos, que son decisiones distintas:

  FILTRO      no entrar cuando el camino reciente es zigzag (Q1 pierde).
  R:B ADAPT.  escalar la distancia del trailing con la eficiencia: camino
              limpio -> deja correr mas; camino sucio -> recoge antes.

Todo se valida con walk-forward: el umbral y la escala se calibran SOLO con
el pasado de cada año. Y se compara contra la linea base con bootstrap, no
con el estadistico t, porque la distribucion esta muy sesgada.

CAUTELA EXPLICITA: cada variante añade parametros. Al final se reporta el
coste de haber probado, igual que en research/sweep.py.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .diversified import coste_relativo
from .exits import bootstrap_p
from .metrics import equity_metrics, sharpe
from .volatility import efficiency_ratio


# ---------------------------------------------------------------------------
# Las variantes
# ---------------------------------------------------------------------------

def escala_trailing(ef: np.ndarray, base: float = 3.0,
                    lo: float = 0.7, hi: float = 1.3,
                    ef_lo: float = 0.20, ef_hi: float = 0.50) -> np.ndarray:
    """Multiplicador del trailing en funcion de la eficiencia del camino.

    Interpolacion lineal simple entre dos anclas, recortada. UN grado de
    libertad efectivo (el ancho lo-hi); las anclas ef_lo/ef_hi salen de los
    quintiles medidos en conditional.py, no se ajustan.

    Camino limpio  -> trailing mas ancho -> se deja correr la tendencia.
    Camino sucio   -> trailing mas corto -> se recoge antes de devolverlo.
    """
    x = np.clip((ef - ef_lo) / (ef_hi - ef_lo), 0.0, 1.0)
    return base * (lo + (hi - lo) * x)


def preparar(df: pd.DataFrame, lookback: int, ef_n: int = 20):
    sig = strategies.donchian(df, lookback)
    ef = efficiency_ratio(df["close"].to_numpy(float), ef_n)
    return sig, ef


def correr_variante(df: pd.DataFrame, lookback: int, base_trail: float,
                    variante: str, ef_umbral: float = 0.20,
                    lo: float = 0.7, hi: float = 1.3):
    sig, ef = preparar(df, lookback)
    tm = None

    if variante in ("filtro", "ambos"):
        sig = sig.copy()
        sig[~np.isfinite(ef) | (ef < ef_umbral)] = 0

    if variante in ("adaptativo", "ambos"):
        tm = escala_trailing(ef, base_trail, lo, hi)

    cfg = BacktestConfig(cost_usd_per_oz=coste_relativo(df), risk_pct=0.5,
                         atr_stop_mult=2.0, exit_mode="trail",
                         trail_atr_mult=base_trail, allow_fractional=True,
                         min_units=1e-9, cooldown_bars=2)
    return run(df, sig, cfg, trail_mult=tm)


def cartera_variante(mercados: dict[str, pd.DataFrame], variante: str,
                     lookback: int = 120, base_trail: float = 3.0,
                     ef_umbral: float = 0.20, lo: float = 0.7,
                     hi: float = 1.3) -> dict:
    curvas, rs = {}, []
    for nombre, df in mercados.items():
        res = correr_variante(df, lookback, base_trail, variante,
                              ef_umbral, lo, hi)
        if len(res.trades) < 5:
            continue
        curvas[nombre] = res.equity.pct_change().fillna(0.0)
        rs.extend(t.r for t in res.trades)
    if not curvas:
        return {}
    port = pd.DataFrame(curvas).fillna(0.0).mean(axis=1)
    eq = (1 + port).cumprod()
    return {"port": port, "equity": eq, "r": rs}


def resumir(nombre: str, c: dict) -> dict:
    eq = c["equity"].to_numpy()
    m = equity_metrics(eq)
    r = c["port"].to_numpy()
    return {
        "variante": nombre, "trades": len(c["r"]),
        "ret": m["net_return_pct"], "maxDD": m["max_dd_pct"],
        "ret_dd": m["ret_over_dd"], "sharpe": sharpe(r),
        "espR": float(np.mean(c["r"])), "p_boot": bootstrap_p(c["r"]),
    }


CAB = (f"  {'variante':22} {'trades':>7} {'retorno':>9} {'maxDD':>7} "
       f"{'ret/DD':>7} {'Sharpe':>7} {'espR':>8} {'p_boot':>9}")


def fila(x: dict, base_sharpe: float | None = None) -> str:
    mark = " *" if x["p_boot"] < 0.05 else "  "
    delta = ""
    if base_sharpe is not None:
        d = x["sharpe"] - base_sharpe
        delta = f"  ({d:+.3f})"
    return (f"  {x['variante']:22} {x['trades']:7d} {x['ret']:+8.1f}% "
            f"{x['maxDD']:6.1f}% {x['ret_dd']:7.2f} {x['sharpe']:7.2f} "
            f"{x['espR']:+8.3f} {x['p_boot']:9.4f}{mark}{delta}")


# ---------------------------------------------------------------------------
# Validacion fuera de muestra
# ---------------------------------------------------------------------------

def walk_forward_variante(mercados: dict[str, pd.DataFrame], variante: str,
                          first_year: int = 2009) -> dict:
    """El umbral de eficiencia se calibra con el pasado de cada año.

    Se usa el percentil 20 de la eficiencia observada HASTA el 31/12 anterior,
    que es exactamente el corte Q1 que conditional.py identifico. No se busca
    el umbral optimo: se toma el que la teoria del analisis previo dicta, y se
    estima con datos pasados. Eso evita añadir un grado de libertad ajustado.
    """
    años = sorted({d.year for df in mercados.values() for d in df.index})
    rets_año: dict[int, list[pd.Series]] = {}
    todos_r: list[float] = []

    for y in años:
        if y < first_year:
            continue
        for nombre, df in mercados.items():
            train = df[df.index.year < y]
            if len(train) < 1000:
                continue
            ef_train = efficiency_ratio(train["close"].to_numpy(float), 20)
            ef_train = ef_train[np.isfinite(ef_train)]
            if len(ef_train) < 200:
                continue
            umbral = float(np.percentile(ef_train, 20))

            ctx = df[df.index.year <= y].tail(600)
            if len(ctx) < 300:
                continue
            res = correr_variante(ctx, 120, 3.0, variante, umbral)
            mask = res.equity.index.year == y
            eq = res.equity[mask]
            if len(eq) < 20:
                continue
            rets_año.setdefault(y, []).append(eq.pct_change().fillna(0.0))
            todos_r.extend(t.r for t in res.trades if t.exit_date.year == y)

    if not rets_año:
        return {}
    piezas = []
    for y in sorted(rets_año):
        piezas.append(pd.DataFrame(
            {i: s for i, s in enumerate(rets_año[y])}).fillna(0.0).mean(axis=1))
    port = pd.concat(piezas)
    eq = (1 + port).cumprod()
    return {"port": port, "equity": eq, "r": todos_r}


def main() -> None:
    print("=" * 104)
    print("R:B ADAPTATIVO AL ESTADO DEL DIA")
    print("=" * 104)
    print("La variable de estado es la EFICIENCIA del camino, no la volatilidad.")
    print("conditional.py midio que el vol_ratio no predice el recorrido (rho=0.30)")
    print("y la eficiencia si (rho=0.90). Se prueba lo que los datos señalan, no")
    print("lo que la intuicion sugiere.\n")

    mercados = data.load_basket(start="2004-01-01")

    # --- 1. en muestra completa ---
    print("--- 1. Muestra completa 2004-2026, 21 mercados ---")
    print(CAB)
    print("  " + "-" * 92)
    base = resumir("base (trailing fijo)", cartera_variante(mercados, "base"))
    print(fila(base))
    for v, etiq in (("filtro", "+ filtro eficiencia"),
                    ("adaptativo", "+ R:B adaptativo"),
                    ("ambos", "+ ambos")):
        print(fila(resumir(etiq, cartera_variante(mercados, v)), base["sharpe"]))

    # --- 2. sensibilidad del ancho de la escala ---
    print("\n--- 2. ¿Cuanto importa el ancho de la escala adaptativa? ---")
    print("  Si el resultado depende de afinar lo/hi, es ajuste, no efecto.")
    print(CAB)
    print("  " + "-" * 92)
    for lo, hi in ((0.9, 1.1), (0.8, 1.2), (0.7, 1.3), (0.5, 1.5), (0.4, 2.0)):
        c = cartera_variante(mercados, "adaptativo", lo=lo, hi=hi)
        print(fila(resumir(f"escala {lo:.1f}-{hi:.1f}", c), base["sharpe"]))

    # --- 3. fuera de muestra ---
    print("\n--- 3. Walk-forward: umbral estimado con el pasado de cada año ---")
    print(CAB)
    print("  " + "-" * 92)
    wf_base = walk_forward_variante(mercados, "base")
    if wf_base:
        b = resumir("base", wf_base)
        print(fila(b))
        for v, etiq in (("filtro", "+ filtro eficiencia"),
                        ("adaptativo", "+ R:B adaptativo"),
                        ("ambos", "+ ambos")):
            wf = walk_forward_variante(mercados, v)
            if wf:
                print(fila(resumir(etiq, wf), b["sharpe"]))

    print("\n" + "=" * 104)
    print("""
EL COSTE DE HABER BUSCADO

  Hasta aqui se han probado: 3 variables de estado x 3 estadisticos
  (conditional.py), 4 variantes y 5 anchos de escala (aqui). Son ~20 pruebas
  mas sobre la misma muestra, encima de las 1.080 del sweep original.

  Por eso la unica cifra que vale es la del bloque 3, y solo si la mejora es
  GRANDE y ESTABLE al mover el ancho de la escala. Una mejora de Sharpe de
  +0.02 que aparece solo con lo=0.7/hi=1.3 es ruido con nombre bonito.
""")


if __name__ == "__main__":
    main()
