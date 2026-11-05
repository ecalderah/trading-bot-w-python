# Hallazgos de la validación

Fecha: 2026-09-06. Reproducible con `python -m research.<modulo>`.

---

> ## ⚠ Corrección (pruebas 6–8)
>
> El análisis de errores destapó un **error de diseño en mi propio motor de
> backtest**: cerraba siempre con un objetivo fijo en múltiplos del riesgo. Para
> un seguidor de tendencia eso no es un parámetro, es un defecto — amputa la cola
> derecha, que es de donde vive toda su esperanza.
>
> Consecuencias, en ambas direcciones:
> - **Se mantiene** la conclusión principal: el Kalman sigue sin batir a un
>   sistema de un parámetro, y necesita seis para acercarse.
> - **Se corrige** el «el efecto es pequeño (+0,5–1,5 % anual)»: era mi motor, no
>   el mercado. Con salida correcta el mismo Donchian casi triplica.
> - **Se matiza** el «el Kalman no tiene ventaja»: con trailing pasa el bootstrap
>   fuera de muestra (p = 0,023).
>
> Las pruebas 1–5 se dejan como estaban, con sus números originales. Las
> pruebas 6–8 las corrigen.

## Resumen

**1. El motor Kalman no tiene ventaja.** Cuatro pruebas independientes dan
`t` entre 0.97 y 1.04 — indistinguible de cero. Pierde contra estar
simplemente largo en todos los regímenes, y contra un cruce de EMAs de dos
parámetros. En el lateral de 2014-2018 pierde dinero. Reoptimizar cada año no
lo salva. Y en un barrido de 1080 configuraciones, **menos** combinaciones
resultan significativas de las que daría el puro azar: no falla una
configuración, falla la familia entera de modelos.

**2. El giro a scalping no cierra con la aritmética.** Con el acierto real del
motor (42.5%), el punto de equilibrio está en objetivos de ~$10/oz (100 pips).
Eso no es un scalp: es aproximadamente la configuración H1 que ya tienes. Para
scalpear de verdad harían falta 56–70% de acierto.

**3. Lo único que sobrevive es lo contrario de lo que se pedía.** Una ruptura
Donchian —un parámetro— pasa el walk-forward (t = 2.25) y muestra una meseta
ancha: 8 de 9 lookbacks entre 20 y 200 son significativos. Pero es **lenta**
(5 operaciones al año), **pequeña** (+0.5 a +1.5%/año) y no sobrevive a la
corrección por búsqueda si se contabiliza honestamente todo lo probado.

El resumen en una frase: **el camino que funciona apunta a menos operaciones y
más lentas, justo en dirección opuesta al scalper por sesión.**

---

## Prueba 1 — Aritmética de costes

`python -m research.costs`

Todo en USD por onza. `pipSize = 0.1` en el broker actual, así 1 pip = $0.10/oz.

Coste ida y vuelta por escenario:

| escenario | spread | comisión | slippage | **total** |
|---|---|---|---|---|
| `ecn_optimo` | 0.12 | 0.07 | 2×0.03 | **$0.25** |
| `ecn_tipico` | 0.20 | 0.10 | 2×0.05 | **$0.40** |
| `estandar` | 0.35 | — | 2×0.08 | **$0.51** |
| `estandar_malo` | 0.50 | — | 2×0.15 | **$0.80** |
| `asia_o_news` | 0.90 | 0.10 | 2×0.30 | **$1.60** |

Con el acierto **real** del motor (42.5%) y R:R 1.5, broker `ecn_tipico`:

| objetivo | | neto por operación | veredicto |
|---|---|---|---|
| $1.00 | 10 pips | −$0.358 | pierde; faltan 21.5 puntos de acierto |
| $2.00 | 20 pips | −$0.317 | pierde; faltan 9.5 puntos |
| $5.00 | 50 pips | −$0.192 | pierde; faltan 2.3 puntos |
| $10.00 | 100 pips | +$0.017 | equilibrio frágil |

**Lectura:** el punto de equilibrio está en ~100 pips de objetivo. Eso no es
scalping; es aproximadamente la configuración H1 que ya tienes. Para scalpear
de verdad (objetivos de 10–30 pips) haría falta un acierto del **56–70%**, que
es una estrategia distinta, no una versión filtrada de la actual.

Dato que apunta en la misma dirección: el backtest en M5 del bot actual dio
**38.9%** de acierto, *peor* que el 42.5% de H1. Bajar de marco temporal
empeora la señal y encarece el coste a la vez. Doble penalización.

---

## Prueba 2 — Supervivencia por régimen

`python -m research.survival` · oro diario 2004–2026, coste $0.40/oz

Muestra completa (22 años):

| estrategia | trades | acierto | PF | esp. R | **t** | retorno | maxDD | Sharpe | **DSR** |
|---|---|---|---|---|---|---|---|---|---|
| kalman | 257 | 43.6% | 1.13 | 0.077 | **1.01** | 9.9% | 5.9% | 0.21 | 0.43 |
| kalman+hedge | 255 | 43.1% | 1.13 | 0.074 | **0.97** | 9.4% | 5.4% | 0.20 | 0.41 |
| donchian(20) | 273 | 44.0% | 1.17 | 0.098 | 1.33 | 13.8% | 8.5% | 0.27 | 0.54 |
| ema(20,100) | 430 | 45.8% | 1.22 | 0.111 | 1.93 | 26.1% | 10.6% | 0.39 | 0.74 |
| **siempre largo** | 387 | 51.4% | 1.54 | 0.264 | **4.17** | **65.5%** | 7.4% | 0.82 | 1.00 |

