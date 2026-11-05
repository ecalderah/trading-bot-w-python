"""
Metricas de resultado, incluida la que decide: el Deflated Sharpe Ratio.

La razon de tener DSR y no solo Sharpe: cuando se prueban N configuraciones
sobre la misma muestra, el MEJOR Sharpe observado esta inflado aunque no
haya ninguna ventaja real. El DSR (Bailey & Lopez de Prado, 2014) descuenta
ese sesgo de seleccion. Un backtest con Sharpe 1.5 elegido entre 200 pruebas
puede tener un DSR por debajo de 0.5, es decir: probablemente ruido.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import numpy as np
from scipy.stats import norm

EULER = 0.5772156649015329


@dataclass
class TradeStats:
    trades: int = 0
    wins: int = 0
    losses: int = 0
    gross_profit: float = 0.0
    gross_loss: float = 0.0          # positivo
    net: float = 0.0
    costs: float = 0.0
    r_multiples: list[float] = field(default_factory=list)
    largest_win: float = 0.0
    largest_loss: float = 0.0
    max_consecutive_losses: int = 0
    _streak: int = 0

    def add(self, pnl: float, r: float, cost: float = 0.0) -> None:
        self.trades += 1
        self.net += pnl
        self.costs += cost
        self.r_multiples.append(r)
        if pnl > 0:
            self.wins += 1
            self.gross_profit += pnl
            self.largest_win = max(self.largest_win, pnl)
            self._streak = 0
        else:
            self.losses += 1
            self.gross_loss += -pnl
            self.largest_loss = min(self.largest_loss, pnl)
            self._streak += 1
            self.max_consecutive_losses = max(self.max_consecutive_losses, self._streak)

    @property
    def win_rate(self) -> float:
        return self.wins / self.trades if self.trades else 0.0

    @property
    def profit_factor(self) -> float | None:
        return self.gross_profit / self.gross_loss if self.gross_loss > 0 else None

    @property
    def avg_win(self) -> float:
        return self.gross_profit / self.wins if self.wins else 0.0

    @property
    def avg_loss(self) -> float:
        return self.gross_loss / self.losses if self.losses else 0.0

    @property
    def expectancy_r(self) -> float:
        return float(np.mean(self.r_multiples)) if self.r_multiples else 0.0

    @property
    def realized_rr(self) -> float | None:
        return self.avg_win / self.avg_loss if self.avg_loss > 0 else None

    @property
    def breakeven_win_rate(self) -> float | None:
        rr = self.realized_rr
        return 1.0 / (1.0 + rr) if rr else None

    def t_stat(self) -> float:
        """t de la esperanza en R. |t| < 2 => indistinguible de cero."""
        if len(self.r_multiples) < 2:
            return 0.0
        r = np.asarray(self.r_multiples)
        sd = r.std(ddof=1)
        return float(r.mean() / (sd / np.sqrt(len(r)))) if sd > 0 else 0.0


def equity_metrics(equity: np.ndarray) -> dict:
    """Drawdown y retorno sobre la curva de equity."""
    equity = np.asarray(equity, dtype=float)
    if len(equity) < 2:
        return {"net_return_pct": 0.0, "max_dd_pct": 0.0, "ret_over_dd": 0.0}
    peak = np.maximum.accumulate(equity)
    dd = (peak - equity) / np.where(peak > 0, peak, 1.0)
    max_dd = float(dd.max())
    ret = float(equity[-1] / equity[0] - 1.0)
    return {
        "net_return_pct": ret * 100,
        "max_dd_pct": max_dd * 100,
        "ret_over_dd": (ret * 100 / (max_dd * 100)) if max_dd > 0 else float("inf"),
    }


def sharpe(returns: np.ndarray, periods_per_year: int = 252) -> float:
    r = np.asarray(returns, dtype=float)
    r = r[np.isfinite(r)]
    if len(r) < 2 or r.std(ddof=1) == 0:
        return 0.0
    return float(r.mean() / r.std(ddof=1) * np.sqrt(periods_per_year))


def deflated_sharpe(returns: np.ndarray, n_trials: int,
                    trial_sharpe_std: float | None = None,
                    periods_per_year: int = 252) -> dict:
    """Probabilidad de que el Sharpe observado no sea puro sesgo de seleccion.

    n_trials:          cuantas configuraciones se probaron sobre esta muestra.
    trial_sharpe_std:  dispersion de los Sharpe entre esas pruebas. Si no se
                       pasa, se usa una estimacion conservadora.

    Devuelve dsr en [0,1]. Interpretacion practica:
        dsr > 0.95  la ventaja sobrevive a la correccion
        dsr ~ 0.5   indistinguible de haber elegido el mejor de N ruidos
        dsr < 0.5   peor que ruido
    """
    r = np.asarray(returns, dtype=float)
    r = r[np.isfinite(r)]
    T = len(r)
    if T < 10:
        return {"sr": 0.0, "sr0": 0.0, "dsr": 0.0, "n_trials": n_trials, "T": T}

    sd = r.std(ddof=1)
    if sd == 0:
        return {"sr": 0.0, "sr0": 0.0, "dsr": 0.0, "n_trials": n_trials, "T": T}

    sr = r.mean() / sd                      # por periodo, sin anualizar
    skew = float(((r - r.mean()) ** 3).mean() / sd ** 3)
    kurt = float(((r - r.mean()) ** 4).mean() / sd ** 4)

    if trial_sharpe_std is None:
        # cota conservadora: la dispersion esperada del SR bajo la nula
        trial_sharpe_std = 1.0 / np.sqrt(T)

    n = max(2, int(n_trials))
    sr0 = trial_sharpe_std * (
        (1 - EULER) * norm.ppf(1 - 1.0 / n) + EULER * norm.ppf(1 - 1.0 / (n * np.e))
    )

    denom = np.sqrt(max(1e-12, 1 - skew * sr + (kurt - 1) / 4.0 * sr ** 2))
    dsr = float(norm.cdf((sr - sr0) * np.sqrt(T - 1) / denom))

    return {
        "sr": float(sr * np.sqrt(periods_per_year)),
        "sr0": float(sr0 * np.sqrt(periods_per_year)),
        "dsr": dsr,
        "n_trials": n,
        "T": T,
        "skew": skew,
        "kurtosis": kurt,
    }
