"""
Descarga y cache de precios para las pruebas de validacion.

Limitacion importante y deliberada: yfinance solo da intradia reciente
(1h -> ~730 dias, 5m -> ~60 dias). La prueba de supervivencia sobre
2011-2019 se hace por tanto en DIARIO.

Eso es honesto para la pregunta que responde: "el motor de tendencia
sobre oro, ¿funciona fuera de un mercado alcista?". El filtro de Kalman
del bot es lento (ventana z de 200 barras H1 ~ 8 dias); en diario captura
el mismo fenomeno de tendencia multi-dia. Lo que NO se puede validar aqui
es el scalping intradia: eso exige datos de tick de cTrader.
"""

from __future__ import annotations

from pathlib import Path
import pandas as pd

CACHE = Path(__file__).resolve().parent.parent / "data" / "cache"
CACHE.mkdir(parents=True, exist_ok=True)

# GC=F  futuro de oro COMEX (proxy de XAUUSD; el spot no esta en yfinance)
# SI=F  futuro de plata, para la referencia del modulo de hedge
TICKERS = {"oro": "GC=F", "plata": "SI=F"}


def load(symbol: str = "GC=F", start: str = "2004-01-01",
         end: str | None = None, refresh: bool = False) -> pd.DataFrame:
    """OHLCV diario, con cache en disco."""
    key = symbol.replace("=", "_").replace("^", "_")
    path = CACHE / f"{key}_{start}_{end or 'hoy'}.parquet"

    if path.exists() and not refresh:
        return pd.read_parquet(path)

    import yfinance as yf

    df = yf.download(symbol, start=start, end=end, auto_adjust=False,
                     progress=False, multi_level_index=False)
    if df is None or df.empty:
        raise RuntimeError(f"yfinance no devolvio datos para {symbol}")

    df = df.rename(columns=str.lower)
    df = df[["open", "high", "low", "close", "volume"]].dropna()
    df.index = pd.to_datetime(df.index).tz_localize(None)
    df.index.name = "date"

    try:
        df.to_parquet(path)
    except Exception:
        pass  # el cache es una comodidad, no un requisito
    return df


def load_pair(start: str = "2004-01-01", end: str | None = None,
              refresh: bool = False) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Oro y plata alineados en el mismo indice de fechas."""
    oro = load(TICKERS["oro"], start, end, refresh)
    plata = load(TICKERS["plata"], start, end, refresh)
    idx = oro.index.intersection(plata.index)
    return oro.loc[idx], plata.loc[idx]


# --- cesta multimercado -----------------------------------------------------
# Un seguidor de tendencia sobre UN mercado es una apuesta; sobre veinte es un
# negocio. La ventaja por mercado es pequeña, pero las rachas no coinciden:
# lo que baja la volatilidad de la cartera es la baja correlacion, no acertar
# mas. Es la tesis entera del CTA clasico y lo que este arnes puede probar.
#
# Verificado disponible en yfinance desde 2004 (21 de 23 candidatos).
BASKET: dict[str, str] = {
    # metales
    "oro": "GC=F", "plata": "SI=F", "cobre": "HG=F", "platino": "PL=F",
    # energia
    "crudo": "CL=F", "gas_nat": "NG=F", "gasolina": "RB=F",
    # agricolas
    "maiz": "ZC=F", "trigo": "ZW=F", "soja": "ZS=F",
    "azucar": "SB=F", "cafe": "KC=F", "algodon": "CT=F",
    # indices
    "sp500": "ES=F", "nasdaq": "NQ=F", "nikkei": "NIY=F",
    # tipos
    "bono10": "ZN=F", "bono30": "ZB=F",
    # divisas
    "eurusd": "6E=F", "yen": "6J=F", "dxy": "DX-Y.NYB",
}

SECTORES: dict[str, str] = {
    "oro": "metales", "plata": "metales", "cobre": "metales", "platino": "metales",
    "crudo": "energia", "gas_nat": "energia", "gasolina": "energia",
    "maiz": "agricola", "trigo": "agricola", "soja": "agricola",
    "azucar": "agricola", "cafe": "agricola", "algodon": "agricola",
    "sp500": "indices", "nasdaq": "indices", "nikkei": "indices",
    "bono10": "tipos", "bono30": "tipos",
    "eurusd": "divisas", "yen": "divisas", "dxy": "divisas",
}


def load_basket(start: str = "2004-01-01", end: str | None = None,
                refresh: bool = False, min_bars: int = 2000
                ) -> dict[str, pd.DataFrame]:
    """Descarga la cesta completa. Salta lo que no tenga historia suficiente."""
    out: dict[str, pd.DataFrame] = {}
    for nombre, ticker in BASKET.items():
        try:
            df = load(ticker, start, end, refresh)
        except Exception:
            continue
        if len(df) >= min_bars:
            out[nombre] = df
    return out


# --- regimenes del oro, para leer los resultados por epoca ------------------
# No son "el mercado": son etiquetas para no confundir un backtest bueno
# con haber pillado un tramo alcista.
REGIMES: list[tuple[str, str, str, str]] = [
    ("2004-2011 bull",   "2004-01-01", "2011-09-06", "alcista secular, +450%"),
    ("2011-2013 crash",  "2011-09-06", "2013-12-31", "techo y desplome, -35%"),
    ("2014-2018 chop",   "2014-01-01", "2018-12-31", "lateral largo, rango 1050-1370"),
    ("2019-2020 bull",   "2019-01-01", "2020-08-06", "ruptura y COVID, +65%"),
    ("2020-2022 chop",   "2020-08-07", "2022-12-31", "lateral alto y volatil"),
    ("2023-2026 bull",   "2023-01-01", "2026-12-31", "alcista fuerte (tu muestra)"),
]


def regime_slices(df: pd.DataFrame) -> list[tuple[str, pd.DataFrame, str]]:
    out = []
    for name, a, b, note in REGIMES:
        sl = df.loc[(df.index >= a) & (df.index <= b)]
        if len(sl) > 60:
            out.append((name, sl, note))
    return out