Por régimen, retorno de cada estrategia:

| régimen | kalman | donchian | ema | siempre largo |
|---|---|---|---|---|
| 2004-2011 alcista | +8.2% | +11.7% | +17.1% | **+33.1%** |
| 2011-2013 desplome | +0.7% | −0.4% | −0.9% | −3.8% |
| **2014-2018 lateral** | **−0.6%** | **−4.7%** | **−5.4%** | +2.9% |
| 2019-2020 alcista | +1.7% | +1.5% | +5.2% | **+6.0%** |
| 2020-2022 lateral | +1.4% | −0.5% | +1.9% | +1.2% |
| **2023-2026 (tu muestra)** | +1.2% | +6.2% | +12.6% | **+16.9%** |

**Tres conclusiones:**

1. **t = 1.01.** La esperanza del motor es indistinguible de cero sobre 22
   años y 257 operaciones. No es que sea pequeña: es que no se distingue del
   ruido.
2. **Pierde contra estar simplemente largo, en todos los regímenes.** Y
   pierde contra un cruce de EMAs de dos parámetros. La maquinaria de espacio
   de estados no se está ganando su sitio.
3. **En el lateral de 5 años (2014-2018) pierde dinero.** El único entorno
   donde el motor no depende de una tendencia secular es justo donde falla.

Nota de honestidad: el motor da 42.5% de acierto y PF 1.11 en el tramo
2023-2026, cifras casi idénticas a las que reportó tu `Mayor Riesgo` en
cTrader. Es llamativo, pero con **40 operaciones** en ese tramo lo más probable
es que sea casualidad. No lo tomo como prueba de que el port reproduce tu bot.

---

## Prueba 3 — Barrido de parámetros y el precio de haber buscado

`python -m research.sweep` · 1080 combinaciones sobre oro diario 2004–2026

Objeción legítima a la prueba 2: esos parámetros están calibrados para H1. Así
que se le dio al motor su mejor oportunidad — barrido completo — y después se
le cobró el coste estadístico de haber buscado.

Mejor configuración encontrada:
`slope_var=1e-5, obs_var=1e-3, z=1.0, ventana=60, SL=1.5×ATR, RR=2.5`
→ 316 operaciones, 38.9% acierto, PF 1.48, esperanza +0.287 R, **t = 3.16**

En muestra parece excelente. Y entonces:

```
Sharpe observado ............ 0.667
Sharpe esperado por AZAR .... 0.546    (el mejor de 1080 pruebas SIN ventaja)
Deflated Sharpe Ratio ....... 0.719    (hace falta > 0.95)
```

**El dato que cierra el caso:**

```
Combinaciones con |t| > 2 ......... 35 de 1080  (3.2%)
Esperadas por puro azar ........... ~5%  (54)
Mediana del t en toda la rejilla .. 0.68
Mediana del retorno (22 años) ..... 5.3%
Mediana del profit factor ......... 1.09
```

Si la familia de modelos tuviera alguna ventaja, **más** del 5% de las
configuraciones serían significativas. Salen **menos**. El mejor resultado de
la rejilla es exactamente lo que produce el máximo de 1080 extracciones de una
distribución sin ventaja.

---

## Prueba 4 — Walk-forward anclado

`python -m research.walkforward` · reoptimizando cada año, 2009–2026

Responde a la última objeción: *"yo no usaría una configuración fija, la
reoptimizaría"*. Se optimiza con datos hasta el 31/12 del año anterior y se
opera el siguiente con la configuración congelada. Solo se juzgan los retornos
fuera de muestra.

| | Kalman | Donchian |
|---|---|---|
| años OOS | 18 | 18 |
| años positivos | 10 (56%) | 13 (72%) |
| operaciones OOS | 211 | 94 |
| acierto | 41.2% | 50.0% |
| profit factor | 1.18 | 1.65 |
| esperanza | +0.074 R | +0.315 R |
| **t** | **1.04 — no significativa** | **2.25 — significativa** |
| retorno acumulado | +7.1% | +16.6% |
| Sharpe OOS | 0.219 | 0.576 |

**El Kalman da t = 1.04**, casi exactamente el mismo valor que en la prueba 2
(t = 1.01). Cuatro pruebas independientes, cuatro veces el mismo resultado: la
esperanza del motor no se distingue de cero. Reoptimizar cada año no lo salva.

**El Donchian sí pasa.** Y eso obligó a una quinta prueba.

---

## Prueba 5 — Estrés del único resultado positivo

`python -m research.robustness`

Un hallazgo positivo merece más escrutinio que uno negativo: es el que te hace
arriesgar dinero. Cuatro ataques al Donchian.

**Ataque 1 — concentración.** 2025 aporta el 42% del total. Sin ese año:
+9.0% en 17 años = **+0.53%/año**. Débil. A favor: 13 de 18 años positivos.

**Ataque 2 — coste.** Muy robusto, porque opera poco (5 veces al año):

| broker | coste | t | retorno | DSR |
|---|---|---|---|---|
| `ecn_optimo` | $0.25 | 2.29 | +16.8% | 0.974 |
| `ecn_tipico` | $0.40 | 2.25 | +16.6% | 0.972 |
| `estandar_malo` | $0.80 | 1.90 | +13.3% | 0.938 |
| `asia_o_news` | $1.60 | 1.71 | +11.6% | 0.905 |

