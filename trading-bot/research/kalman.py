"""
Port en Python del motor de señal del XauusdKalmanModularBot.

Es una re-implementacion, no una traduccion literal: el objetivo es poder
correr el MISMO principio sobre 22 años de oro para responder si la ventaja
existe fuera del tramo alcista 2023-2026.

Riesgo conocido y aceptado: C# y Python son dos implementaciones del mismo
modelo y pueden divergir. Por eso `tests/test_kalman.py` fija vectores de
referencia; si algun dia se toca el C#, esos vectores tienen que seguir
cuadrando.
"""

from __future__ import annotations

from collections import deque
import numpy as np


class KalmanTrendFilter:
    """Modelo local lineal: estado = [nivel, pendiente].

        nivel_t    = nivel_{t-1} + pendiente_{t-1} + ruido
        pendiente_t = pendiente_{t-1} + ruido
        observacion = nivel_t + ruido

    La pendiente filtrada es la señal. No es un predictor: es un suavizador.
    El ratio Q/R hace de "lookback" -- esta oculto, no eliminado.

    Implementado con aritmetica ESCALAR (2x2 desenrollado) y no con numpy.
    Es matematicamente identico, pero el bucle por barra no asigna ni un solo
    array: con numpy, el walk-forward agotaba la memoria de la maquina.
    """

    def __init__(self, level_process_var: float = 1e-5,
                 slope_process_var: float = 1e-7,
                 observation_var: float = 1e-4):
        self.q0 = float(level_process_var)
        self.q1 = float(slope_process_var)
        self.R = float(observation_var)
        self._l = 0.0   # nivel
        self._s = 0.0   # pendiente
        self.p00 = self.p11 = 1.0
        self.p01 = self.p10 = 0.0
        self._init = False

    def update(self, price: float) -> tuple[float, float]:
        if not self._init:
            self._l, self._s = price, 0.0
            self._init = True
            return self._l, self._s

        # prediccion:  F = [[1,1],[0,1]],  P' = F P F^T + Q
        l = self._l + self._s
        s = self._s
        p00 = self.p00 + self.p01 + self.p10 + self.p11 + self.q0
        p01 = self.p01 + self.p11
        p10 = self.p10 + self.p11
        p11 = self.p11 + self.q1

        # correccion:  H = [1,0]
        y = price - l
        S = p00 + self.R
        k0 = p00 / S
        k1 = p10 / S

        self._l = l + k0 * y
        self._s = s + k1 * y
        self.p00 = p00 - k0 * p00
        self.p01 = p01 - k0 * p01
        self.p10 = p10 - k1 * p00
        self.p11 = p11 - k1 * p01

        return self._l, self._s

    @property
    def level(self) -> float:
        return self._l

    @property
    def slope(self) -> float:
        return self._s


class KalmanHedgeFilter:
    """Regresion dinamica precio ~ alpha + beta * referencia.

    El residual es la 'valoracion relativa' (oro caro/barato frente a plata).
    En el bot es la Puerta 1, que solo VETA.

    Tambien desenrollado a escalares: H = [1, referencia].
    """

    def __init__(self, alpha_var: float = 1e-6, beta_var: float = 1e-6,
                 obs_var: float = 1e-3):
        self.q0 = float(alpha_var)
        self.q1 = float(beta_var)
        self.R = float(obs_var)
        self.a = 0.0    # alpha
        self.b = 0.0    # beta
        self.p00 = self.p11 = 1.0
        self.p01 = self.p10 = 0.0
        self._init = False

    def update(self, price: float, reference: float) -> float:
        if not self._init:
            self.a = 0.0
            self.b = price / reference if reference else 1.0
            self._init = True
            return 0.0

        # random walk en (alpha, beta):  P' = P + Q
        p00 = self.p00 + self.q0
        p01 = self.p01
        p10 = self.p10
        p11 = self.p11 + self.q1

        r = reference
        resid = price - (self.a + self.b * r)

        # S = H P' H^T + R  con H = [1, r]
        hp0 = p00 + p01 * r          # (P' H^T)[0]
        hp1 = p10 + p11 * r          # (P' H^T)[1]
        S = hp0 + hp1 * r + self.R
        if S <= 0:
            return float(resid)

        k0 = hp0 / S
        k1 = hp1 / S
        self.a += k0 * resid
        self.b += k1 * resid

        # P = P' - K (H P')   con  H P' = [hp0, p01 + p11*r] por filas
        hpr0 = p00 + r * p10         # (H P')[0]
        hpr1 = p01 + r * p11         # (H P')[1]
        self.p00 = p00 - k0 * hpr0
        self.p01 = p01 - k0 * hpr1
        self.p10 = p10 - k1 * hpr0
        self.p11 = p11 - k1 * hpr1
        return float(resid)


class RollingStats:
    """Media, desviacion y mediana sobre ventana movil. Port de la clase C#.

    Media y desviacion se mantienen de forma INCREMENTAL (suma y suma de
    cuadrados, restando el elemento que sale). Recalcular con np.mean/np.std
    en cada barra costaba mas que el propio filtro de Kalman.
    """

    def __init__(self, window: int):
        self.window = max(2, int(window))
        self._buf: deque[float] = deque()
        self._sum = 0.0
        self._sumsq = 0.0

    def add(self, v: float) -> None:
        if v is None or not np.isfinite(v):
            return
        v = float(v)
        self._buf.append(v)
        self._sum += v
        self._sumsq += v * v
        if len(self._buf) > self.window:
            old = self._buf.popleft()
            self._sum -= old
            self._sumsq -= old * old

    @property
    def ready(self) -> bool:
        return len(self._buf) >= self.window

    @property
    def last(self) -> float:
        return self._buf[-1] if self._buf else 0.0

    @property
    def mean(self) -> float:
        n = len(self._buf)
        return self._sum / n if n else 0.0

    @property
    def std(self) -> float:
        n = len(self._buf)
        if n < 2:
            return 0.0
        var = (self._sumsq - self._sum * self._sum / n) / (n - 1)
        return np.sqrt(var) if var > 0 else 0.0

    @property
    def median(self) -> float:
        return float(np.median(self._buf)) if self._buf else 0.0


def normalize_z(value: float, stats: RollingStats, sigma_floor: float,
                max_abs_z: float = 10.0) -> tuple[float, bool]:
    """FIX 1 del bot: sigma de ventana movil, suelo absoluto y clamp.

    El original dividia por la sigma del propio Kalman, que converge a cero,
    y producia z de tres cifras que disparaban siempre.

    Devuelve (z, se_aplico_clamp).
    """
    sigma = max(stats.std, max(sigma_floor, 1e-12))
    z = (value - stats.mean) / sigma
    if not np.isfinite(z):
        return 0.0, False
    if abs(z) > max_abs_z:
        return float(np.sign(z) * max_abs_z), True
    return float(z), False


def atr(high: np.ndarray, low: np.ndarray, close: np.ndarray,
        period: int = 14) -> np.ndarray:
    """ATR exponencial, como el `Indicators.AverageTrueRange(..., Exponential)`."""
    n = len(close)
    tr = np.empty(n)
    tr[0] = high[0] - low[0]
    for i in range(1, n):
        tr[i] = max(high[i] - low[i],
                    abs(high[i] - close[i - 1]),
                    abs(low[i] - close[i - 1]))
    out = np.empty(n)
    out[:] = np.nan
    alpha = 2.0 / (period + 1.0)
    acc = tr[:period].mean()
    out[period - 1] = acc
    for i in range(period, n):
        acc = alpha * tr[i] + (1 - alpha) * acc
        out[i] = acc
    return out
