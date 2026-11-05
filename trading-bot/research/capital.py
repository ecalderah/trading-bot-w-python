"""
¿Con cuanto capital funciona esto?

Es la restriccion que mato al bot original. Su FIX 21 lo documenta: con
$1.000 al 0.5% de riesgo, el 0% de las señales era operable, porque el
volumen minimo del broker exigia arriesgar mas de lo permitido.

La cartera de la prueba 10 necesita ~21 mercados abiertos a la vez, cada uno
arriesgando 0.77% del capital total. La pregunta es cuanto capital hace falta
para que eso sea posible, y que pasa si no lo tienes.

Se calcula, por mercado:

    riesgo en dolares de UN contrato = 2 * ATR_mediana * multiplicador
    capital minimo = ese riesgo / 0.0077

El mercado mas caro fija el minimo para operar la cartera completa. Con menos
capital se opera un subconjunto, y ahi entra el coste real: menos mercados es
menos diversificacion, y la diversificacion era la unica comida gratis del
proyecto.

TRES VEHICULOS, tres respuestas muy distintas:
  - Futuros completos: el minimo es alto.
  - Micro futuros: ~1/10 del tamaño. Cambia el problema por completo.
  - CFDs (cTrader): tamaño fraccionado, el minimo baja mucho mas, a cambio
    de peor coste y riesgo de contrapartida.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from . import data, strategies
from .backtest import BacktestConfig, run
from .diversified import coste_relativo
from .kalman import atr as atr_ema
from .metrics import equity_metrics, sharpe

# Multiplicador: cuantos USD mueve 1 punto del precio que cotiza yfinance.
# Margen inicial aproximado por contrato (CME, orientativo, varia con la vol).
CONTRATOS = {
    #                mult      margen   micro?  mult_micro
    "oro":         (100.0,     12_000,  "MGC",  10.0),
    "plata":       (5_000.0,   16_000,  "SIL",  1_000.0),
    "cobre":       (25_000.0,   9_000,  "MHG",  2_500.0),
    "platino":     (50.0,       4_000,  None,   None),
    "crudo":       (1_000.0,    7_000,  "MCL",  100.0),
    "gas_nat":     (10_000.0,   5_000,  "QG",   2_500.0),
    "gasolina":    (42_000.0,   9_000,  None,   None),
    "maiz":        (50.0,       2_000,  "XC",   10.0),
    "trigo":       (50.0,       2_500,  "XW",   10.0),
    "soja":        (50.0,       3_500,  "XK",   10.0),
    "azucar":      (1_120.0,    1_500,  None,   None),
    "cafe":        (375.0,      8_000,  None,   None),
    "algodon":     (500.0,      3_500,  None,   None),
    "sp500":       (50.0,      15_000,  "MES",  5.0),
    "nasdaq":      (20.0,      20_000,  "MNQ",  2.0),
    "nikkei":      (5.0,        8_000,  None,   None),
    "bono10":      (1_000.0,    2_000,  None,   None),
    "bono30":      (1_000.0,    4_000,  None,   None),
    "eurusd":      (125_000.0,  3_000,  "M6E",  12_500.0),
    # 6J cotiza USD por JPY (~0.0064). El contrato son 12.5M JPY, asi que un
    # punto entero son 12.500.000 USD. Ponerlo en 125.000 daba un riesgo de
    # 19$ por contrato: absurdo, y colaba al yen como "el mercado barato".
    "yen":         (12_500_000.0, 3_500, "M6J", 1_250_000.0),
    "dxy":         (1_000.0,    2_500,  None,   None),
}

RIESGO_OP = 0.0077       # 0.77% del capital, el perfil medio Kelly
ATR_MULT = 2.0


def riesgo_por_contrato(df: pd.DataFrame, mult: float) -> float:
    """USD arriesgados con UN contrato y un stop de 2xATR (mediana historica)."""
    a = atr_ema(df["high"].to_numpy(float), df["low"].to_numpy(float),
                df["close"].to_numpy(float), 14)
    a = a[np.isfinite(a)]
    if len(a) == 0:
        return np.nan
    return float(np.median(a) * ATR_MULT * mult)


def tabla_capital(mercados: dict[str, pd.DataFrame], micro: bool = False) -> pd.DataFrame:
    filas = []
    for nombre, df in mercados.items():
        if nombre not in CONTRATOS:
            continue
        mult, margen, nom_micro, mult_micro = CONTRATOS[nombre]
        if micro:
            if mult_micro is None:
                continue
            mult, margen = mult_micro, margen * (mult_micro / CONTRATOS[nombre][0])
            etiqueta = nom_micro
        else:
            etiqueta = nombre
        r = riesgo_por_contrato(df, mult)
        if not np.isfinite(r) or r <= 0:
            continue
        filas.append({
            "mercado": nombre, "contrato": etiqueta,
            "riesgo_1_contrato": r,
            "capital_min": r / RIESGO_OP,
            "margen": margen,
        })
    return pd.DataFrame(filas).sort_values("capital_min")


def sharpe_por_subconjunto(mercados: dict[str, pd.DataFrame],
                           orden: list[str], lookback: int = 252) -> list[dict]:
    """Sharpe de la cartera segun cuantos mercados se pueden permitir.

    El orden es el de capital minimo creciente: con poco dinero solo entran
    los mercados baratos, que no son necesariamente los mejores.
    """
    curvas: dict[str, pd.Series] = {}
    for nombre in orden:
        df = mercados[nombre]
        sig = strategies.tsmom(df, lookback)
        cfg = BacktestConfig(cost_usd_per_oz=coste_relativo(df), risk_pct=0.5,
                             atr_stop_mult=ATR_MULT, exit_mode="trail",
                             trail_atr_mult=3.0, allow_fractional=True,
                             min_units=1e-9, cooldown_bars=2)
        res = run(df, sig, cfg)
        if len(res.trades) >= 5:
            curvas[nombre] = res.equity.pct_change().fillna(0.0)

    out = []
    activos = [m for m in orden if m in curvas]
    for n in range(1, len(activos) + 1):
        sub = pd.DataFrame({m: curvas[m] for m in activos[:n]}).fillna(0.0)
        port = sub.mean(axis=1).to_numpy()
        eq = np.cumprod(1 + port)
        out.append({"n": n, "ultimo": activos[n - 1], "sharpe": sharpe(port),
                    "maxDD_1x": equity_metrics(eq)["max_dd_pct"]})
    return out


SHARPE_OOS_COMPLETA = 0.591   # walk-forward 17 años, prueba 10
"""Sharpe fuera de muestra de la cartera completa.