**Ataque 3 — corrección honesta por búsqueda.** El DSR de 0.972 usó
`n_trials=1`. Es correcto para la elección de parámetros *dentro* de cada año,
pero ignora que también se eligieron la estrategia, la rejilla y el instrumento:

| n_trials | qué representa | DSR |
|---|---|---|
| 1 | solo el walk-forward interno | 0.972 |
| 5 | + las 5 estrategias de `survival.py` | **0.891** ✗ |
| 20 | + variantes de rejilla y coste | **0.698** ✗ |
| 100 | + todo lo probado en el proyecto | **0.454** ✗ |

**Ataque 4 — ¿pico aislado o meseta?** Aquí está lo interesante. Donchian con
lookback **fijo**, sin reoptimizar, muestra completa:

| lookback | trades | acierto | PF | **t** | retorno |
|---|---|---|---|---|---|
| 10 | 465 | 39.8% | 0.98 | −0.18 | −3.2% |
| 20 | 328 | 47.6% | 1.32 | **2.49** | +31.6% |
| 30 | 256 | 50.4% | 1.48 | **3.05** | +35.0% |
| 40 | 223 | 48.9% | 1.39 | **2.39** | +24.4% |
| 55 | 192 | 50.0% | 1.44 | **2.50** | +23.8% |
| 80 | 150 | 54.0% | 1.68 | **3.21** | +27.4% |
| 120 | 126 | 57.9% | 1.97 | **3.87** | +30.5% |
| 160 | 106 | 58.5% | 2.03 | **3.68** | +26.1% |
| 200 | 99 | 55.6% | 1.79 | **2.94** | +19.7% |

**Ocho de nueve lookbacks son significativos.** Es una meseta ancha, no un
pico. Y es la imagen especular exacta de la prueba 3: allí solo el 3.2% de las
1080 configuraciones del Kalman era significativo (*menos* que el 5% del azar);
aquí lo es casi toda la familia.

Esto importa porque el efecto **no lo descubrí en estos datos**: la ruptura
tendencial en materias primas es de los resultados más replicados en finanzas
(literatura de CTA/Managed Futures, Donchian en los años 60, las Tortugas). Que
un efecto conocido de antemano aparezca con una meseta ancha es evidencia
mucho más fuerte que encontrar un patrón nuevo rebuscando.

**Síntesis honesta:** el Donchian no es un artefacto —el efecto es real y
conocido— pero es **pequeño** (+0.5 a +1.5%/año al 0.5% de riesgo), **lento**
(5 operaciones al año) y no sobrevive a la corrección por búsqueda si se
contabiliza todo lo que se probó. Sirve como base sobre la que construir; no
como sistema listo para operar.

---

## Prueba 6 — Análisis de errores

`python -m research.errors`

Las pruebas 1–5 responden *«¿hay ventaja?»*. Ésta responde *«¿qué está roto?»*,
que es lo que dice cómo arreglarlo. Instrumenté el backtest con MAE, MFE y
duración por operación.

Lo que apareció en las tres estrategias:

```
motivo        n      neto    R medio
stop        140    -7.684    -1,018
objetivo    119    +9.421    +1,479   <-- TODAS las ganadoras topadas
```

- **`maxR` = exactamente el R:R configurado.** Ninguna ganadora superaba 1,5R.
- **«0 % de las perdedoras llegó a +1,5R»** — porque a +1,5R dejaban de ser
  perdedoras.
- Duración de las ganadoras: **8 barras**. Eso no es seguir una tendencia.
- Asimetría de la distribución de R: **0,2**. Simétrica. Un trend follower sano
  tiene asimetría de 3 o más.

Otros hallazgos del análisis:

- **Los cortos pierden en oro** en las estrategias rápidas (PF 0,62–0,70) pero
  **no en las lentas**: el Donchian 120 tiene cortos con PF 1,44. No es que los
  cortos sean malos; es que los cortos *rápidos* lo son.
- **El stop no está demasiado ajustado**: sólo el 6–7 % de las ganadoras llegó a
  −0,8R antes de ganar.
- **El «giro de señal» destruye valor** en las tres (R medio de −0,06 a −0,48).

---

## Prueba 7 — El modo de salida

`python -m research.exits` · mismas señales, tres formas de salir

| estrategia | salida | n | PF | esp. R | p_boot | asim. | maxR | retorno |
|---|---|---|---|---|---|---|---|---|
| Kalman | tp 1,5R | 257 | 1,13 | +0,077 | 0,161 | 0,3 | 1,5 | +9,9 % |
| Kalman | **trailing 3×ATR** | 184 | 1,32 | +0,161 | 0,088 | 2,2 | 8,9 | +15,3 % |
| Donchian 20 | tp 1,5R | 273 | 1,17 | +0,098 | 0,090 | 0,2 | 1,5 | +13,8 % |
| Donchian 20 | **trailing 3×ATR** | 217 | 1,68 | +0,321 | **0,003** | 3,1 | 14,9 | **+40,3 %** |
| Donchian 120 | tp 1,5R | 106 | 2,05 | +0,446 | 0,0001 | −0,3 | 1,5 | +26,4 % |
| Donchian 120 | **trailing 3×ATR** | 75 | **3,63** | +0,792 | 0,0001 | 1,9 | 9,6 | +34,0 % |
| Donchian 120 | solo señal | 30 | 6,68 | +5,599 | 0,026 | 3,7 | **96,7** | **+104,9 %** |

