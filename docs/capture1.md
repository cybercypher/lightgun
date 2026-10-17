# Capture 1 — gun 1 aimed, gun 2 idle (RawInput, Windows host)

- 7950 digitizer reports / 30 s (~265 Hz). Gun 1 (`...MI_01#a&1452421a`, h=B884039D): 7613.
  Gun 2 idle (`...MI_01#a&f25986b`, h=C92E31AF): 337 over 27 s (~12 Hz), X 1725..7134,
  Y 6420..9899, tip=0 — untouched gun wanders thousands of counts. Never trust raw coords.
- Gun 1 span: X 125..9011, Y 520..9900. `in_range` always 1 (useless as gate, confirms gaime_mods).
- Trigger: 373 `tip=1` reports. Rapid down/up flutter at ~30 ms during holds → debounce
  (~50 ms / N-consecutive) required in feeder.
- Trigger-pull jump: 144–810 counts (1.4–8% of 10000 range) vs 150 ms pre-average.
  Feeder must latch-and-hold position from ~50–100 ms *before* the down edge.
- Zero legacy keyboard/mouse RawInput in this pass (logger only handled Type 2 HID;
  fixed to also log Type 0/1). Button map (A/B/Coin/Start) still to capture.