Las Sharpe que calcula sharpe_por_subconjunto() son EN MUESTRA sobre los 22
años, y salen bastante mas altas. Usarlas para estimar CAGR seria presentar
como alcanzable un numero que solo existe mirando hacia atras. Se aplica un
descuento proporcional: sharpe_oos ~= sharpe_muestra * (0.591 / sharpe_completa).
Es una aproximacion, pero es la direccion correcta del error.
"""


def main() -> None:
    print("=" * 96)
    print("¿CON CUANTO CAPITAL FUNCIONA?")
    print("=" * 96)
    print(f"Perfil medio Kelly: {RIESGO_OP:.2%} del capital por operacion, stop 2xATR.")
    print("Capital minimo por mercado = riesgo de 1 contrato / 0.77%.\n")

    mercados = data.load_basket(start="2004-01-01")

    for micro, titulo in ((False, "FUTUROS COMPLETOS"), (True, "MICRO FUTUROS")):
        t = tabla_capital(mercados, micro)
        if t.empty:
            continue
        print(f"--- {titulo} ---")
        print(f"  {'mercado':12} {'contrato':10} {'riesgo 1 cto':>13} "
              f"{'capital min':>14} {'margen':>10}")
        print("  " + "-" * 64)
        for _, r in t.iterrows():
            print(f"  {r['mercado']:12} {r['contrato']:10} "
                  f"{r['riesgo_1_contrato']:12,.0f}$ {r['capital_min']:13,.0f}$ "
                  f"{r['margen']:9,.0f}$")
        print(f"\n  Mercados operables: {len(t)}")
        print(f"  Para la cartera COMPLETA hace falta el mas caro: "
              f"{t['capital_min'].max():,.0f}$")
        print(f"  Margen total de las {len(t)} posiciones: {t['margen'].sum():,.0f}$")
        print()

    # --- cuantos mercados compra cada nivel de capital ---
    t = tabla_capital(mercados, micro=False)
    tm = tabla_capital(mercados, micro=True)
    orden = t["mercado"].tolist()
    curva = sharpe_por_subconjunto(mercados, orden)
    por_n = {c["n"]: c for c in curva}

    sh_completa = max((c["sharpe"] for c in curva), default=1.0)
    descuento = SHARPE_OOS_COMPLETA / sh_completa if sh_completa > 0 else 0.0

    def bloque(titulo, tabla, curva_, niveles):
        por = {c["n"]: c for c in curva_}
        print(f"--- {titulo} ---")
        print(f"  {'capital':>12} {'mkts':>5} {'Sharpe':>9} {'Sharpe':>9} "
              f"{'techo':>8} {'CAGR':>9} {'drawdown':>9}")
        print(f"  {'':>12} {'':>5} {'muestra':>9} {'OOS est.':>9} "
              f"{'SR^2/2':>8} {'1/2 Kelly':>9} {'esperado':>9}")
        print("  " + "-" * 68)
        for cap in niveles:
            n = int((tabla["capital_min"] <= cap).sum())
            if n == 0:
                print(f"  {cap:11,}$ {0:5d} {'—':>9} {'—':>9} {'—':>8} "
                      f"{'—':>9}  ni un mercado")
                continue
            c = por.get(n)
            if c is None:
                continue
            sh_oos = c["sharpe"] * descuento
            techo = sh_oos ** 2 / 2
            cagr = techo * 0.75                 # medio Kelly conserva ~75%
            dd = min(0.95, sh_oos * 0.98)       # calibrado: SR 0.591 -> 58%
            print(f"  {cap:11,}$ {n:5d} {c['sharpe']:9.3f} {sh_oos:9.3f} "
                  f"{techo:7.1%} {cagr:8.1%} {dd:8.0%}")
        print()

    print("--- QUE COMPRA CADA NIVEL DE CAPITAL ---")
    print("  El Sharpe cae al quedarte con pocos mercados: la diversificacion")
    print("  era la unica comida gratis y es lo primero que se pierde.")
    print("  'Sharpe muestra' es EN MUESTRA sobre 22 años y esta inflado.")
    print(f"  'OOS est.' le aplica el descuento medido en la prueba 10 (x{descuento:.2f}),")
    print("  que es el que convierte 0.894 en los 0.591 reales fuera de muestra.")
    print("  CAGR y drawdown son a medio Kelly.\n")

    bloque("FUTUROS COMPLETOS", t, curva,
           (25_000, 50_000, 100_000, 250_000, 500_000, 1_000_000, 2_500_000))

    orden_m = tm["mercado"].tolist()
    curva_m = sharpe_por_subconjunto(mercados, orden_m)
    bloque("MICRO FUTUROS", tm, curva_m,
           (10_000, 25_000, 50_000, 100_000, 250_000, 500_000))

    print("=" * 96)
    print("""
LA TERCERA VIA: CFDs EN cTRADER

Los CFD se dimensionan de forma fraccionada (0.01 lotes o menos), asi que el
minimo de contrato desaparece como restriccion: con cualquier capital se puede
tomar la posicion del tamaño exacto que pide el riesgo. Eso baja el minimo
practico a unos pocos miles de dolares.

El precio de esa flexibilidad no es pequeño:
  - Coste por operacion mayor (spread del broker en vez de bolsa regulada).
  - Riesgo de contrapartida: el broker es la otra parte de tu operacion.
  - Financiacion nocturna, que en posiciones de meses se acumula. Este sistema
    mantiene las posiciones semanas o meses: el swap deja de ser un detalle.
  - Muchos brokers de cTrader NO ofrecen agricolas, que es uno de los dos
    sectores con ventaja demostrada (prueba 8).

El backtest de este proyecto NO modela financiacion nocturna. Para la via CFD
habria que rehacer la prueba 8 con el swap dentro antes de creerse el CAGR.
""")


if __name__ == "__main__":
    main()