`p_boot` = P(esperanza ≤ 0) por remuestreo. Con asimetría alta el estadístico
`t` asume normalidad y deja de ser fiable; el bootstrap no supone nada sobre la
forma.

**Una sola operación del Donchian 120 hizo +96R.** Ésa es la firma del
seguimiento de tendencia, y el objetivo fijo la borraba.

### Walk-forward, rehecho con la salida correcta

| estrategia | salida | n OOS | PF | esp. R | p_boot | retorno |
|---|---|---|---|---|---|---|
| Kalman | tp 1,5R | 211 | 1,18 | +0,074 | 0,145 | +7,1 % |
| **Kalman** | **trailing** | 291 | 1,38 | +0,195 | **0,023** ✓ | +22,7 % |
| Donchian | trailing | 65 | **2,73** | **+0,658** | **0,002** ✓ | +23,3 % |

**El hallazgo que más importa:** lo decisivo era la **salida**, no la entrada.
Los 27 FIX del bot fueron todos al originador — al filtro de Kalman, a las
puertas de veto, al z-score. Lo que gobernaba el resultado estaba en el otro
extremo de la operación.

---

## Prueba 8 — Ruptura lenta diversificada

`python -m research.diversified` · 21 futuros, Donchian(120) + trailing 3×ATR

Es lo que señalaban las pruebas anteriores, no una idea nueva. El tamaño sale
del ATR, así que **ya es vol targeting** — no hace falta GARCH encima.

| cartera | mkts | trades | retorno | maxDD | ret/DD | Sharpe | p_boot |
|---|---|---|---|---|---|---|---|
| solo oro | 1 | 75 | +33,6 % | 2,7 % | 12,61 | 0,73 | 0,0001 |
| solo metales | 4 | 313 | +18,5 % | 1,9 % | 9,90 | 0,61 | <0,0001 |
| **completa** | 21 | **1.670** | +8,6 % | **1,1 %** | 7,95 | 0,66 | **<0,0001** |

Con 1.670 operaciones y `p < 0,0001`, **la señal ya no está en discusión**. Y la
meseta se mantiene en la cartera: todos los lookbacks de 20 a 200 dan
`p < 0,0001`.

### Pero el efecto no está repartido

| sector | mkts | Sharpe | p_boot | |
|---|---|---|---|---|
| metales | 4 | 0,61 | <0,0001 | ✓ |
| agrícola | 6 | 0,48 | 0,004 | ✓ |
| índices | 3 | 0,29 | 0,015 | ✓ |
| energía | 3 | 0,18 | 0,166 | ✗ |
| tipos | 2 | 0,07 | 0,301 | ✗ |
| divisas | 3 | 0,08 | 0,309 | ✗ |

**Divisas, tipos y energía no muestran nada.** Eso explica por qué diversificar
baja el drawdown 2,5× (2,7 % → 1,1 %) pero **no sube el Sharpe** (0,73 → 0,66):
la mitad de los mercados diluyen sin aportar.

*Aviso:* quedarse con metales + agrícola + índices porque son los que
funcionaron **es selección en muestra**. Necesita confirmación fuera de muestra
antes de tratarse como decisión.

### Por qué funciona

```
Correlación media entre los P&L de los mercados: 0,058
Pares con correlación > 0,3: 4 de 210
```

Las tendencias de mercados distintos no coinciden en el tiempo. **Ahí está la
reducción de riesgo, no en acertar más.** Es la única comida gratis real del
proyecto.

### A escala de CTA

Los retornos de arriba salen pequeños porque el riesgo es 0,5 % por operación
repartido entre 21 mercados. Lo invariante es el Sharpe; retorno y drawdown
escalan juntos:

| vol objetivo | retorno anual | maxDD estimado |
|---|---|---|
| 5 % | 3,3 % | 9,9 % |
| **12 %** (típico CTA) | **7,9 %** | **24 %** |
| 20 % | 13,2 % | 39,4 % |

Sharpe 0,66 sobre 22 años. Es **real pero nada espectacular** — y encaja con la
industria: el índice SG Trend ronda 0,4–0,6 a largo plazo. El «apalancamiento»
que aparece en el script es un artefacto del reparto equiponderado: en la
práctica cada mercado arriesga un % del capital *total*, no del capital dividido
entre 21. En futuros eso es normal.

---

## Prueba 9 — R:B adaptativo al estado del día

`research.volatility` · `research.conditional` · `research.adaptive`

Tres pasos, en este orden. Empezar por la regla adaptativa habría sido empezar
por el final: añadir parámetros a un sistema cuya ventaja ya es modesta, y el
backtest habría dicho que sí porque siempre hay alguna combinación que funciona.

### Paso 1 — ¿Se predice la volatilidad? Sí

Evaluación fuera de muestra (último 40 %), pérdida QLIKE y regresión de
Mincer-Zarnowitz. Media entre 6 mercados:

| modelo | QLIKE | |
|---|---|---|
| GARCH(1,1) | **−8,4691** | mejor |
| EWMA 0,94 | −8,4477 | +0,021 peor |
| desv. móvil 20 | −8,3960 | +0,073 peor |
| naive \|r ayer\| | +18,28 | inservible |

El coeficiente `b` sale entre 0,66 y 1,04 — el pronóstico es **insesgado**, se
puede usar como escala y no sólo como ordenación. La persistencia
(`α+β`) es 0,97–0,999 en los seis mercados.

**Pero el GARCH apenas le gana al EWMA**, que tiene un parámetro y no hay que
reestimar nunca. Confirma lo que ya se dijo en el análisis inicial: el GARCH no
se gana su sitio.

