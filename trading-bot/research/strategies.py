"""
Generadores de señal.

Uno es el motor actual (Kalman); los otros son las varas de medir. Si un
sistema de dos parametros iguala al Kalman fuera de muestra, la maquinaria
sofisticada no se esta ganando el sitio.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from .kalman import KalmanTrendFilter, KalmanHedgeFilter, RollingStats, normalize_z


def kalman_trend(df: pd.DataFrame,
                 level_var: float = 1e-5,
                 slope_var: float = 1e-7,
                 obs_var: float = 1e-4,
                 z_threshold: float = 1.0,
                 z_window: int = 200,
                 warmup: int = 300,
                 max_abs_z: float = 10.0) -> np.ndarray:
    """El originador del XauusdKalmanModularBot: z-score de la pendiente."""
    close = df["close"].to_numpy(float)
    n = len(close)
    sig = np.zeros(n, dtype=int)

    kf = KalmanTrendFilter(level_var, slope_var, obs_var)
    stats = RollingStats(z_window)

    for i in range(n):
        _, slope = kf.update(close[i])
        stats.add(slope)
        if i < warmup or not stats.ready:
            continue
        z, _ = normalize_z(slope, stats, close[i] * 1e-6, max_abs_z)
        if z >= z_threshold:
            sig[i] = 1
        elif z <= -z_threshold:
            sig[i] = -1
    return sig


def kalman_trend_with_hedge_veto(df: pd.DataFrame, ref: pd.DataFrame,
                                 hedge_z: float = 2.0,
                                 hedge_window: int = 200,
                                 sigma_floor_pct: float = 0.02,
                                 **trend_kwargs) -> np.ndarray:
    """Motor completo: tendencia origina, valoracion relativa VETA (FIX 8).

    Un veto ausente significa 'sin objecion', no 'sin permiso'.
    """
    sig = kalman_trend(df, **trend_kwargs)
    close = df["close"].to_numpy(float)
    rclose = ref["close"].to_numpy(float)

    hf = KalmanHedgeFilter()
    hstats = RollingStats(hedge_window)
    out = sig.copy()

    for i in range(len(close)):
        resid = hf.update(close[i], rclose[i]) if rclose[i] > 0 else 0.0
        hstats.add(resid)
        if sig[i] == 0 or not hstats.ready:
            continue
        z, _ = normalize_z(hstats.last, hstats, close[i] * sigma_floor_pct / 100.0)
        # spread caro => oro sobrevalorado => opinion "vender"
        opinion = -1 if z >= hedge_z else (1 if z <= -hedge_z else 0)
        if opinion != 0 and opinion != sig[i]:
            out[i] = 0          # veto
    return out


# --------------------------------------------------------------------------
# Baselines. Dos parametros cada uno. Esta es la vara de medir.
# --------------------------------------------------------------------------

def donchian(df: pd.DataFrame, lookback: int = 20) -> np.ndarray:
    """Ruptura de maximo/minimo de N barras. El analogo diario del ORB.

    Un solo parametro. Si esto le gana al Kalman, el Kalman sobra.
    """
    h, l = df["high"].to_numpy(float), df["low"].to_numpy(float)
    c = df["close"].to_numpy(float)
    n = len(c)
    sig = np.zeros(n, dtype=int)
    for i in range(lookback, n):
        hi = h[i - lookback:i].max()
        lo = l[i - lookback:i].min()
        if c[i] > hi:
            sig[i] = 1
        elif c[i] < lo:
            sig[i] = -1
    return sig


def ema_cross(df: pd.DataFrame, fast: int = 20, slow: int = 100) -> np.ndarray:
    """Cruce de medias. El sistema tendencial mas tonto que existe."""
    c = df["close"]
    f = c.ewm(span=fast, adjust=False).mean().to_numpy()
    s = c.ewm(span=slow, adjust=False).mean().to_numpy()
    sig = np.zeros(len(c), dtype=int)
    sig[f > s] = 1
    sig[f < s] = -1
    sig[:slow] = 0
    return sig


def buy_and_hold(df: pd.DataFrame) -> np.ndarray:
    """La vara de medir definitiva: estar largo y ya.

    Si el motor no le gana a esto en un tramo alcista, lo unico que hizo
    fue capturar beta direccional con mas pasos intermedios.
    """
    sig = np.ones(len(df), dtype=int)
    sig[:1] = 0
    return sig


def tsmom(df: pd.DataFrame, lookback: int = 252) -> np.ndarray:
    """Time-series momentum (Moskowitz, Ooi & Pedersen 2012).

    Señal = signo del retorno de los ultimos N dias. Un parametro.

    Es EL referente academico de esta familia: documentado sobre 58 futuros
    y 25 años, y replicado despues en muestras mas largas. Se prueba porque
    tiene mejor prior que el Donchian -no lo encontre yo en estos datos, ya
    estaba ahi- y porque si sube el Sharpe, sube el techo de rentabilidad.
    """
    c = df["close"].to_numpy(float)
    sig = np.zeros(len(c), dtype=int)
    if len(c) <= lookback:
        return sig
    dif = c[lookback:] - c[:-lookback]
    sig[lookback:] = np.where(dif > 0, 1, np.where(dif < 0, -1, 0))
    return sig


def combinada(df: pd.DataFrame, lookback_donchian: int = 120,
              lookback_tsmom: int = 252) -> np.ndarray:
    """Ensemble: solo opera cuando ruptura y momentum coinciden.

    No es un filtro de veto sobre un originador: son dos señales
    independientes que tienen que estar de acuerdo. Reduce operaciones y
    deberia subir la calidad de cada una -- si la sube lo bastante para
    compensar operar menos es justo lo que hay que medir.
    """
    d = donchian(df, lookback_donchian)
    t = tsmom(df, lookback_tsmom)
    out = np.zeros(len(d), dtype=int)
    acuerdo = (d != 0) & (d == t)
    out[acuerdo] = d[acuerdo]
    return out
