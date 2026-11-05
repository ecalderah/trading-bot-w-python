"""
Motor de backtest barra a barra.

Decisiones deliberadamente pesimistas, para no comprar humo:

  - La señal se calcula con el CIERRE de la barra i y se entra en la
    APERTURA de la barra i+1. Sin lookahead.
  - Si en la misma barra se tocan stop y objetivo, se asume que se toco el
    STOP. Con datos OHLC no se sabe el orden; asumir lo bueno es el error
    clasico que infla los backtests intradia.
  - El coste ida y vuelta se descuenta ENTERO en cada operacion.
  - El equity se marca a mercado en cada barra, no solo al cerrar: el
    drawdown que sale es el que de verdad habria que aguantar.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import numpy as np
import pandas as pd

from .kalman import atr as atr_ema
from .metrics import TradeStats, equity_metrics, sharpe, deflated_sharpe


@dataclass
class BacktestConfig:
    initial_balance: float = 10_000.0
    risk_pct: float = 0.5              # % del balance arriesgado por operacion
    atr_period: int = 14
    atr_stop_mult: float = 2.0
    reward_risk: float = 1.5           # TP = RR * SL
    cooldown_bars: int = 2
    max_bars_in_trade: int = 0         # 0 = sin limite
    exit_on_flip: bool = True          # salir si la señal se invierte
    cost_usd_per_oz: float = 0.40      # ida y vuelta (ver research/costs.py)

    # ---- modo de salida -------------------------------------------------
    # "tp"     objetivo fijo en RR multiplos del riesgo.
    # "trail"  SIN objetivo: stop que persigue al precio (chandelier). Es el
    #          unico modo compatible con seguir tendencias, porque no le pone
    #          techo a la ganadora. Un objetivo fijo trunca la cola derecha,
    #          que es de donde vive toda la esperanza de un trend follower.
    # "signal" sin objetivo y sin trailing: se sale solo cuando la señal gira.
    exit_mode: str = "tp"
    trail_atr_mult: float = 3.0        # distancia del trailing, en ATR
    direction: str = "both"            # both | long | short
    min_units: float = 1.0             # minimo del broker
    allow_fractional: bool = False


@dataclass
class Trade:
    entry_date: pd.Timestamp
    exit_date: pd.Timestamp
    direction: int
    entry: float
    exit: float
    units: float
    gross: float
    cost: float
    net: float
    r: float
    reason: str
    # instrumentacion para el analisis de errores (research/errors.py)
    bars_held: int = 0
    mae_r: float = 0.0   # excursion adversa maxima, en R (negativa)
    mfe_r: float = 0.0   # excursion favorable maxima, en R (positiva)


@dataclass
class BacktestResult:
    stats: TradeStats
    long_stats: TradeStats
    short_stats: TradeStats
    trades: list[Trade]
    equity: pd.Series
    config: BacktestConfig
    signals_generated: int = 0
    entries_taken: int = 0
    skipped_undersized: int = 0

    def summary(self, n_trials: int = 1) -> dict:
        eq = self.equity.to_numpy()
        m = equity_metrics(eq)
        rets = np.diff(eq) / eq[:-1] if len(eq) > 1 else np.array([0.0])
        d = deflated_sharpe(rets, n_trials=n_trials)
        s = self.stats
        return {
            "trades": s.trades,
            "win_rate": s.win_rate * 100,
            "profit_factor": s.profit_factor,
            "expectancy_r": s.expectancy_r,
            "t_stat": s.t_stat(),
            "net_return_pct": m["net_return_pct"],
            "max_dd_pct": m["max_dd_pct"],
            "ret_over_dd": m["ret_over_dd"],
            "sharpe": sharpe(rets),
            "dsr": d["dsr"],
            "costs": s.costs,
            "max_consec_losses": s.max_consecutive_losses,
        }


def run(df: pd.DataFrame, signals: np.ndarray, cfg: BacktestConfig,
        trail_mult: np.ndarray | None = None) -> BacktestResult:
    """df: OHLC con indice de fechas. signals: -1/0/+1 alineado a df.

    trail_mult: opcional, multiplicador de trailing POR BARRA. Permite que la
    distancia de la salida dependa del estado del mercado el dia de la entrada
    (ver research/adaptive.py). Se congela en la entrada: cambiarlo con la
    posicion viva convertiria el stop en una funcion del futuro.
    """
    o = df["open"].to_numpy(float)
    h = df["high"].to_numpy(float)
    l = df["low"].to_numpy(float)
    c = df["close"].to_numpy(float)
    n = len(df)

    a = atr_ema(h, l, c, cfg.atr_period)

    balance = cfg.initial_balance
    equity = np.full(n, cfg.initial_balance, dtype=float)

    stats, longs, shorts = TradeStats(), TradeStats(), TradeStats()
    trades: list[Trade] = []

    pos_dir = 0
    entry_px = stop_px = tp_px = 0.0
    units = 0.0
    entry_i = -1
    last_exit_i = -10**9
    sig_count = entries = undersized = 0
    mae = mfe = 0.0          # excursiones en R, dentro de la posicion viva
    init_risk = 0.0          # riesgo INICIAL en precio; se congela en la entrada
    trailing_armed = False   # el trailing ya movio el stop por encima del original
    trail_k = cfg.trail_atr_mult   # multiplicador vigente, congelado en la entrada

    def close_position(i: int, price: float, reason: str) -> None:
        nonlocal balance, pos_dir, units, entry_i, mae, mfe, init_risk, trailing_armed
        gross = (price - entry_px) * units * pos_dir
        cost = cfg.cost_usd_per_oz * units
        net = gross - cost
        balance += net
        # R se mide siempre contra el riesgo INICIAL. Usar el stop actual
        # despues de que el trailing lo haya movido inventaria R enormes.
        risk_usd = init_risk * units
        r = net / risk_usd if risk_usd > 0 else 0.0

        t = Trade(df.index[entry_i], df.index[i], pos_dir, entry_px, price,
                  units, gross, cost, net, r, reason,
                  bars_held=i - entry_i, mae_r=mae, mfe_r=mfe)
        trades.append(t)
        stats.add(net, r, cost)
        (longs if pos_dir > 0 else shorts).add(net, r, cost)

        pos_dir = 0
        units = 0.0
        entry_i = -1
        mae = mfe = 0.0
        init_risk = 0.0
        trailing_armed = False

    for i in range(n):
        # ---- gestion de la posicion abierta (con la barra actual) ----
        if pos_dir != 0:
            # excursiones antes de decidir la salida: el extremo adverso de la
            # barra cuenta aunque esa misma barra cierre la operacion
            risk_px = init_risk
            if risk_px > 0:
                if pos_dir > 0:
                    mae = min(mae, (l[i] - entry_px) / risk_px)
                    mfe = max(mfe, (h[i] - entry_px) / risk_px)
                else:
                    mae = min(mae, (entry_px - h[i]) / risk_px)
                    mfe = max(mfe, (entry_px - l[i]) / risk_px)

            hit_stop = (l[i] <= stop_px) if pos_dir > 0 else (h[i] >= stop_px)
            hit_tp = cfg.exit_mode == "tp" and (
                (h[i] >= tp_px) if pos_dir > 0 else (l[i] <= tp_px))

            if hit_stop:                      # pesimista: el stop manda
                close_position(i, stop_px, "stop" if not trailing_armed else "trailing")
                last_exit_i = i
            elif hit_tp:
                close_position(i, tp_px, "objetivo")
                last_exit_i = i
            elif cfg.max_bars_in_trade and (i - entry_i) >= cfg.max_bars_in_trade:
                close_position(i, c[i], "tiempo maximo")
                last_exit_i = i
            elif cfg.exit_on_flip and signals[i] != 0 and signals[i] != pos_dir:
                close_position(i, c[i], "giro de señal")
                last_exit_i = i

        # ---- marca a mercado ----
        if pos_dir != 0:
            open_pnl = (c[i] - entry_px) * units * pos_dir
            equity[i] = balance + open_pnl
        else:
            equity[i] = balance

        # ---- trailing: el stop persigue al precio, nunca retrocede ----
        if pos_dir != 0 and cfg.exit_mode == "trail" and np.isfinite(a[i]) and a[i] > 0:
            dist = a[i] * trail_k
            if pos_dir > 0:
                cand = h[i] - dist
                if cand > stop_px:
                    stop_px = cand
                    trailing_armed = True
            else:
                cand = l[i] + dist
                if cand < stop_px:
                    stop_px = cand
                    trailing_armed = True

        # ---- evaluar entrada para la barra siguiente ----
        if i + 1 >= n:
            continue
        s = int(signals[i])
        if s == 0:
            continue
        sig_count += 1

        if pos_dir != 0:
            continue
        if cfg.direction == "long" and s < 0:
            continue
        if cfg.direction == "short" and s > 0:
            continue
        if i - last_exit_i < cfg.cooldown_bars:
            continue
        if not np.isfinite(a[i]) or a[i] <= 0:
            continue

        px = o[i + 1]
        stop_dist = a[i] * cfg.atr_stop_mult
        if stop_dist <= 0:
            continue

        risk_usd = balance * cfg.risk_pct / 100.0
        u = risk_usd / stop_dist
        if not cfg.allow_fractional:
            u = np.floor(u)
        if u < cfg.min_units:
            undersized += 1
            continue

        pos_dir = s
        entry_px = px
        units = float(u)
        # el multiplicador del trailing se fija AQUI, con lo que se sabe hoy
        trail_k = cfg.trail_atr_mult
        if trail_mult is not None and np.isfinite(trail_mult[i]) and trail_mult[i] > 0:
            trail_k = float(trail_mult[i])
        init_risk = stop_dist
        trailing_armed = False
        stop_px = px - stop_dist * s
        tp_px = px + stop_dist * cfg.reward_risk * s
        entry_i = i + 1
        entries += 1

    # cerrar lo que quede vivo al final
    if pos_dir != 0:
        close_position(n - 1, c[-1], "fin del periodo")
        equity[-1] = balance

    return BacktestResult(
        stats=stats, long_stats=longs, short_stats=shorts, trades=trades,
        equity=pd.Series(equity, index=df.index), config=cfg,
        signals_generated=sig_count, entries_taken=entries,
        skipped_undersized=undersized,
    )