*Un `R²` del 2–24 % es normal y no es malo: `r²` diario es un estimador
ruidosísimo de la varianza latente. Lo que valida el pronóstico es `b ≈ 1`.*

### Paso 2 — ¿Qué estado predice el recorrido? No el que parece

1.626 operaciones sobre 21 mercados. Se anota el estado el día de la **entrada**
y se mira el MFE — cuánto llegó a recorrer a favor, que es el techo que un
objetivo podría haber capturado.

| variable de estado | MFE Q1 → Q5 | rho | veredicto |
|---|---|---|---|
| vol_ratio (expansión/compresión) | 0,96 → 1,02 | +0,30 | **plano — no sirve** |
| **eficiencia del camino** (Kaufman) | **0,63 → 1,16** | **+0,90** | **monótona** |
| nivel de volatilidad (percentil) | 0,92 → 1,02 | +0,60 | parcial |

**La idea intuitiva —«si hay más volatilidad, sube el objetivo»— no está
respaldada.** El recorrido de la operación no depende del estado de volatilidad.
Sí depende de si el camino reciente fue una línea recta o un zigzag.

Además: el quintil más zigzagueante es el **único que pierde dinero**
(R medio −0,060, acierto 30,4 % frente a 40,3 % en Q5).

### Paso 3 — ¿La regla funciona? Marginalmente, y no de forma demostrable

| walk-forward | trades | retorno | Sharpe | Δ |
|---|---|---|---|---|
| base (trailing fijo) | 1.413 | +5,1 % | 0,540 | — |
| + filtro de eficiencia | 1.410 | +5,0 % | 0,537 | −0,003 |
| **+ R:B adaptativo** | 1.432 | +6,1 % | **0,629** | **+0,089** |
| + ambos | 1.426 | +6,1 % | 0,630 | +0,090 |

Y la prueba que decide — ¿esa mejora se distingue del ruido?

```
Diferencia diaria (adaptativo − base), 4.450 días:
    t = 1,27        P(mejora ≤ 0) = 0,1066
```

**No.** Y con la corrección por búsqueda tampoco:

| n_trials | DSR base | DSR adaptativo |
|---|---|---|
| 1 | 0,960 | 0,983 |
| 4 (las variantes de aquí) | 0,888 | 0,944 |
| 20 (variables y anchos probados) | 0,644 | **0,770** ✗ |
| 100 (todo el proyecto) | 0,397 | 0,544 ✗ |

### Veredicto

- **El filtro de eficiencia está muerto.** −0,003 de Sharpe fuera de muestra, pese
  a que el quintil zigzagueante pierde dinero. Quitar el 5 % de las operaciones no
  mueve la aguja: ese quintil también contiene ganadoras grandes.
- **La relación es real; la regla no está probada.** Que la eficiencia prediga el
  MFE con rho = 0,90 sobre 1.626 operaciones **sí** es sólido. Que convertirlo en
  una escala de trailing mejore el resultado, no: p = 0,107.
- **Estabilidad parcial.** La mejora es positiva en 4 de 5 anchos de escala
  (+0,029 a +0,088) pero se vuelve negativa en el más ancho (0,4–2,0). Es una
  meseta estrecha, no ancha.
- **Debilidad del test que hay que declarar:** el walk-forward reestima el
  *umbral* con datos pasados, pero el ancho de la escala (0,7–1,3) sigue siendo
  in-sample. Es una validación parcial, no completa.

**Recomendación:** conocer la relación es útil —dice dónde está la asimetría del
sistema— pero la regla adaptativa **no se gana los parámetros que cuesta**. Si se
usa, como sesgo pequeño, nunca como característica central.

### Dos bugs corregidos por el camino

- `np.log()` sobre el crudo WTI daba NaN: **cotizó a −37 USD el 20 de abril de
  2020**. Ahora se usan retornos simples con recorte.
- El modelo naive de volatilidad estaba degenerado (un día plano da varianza ~0 y
  el QLIKE del siguiente explota a cientos), lo que inflaba artificialmente a
  todos los demás. Se le puso suelo.

---

## Prueba 10 — Máxima rentabilidad: dónde está el techo

`research.maxret` · `research.config_final`

Pedir «máxima rentabilidad» sin restricción de riesgo no está bien definido:
subiendo apalancamiento sube el retorno *esperado* sin límite. Pero el retorno
**compuesto** —el que determina cuánto dinero acabas teniendo— sí tiene máximo:

```
g(f) = f·μ − f²σ²/2        →     f* = μ/σ²  (Kelly)
g_max = SR² / 2
```

**Es un techo duro.** Ningún apalancamiento lo supera. La única forma de subirlo
es subir el Sharpe.

### Paso 1 — Subir el techo

Walk-forward sobre 17 años, 21 mercados, parámetro elegido con datos pasados:

| señal | Sharpe OOS | techo `SR²/2` |
|---|---|---|
| Donchian 120 | 0,515 | 13,3 % |
| **TSMOM (12 meses)** | **0,591** | **17,5 %** |

TSMOM gana. Y no es un hallazgo mío: es el referente de Moskowitz, Ooi &
Pedersen (2012), documentado sobre 58 futuros. Tiene mejor prior *y* mejor
Sharpe. **Cambiar de Donchian a TSMOM sube el techo del 13,3 % al 17,5 %.**

### Paso 2 — Llegar al techo

Recalculado sobre la serie real, con arrastre de volatilidad y ruina de verdad:

