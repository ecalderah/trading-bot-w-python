# Aparcado

Estos archivos se escribieron antes de tiempo: son infraestructura para
**operar** una estrategia cuya existencia todavia no esta demostrada.

Vuelven al proyecto solo si la validacion (`research/`) demuestra que hay
ventaja. Cada uno tiene defectos conocidos, documentados aqui para no
re-descubrirlos:

## `CostGate.cs`
- `AssumedSlippagePips = 1.0` es un **absoluto en pips**: depende del
  instrumento. Repite el error que el FIX 27 del `XauusdKalmanModularBot`
  ya documenta. Debe ser relativo (multiplo del spread o fraccion de sigma).
- `RecordClosedTrade` pide un `spreadCostPrice` que cTrader no expone
  directamente por posicion.
- Su trabajo actual lo hace `research/costs.py`, que es donde importa ahora.

## `TelemetryEmitter.cs`
- **Grave:** sella cada evento con `DateTime.UtcNow`. En backtest eso es el
  reloj de pared, no `Server.Time`: dos años de backtest quedarian
  registrados como 40 segundos. `Server.Time` tiene que entrar por parametro.
- `AutoFlush = true` mas un snapshot por barra = ~150.000 flushes a disco en
  un backtest M5 de 2 años.
- `TryPost` es fire-and-forget sin guarda de modo: con la URL puesta en un
  backtest dispararia 150.000 peticiones HTTP con `Task` no observadas.

## `VolatilityModel.cs`
- **Grave:** cuenta la estacionalidad dos veces. El EWMA se alimenta con
  retornos crudos (que ya contienen el patron diurno) y despues se vuelve a
  multiplicar por el perfil. El arreglo va en `Update`
  (`ewma.Update(ret / profile(t))`), no en `ForecastSigma`.
- `DiurnalProfile` esta anclado a la hora UTC cruda: medio año esta
  desplazado una hora por el horario de verano.
- La version correcta (y desestacionalizada) vive en `research/volatility.py`.