| apalanc. | vol anual | CAGR | maxDD | |
|---|---|---|---|---|
| 16,3× | 15 % | 8,0 % | 34 % | |
| 32,5× | 30 % | 14,0 % | 58 % | medio Kelly |
| **65,0×** | 59 % | **19,1 %** | **84 %** | **el máximo** |
| 97,6× | 89 % | 13,8 % | 96 % | más riesgo, **menos** dinero |
| 130× | 118 % | −0,7 % | 100 % | |
| 325× | — | −100 % | 100 % | ruina |

**La respuesta literal a «máxima rentabilidad posible»: 19,1 % anual, y cuesta
tolerar un drawdown del 84 %.**

*Traducción del apalancamiento:* el «65×» asusta pero es un artefacto de
promediar 21 mercados al 0,5 % cada uno. En términos útiles,
`riesgo/operación = 0,5 % × apalancamiento / 21`:

| perfil | riesgo por operación | vol | CAGR | drawdown |
|---|---|---|---|---|
| prudente (16×) | 0,39 % | 15 % | 8,0 % | 34 % |
| **medio Kelly (32×)** | **0,77 %** | 30 % | **14,0 %** | **58 %** |
| Kelly (65×) | 1,55 % | 59 % | 19,1 % | 84 % |

Arriesgar ~0,8 % del capital por operación con ~21 mercados abiertos es una
configuración CTA normal, no una locura.

### Paso 3 — Por qué no Kelly completo

El Sharpe OOS es **0,591 ± 0,266**. El intervalo al 95 % va de 0,07 a 1,11, así
que el techo real está entre 0,3 % y 62 % anual. Con esa incertidumbre:

```
Kelly con el Sharpe optimista .... 65x
Kelly con el pesimista ...........  8x
```

Si el Sharpe verdadero está en la parte baja y apalancas a 65×, estás operando
**ocho veces por encima de Kelly** — la zona donde el crecimiento compuesto se
vuelve negativo. La tabla lo enseña: a 130× el CAGR ya es −0,7 %.

Medio Kelly conserva ~75 % del crecimiento con la mitad de volatilidad y
sobrevive a que la estimación se quede corta. Es el estándar de la industria
por esta razón exacta, no por prudencia estética.

### Lo que hay que aceptar para cobrar el 14 %

- **Un drawdown histórico del 58 %.** No es una cola improbable: ya ocurrió en la
  muestra. Hay que poder verlo sin apagar el sistema.
- **Rachas largas.** Cinco operaciones por mercado y año significa que un año malo
  no da información suficiente para saber si el sistema está roto.
- **21 mercados abiertos a la vez**, con margen suficiente en el peor momento. Si
  el bróker te liquida en el drawdown, el CAGR histórico no sirve de nada.
- **El drawdown es sobre datos diarios limpios.** En vivo, con huecos de fin de
  semana y noticias, será peor.

---

## Prueba 11 — ¿Con cuánto capital funciona?

`research.capital`

Es la restricción que mató al bot original: su FIX 21 documenta que con $1.000
al 0,5 % de riesgo, **el 0 % de las señales era operable**. La cartera de la
prueba 10 necesita ~21 mercados abiertos a la vez, cada uno arriesgando 0,77 %
del capital total.

`capital mínimo por mercado = riesgo de 1 contrato / 0,77 %`

### Futuros completos — el mercado más caro fija el mínimo

| mercado | riesgo 1 contrato | capital mínimo |
|---|---|---|
| maíz | $922 | $119.733 |
| azúcar | $935 | $121.377 |
| … | | |
| oro | $3.137 | $407.400 |
| crudo | $3.990 | $518.148 |
| **gasolina** | $5.103 | **$662.709** |

Margen total de las 21 posiciones: **$141.000**.

### Qué compra cada nivel de capital

El Sharpe cae al quedarte con pocos mercados — la diversificación era la única
comida gratis y es lo primero que se pierde. Las cifras llevan el **descuento
fuera de muestra** (×0,66) medido en la prueba 10; sin él serían fantasía.

**Futuros completos:**

| capital | mercados | Sharpe OOS | CAGR | drawdown |
|---|---|---|---|---|
| <$120k | 0 | — | — | ni un mercado |
| $250k | 9 | 0,310 | 3,6 % | 30 % |
| $500k | 19 | 0,575 | 12,4 % | 56 % |
| $1M | 21 | 0,591 | 13,1 % | 58 % |

**Micro futuros** (~1/10 del tamaño — cambia el problema por completo):

| capital | mercados | Sharpe OOS | CAGR | drawdown |
|---|---|---|---|---|
| <$24k | 0 | — | — | ni un mercado |
| $25k | 2 | 0,202 | 1,5 % | 20 % |
| $50k | 9 | 0,395 | 5,9 % | 39 % |
| **$100k** | 11 | **0,509** | **9,7 %** | 50 % |
| $250k | 12 | 0,480 | 8,6 % | 47 % |

### La tercera vía — CFDs en cTrader — no funciona

Los CFD eliminan el mínimo de contrato: se dimensionan de forma fraccionada, así
que con cualquier capital se toma el tamaño exacto que pide el riesgo. Parece la
solución obvia para una cuenta pequeña. **Y la financiación nocturna la destruye.**

Este sistema mantiene posiciones **semanas o meses** (TSMOM a 12 meses + salida
por trailing). Medido sobre el backtest:

```
Nocional medio por posición abierta ......  22% del sub-libro
Ocupación (tiempo con posición abierta) ..  78%
Nocional bruto de cartera, sin apalancar .  18% del capital
A medio Kelly (32,5x) .................... 570% del capital
```

| financiación CFD | coste anual | del CAGR 13,1 % queda |
|---|---|---|
| 2 % | 11,4 % | **1,7 %** |
| 3 % | 17,1 % | **−4,0 %** |
| 5 % | 28,5 % | −15,4 % |
| 7 % | 39,9 % | −26,8 % |

**A una financiación del 3 % —conservadora— el sistema es negativo.** No es un
problema de capital: es un problema de vehículo. En futuros ese coste no existe
como cargo, va embebido en el precio del contrato y ya está dentro del backtest.
Es exactamente la razón por la que los CTA usan futuros y no CFDs.

### Respuesta

| vehículo | capital mínimo real | qué obtienes |
|---|---|---|
| **CFDs (cTrader)** | — | **no funciona a ningún capital** |
| Micro futuros | **$100k** | 11 mercados, ~9,7 % anual, 50 % DD |
| Micro futuros | $50k | 9 mercados, ~5,9 % anual — marginal |
| Futuros completos | $500k–1M | 19–21 mercados, ~13 % anual, 58 % DD |
| Cualquiera | <$25k | nada |

*Dos correcciones aplicadas al hacer esta prueba:* el multiplicador del yen
estaba 100× mal (daba $19 de riesgo por contrato y lo colaba como «el mercado
barato»), y las Sharpe de la tabla eran en muestra — presentarlas sin el
descuento habría mostrado un CAGR del 30 % en vez del 13 %.

---

## Prueba 12 — Optimización, y el plan por capital

`research.optimize` · `research.plan`

Optimizar para máxima rentabilidad **sólo puede significar subir el Sharpe**: el
techo es `SR²/2` y el apalancamiento lleva al techo pero no lo mueve.

### Las tres mejoras que probé — las tres empeoran

Elegidas porque son *diversificación*, no ajuste de parámetros (no añaden grados
de libertad sobreajustables):

| señal | pesos | Sharpe |
|---|---|---|
| **TSMOM 252** | **iguales** | **0,756** |
| TSMOM 252 | vol inversa | 0,676 |
| TSMOM 252 | por sector | 0,676 |
| ensemble 63/126/252 | iguales | 0,602 |
| ensemble 63/126/252 | vol inversa | 0,598 |
| ensemble 63/126/252 | por sector | 0,546 |

Resultado negativo limpio: **ninguna ayuda**. Ni el ensemble de velocidades ni la
ponderación por volatilidad inversa ni la sectorial.

### Lo que sí subió el Sharpe: dejar de optimizar

| configuración | Sharpe | techo |
|---|---|---|
| TSMOM fijo 63 | 0,322 | 5,2 % |
| TSMOM fijo 126 | 0,551 | 15,2 % |
| TSMOM fijo 189 | 0,483 | 11,7 % |
| **TSMOM fijo 252** | **0,756** | **28,6 %** |
| TSMOM fijo 378 | 0,752 | 28,3 % |
| TSMOM fijo 504 | 0,728 | 26,5 % |
| **TSMOM reoptimizado cada año** | **0,591** | 17,5 % |

**Reoptimizar el lookback cada año lo empeora.** Fijarlo en 252 —el valor del
paper original, no uno elegido mirando estos datos— da Sharpe 0,756 y sube el
techo del 17,5 % al 28,6 %. Y hay meseta ancha: 252, 378 y 504 dan 0,73–0,76.

Es coherente con todo el proyecto: buscar parámetros añade ruido, no ventaja.

### El plan según el capital

`python -m research.plan 50000 MXN` · `python -m research.plan 500000 USD`

**Todas las cifras del proyecto son USD.** Los futuros CME cotizan en dólares.
Tipo USD/MXN = 16,88.

| USD | MXN | vehículo | mkts | Sharpe | riesgo/op | CAGR | drawdown |
|---|---|---|---|---|---|---|---|
| $3.000 | 50.649 | — | 0 | — | — | — | **no viable** |
| $10.000 | 168.830 | — | 0 | — | — | — | **no viable** |
| $25.000 | 422.075 | micro | 2 | 0,095 | 0,66 % | 0,3 % | 28 % |
| $50.000 | 844.150 | micro | 9 | 0,458 | 1,20 % | 8,2 % | 67 % |
| **$100.000** | **1.688.300** | micro | 11 | 0,657 | 1,39 % | **17,6 %** | 64 % |
| $250.000 | 4.220.750 | micro | 12 | 0,623 | 1,29 % | 15,7 % | 66 % |
| $500.000 | 8.441.500 | completo | 19 | 0,712 | 1,04 % | 20,9 % | 70 % |
| $1.000.000 | 16.883.000 | completo | 21 | 0,756 | 1,03 % | 23,9 % | 67 % |

CAGR y drawdown son **medidos** apalancando la serie real a medio Kelly, no
salidos de una fórmula. Una versión anterior usaba `dd ≈ SR × 0,98`, que era
inventado.

### Los dos casos concretos

**50.000 MXN = $2.962 USD → NO VIABLE.** No entra ni un mercado: el contrato más
barato (micro maíz, XC) exige $23.947 para que su riesgo no pase del umbral. Las
tres salidas aparentes y por qué ninguna sirve:

1. *Subir el riesgo por operación* — con un solo mercado el Sharpe cae a ~0,1 y
   el techo baja al 0,3 % anual pase lo que pase.
2. *CFDs en cTrader* — quitan el mínimo de contrato, pero la financiación
   nocturna deja el sistema negativo (prueba 11).
3. *Operar un solo mercado* — es una apuesta direccional, no esta estrategia.

Umbral real de entrada: **$49.216 (831.000 MXN)** para 9 mercados micro.

**500.000 USD (8,44 M MXN):** futuros completos, 19 mercados, riesgo 1,04 % por
operación ($5.200), Sharpe 0,712, **CAGR 20,9 % ($104.500 al año)** con
**drawdown medido del 70 % ($350.000 de caída desde máximos)**.

### El aviso que acompaña al 24 %

Subir el Sharpe subió el CAGR de 13,1 % a 23,9 %, pero también el drawdown de
58 % a 67 %, porque medio Kelly escala con el Sharpe. Un drawdown del 67 % es ver
un millón convertirse en 330.000.

**La rentabilidad que obtienes es la que puedes aguantar.** Un sistema abandonado
en el fondo del drawdown realiza −67 %, no +24 %. Si esa cifra no es tolerable, la
respuesta no es esperar que no pase: es bajar el apalancamiento y aceptar menos
CAGR.

---

## Qué NO prueba esto

Hay que ser exacto sobre el alcance:

- **Es diario, no H1.** Tu bot opera H1. yfinance no da intradía histórico de
  22 años, así que la prueba es sobre el mismo fenómeno (tendencia multi-día)
  en otro marco temporal.
- **Es `GC=F` (futuro COMEX), no XAUUSD spot.** Instrumento parecido, no
  idéntico: sin swaps, spread distinto.
- **No valida ni invalida el scalping intradía** por sí solo. Eso exige datos
  de tick de cTrader. Lo que sí decide sin datos es la prueba 1, y la prueba 1
  ya dice que no cierra con el acierto actual.
- **A escala diaria el coste es casi irrelevante** ($0.40 sobre un stop de
  ~$40 es el 1%). Los resultados negativos **no** son culpa del modelo de
  costes; el motor pierde por señal, no por fricción.
- **Los futuros son continuos de yfinance, sin ajuste de rolo.** En mercados con
  contango fuerte (gas natural) eso distorsiona los retornos largos. Para operar
  de verdad haría falta una serie ajustada por rolo.
- **La prueba 8 no modela margen, límites de exposición ni riesgo de ruina.**
  Valida la señal y el efecto de diversificar, no una cuenta real.
- **Quedarse con metales + agrícola + índices es selección en muestra.** Que
  sean los sectores que funcionaron no garantiza que sigan haciéndolo.

La prueba 3 es la más robusta a todo esto, porque no juzga una configuración
sino la **familia entera de modelos**, y esa conclusión no depende del marco
temporal ni del instrumento exacto. La prueba 8, con 1.670 operaciones y
`p < 0,0001`, es la más robusta del lado positivo.

---

## Qué hacer

1. **Arreglar la salida antes que nada más.** Es el cambio con mejor relación
   esfuerzo/resultado de todo el proyecto: sustituir el objetivo fijo por un
   trailing de 3×ATR casi triplica el resultado de la misma señal, sin tocar
   una línea del originador. Si sólo se hace una cosa, que sea ésta.
2. **No escribir el bot scalper.** La aritmética no cierra con el acierto que
   el motor consigue, y bajar de marco temporal lo empeora (M5 dio 38,9 %,
   *peor* que el 42,5 % de H1) mientras encarece el coste. Doble penalización.
   Además, todo lo que funciona apunta a operar **menos y más lento**.
3. **Dejar de invertir en el originador.** El Kalman con la salida corregida
   llega a `p = 0,023` fuera de muestra — no es basura. Pero un Donchian de
   **un** parámetro logra mejor PF (2,73 vs 1,38) y mejor esperanza (+0,658 vs
   +0,195 R). Seis parámetros y 27 FIX para quedar por debajo.
4. **No añadir más capas** (GARCH, régimen Markov, ML). Los filtros quitan
   operaciones; no crean ventaja. Lo dice el FIX 26 de tu propio bot y lo
   confirma la prueba 2. Y el sizing por ATR **ya es vol targeting**: no hace
   falta un GARCH encima.
5. **El camino que sí tiene evidencia:** ruptura lenta (lookback 80–160) con
   trailing, diversificada sobre metales, agrícolas e índices. Sharpe 0,66 sobre
   22 años y 1.670 operaciones. A escala de CTA (12 % de vol) son ~7,9 % anual
   con ~24 % de drawdown. Real, no espectacular, y honesto respecto a la
   industria.
6. **Lo siguiente que haría:** confirmar la selección de sectores fuera de
   muestra (walk-forward sobre la cartera, no sólo sobre el oro), y sustituir
   yfinance por series ajustadas por rolo.
7. **Si se sigue con oro intradía:** el paso obligatorio es exportar datos de
   **tick** de cTrader y medir el spread real por hora. La prueba 1 dice que el
   listón está en 56–70 % de acierto.
8. **Sobre vender esto a clientes:** un Sharpe de 0,66 es un producto honesto
   pero difícil de vender a retail, que espera mucho más. Y sigue pendiente el
   marco legal (licencias, claims de rendimiento) — es un proyecto distinto del
   técnico.
9. **Lo que sí sobrevive del código:** `shared_cbot/SessionEngine.cs`,
   corregido y con 33 vectores en verde. Sirva la estrategia que sirva.
