# Scenario format (`ctsim-scenario/0`)

One JSON file per scenario. It runs on the engine and, through CtAPI, on the live runtime
(steps a runtime cannot do, such as setting quality, are skipped there and reported).

```json
{
  "format": "ctsim-scenario/0",
  "project": "ExampleSA",
  "description": "Pump P101 starts when the level passes 80 %",
  "user": {"name": "Engineer", "privileges": {"0": 8}},
  "clock": {"start": "2026-10-01T08:00:00", "speed": 1},
  "page": {"window": 0, "name": "WTP_INTAKE_HD1080"},
  "tags": {
    "WTP_T201_LT": {"value": 42.5, "quality": "good"},
    "P101_RUN": {"value": 0}
  },
  "steps": [
    {"at": 1.0, "set": {"WTP_T201_LT": 85}},
    {"at": 1.0, "ramp": {"tag": "WTP_T202_LT", "from": 0, "to": 100, "seconds": 10}},
    {"at": 2.0, "input": {"window": 0, "an": 112, "kind": "click"}},
    {"at": 2.5, "call": "PageDisplay(\"Mixer\")"},
    {"at": 3.0, "expect": {"P101_RUN": 1}},
    {"at": 3.0, "expectPage": {"window": 0, "name": "Mixer"}}
  ]
}
```

- `at`: simulated seconds from the start; steps with the same `at` run in file order.
- `user.privileges`: area (string, "0" = all areas) -> privilege level, as `GetPriv(level, area)`
  reads it.
- `quality`: `good`, `bad`, `uncertain`, or a quality name (see Reading the file).
- `input.kind`: `down`, `up`, `click` (down then up), `key` (+ `"key": "F2"`), `slider`
  (+ `"value"`).
- `expect` compares a tag value after the step time's scan; a failed expectation is reported,
  the run goes on. A number matches within `tolerance` (default 0) plus 1e-9 relative; a
  string expected of a numeric tag is read as a number (within `tolerance`); `true` / `false`
  match a value that is set / not set; anything else compares as text. `null` checks only that
  the tag exists.
- Generators: `ramp`, `sine` (`tag`, `min`, `max`, `period`), `random` (`tag`, `min`, `max`,
  `every`, `seed`).

The steps the engine runs (one action or expectation per step besides `at`):

| step | value |
|---|---|
| `set` | `{tag: value}` or `{tag: {"value": v, "quality": q}}` |
| `quality` | `{tag: quality}` |
| `ramp` | `tag`, `from` (0), `to` (0), `seconds` or `until` (the end: `to` is written there), `every` (0.25 s) |
| `sine` | `tag`, `min` (0), `max` (1), `period` (10 s), `every`, `seconds` / `until`: mid + amp * sin(2 pi (t - t0) / period), the Universal CRT's sin |
| `random` | `tag`, `min` (0), `max` (1), `every`, `seed` (0), `seconds` / `until`: min + (max - min) * mulberry32(seed) / 2^32 |
| `square` | `tag`, `min` (0), `max` (1), `period` (2 s), `duty` (0.5: the part of each period at max), `every`, `seconds` / `until`: min for the first (1 - duty) of each period, then max (a pulse; an on / off digital with min 0, max 1) |
| `input` | `window` (0), `an`, `kind` (`click`), `key`, `value`, `x`, `y` |
| `call` | a Cicode call text |
| `page` / `openPage` | `{"window": w, "name": page}` |
| `ack` | an alarm tag, a list of them, or `all` |
| `disable` / `enable` | an alarm tag or a list of them |
| `setAlarm` | `{alarm: state}`: the alarm server's state of an alarm (tag or `Equipment.Item`), set directly (see below) |
| `setEquipment` | `{equipment: {"MODE": v, "STATE": v, "DRMODE": v}}`: an equipment's runtime fields (what the scheduler and EquipSetProperty set; EquipGetProperty reads them), field names without case, a value as its text |
| `setServer` | `{server: true / false}`: takes a server offline (`false`, until a later step brings it back) or back online; ServerIsOnline answers 0 while it is offline. The name is the project's (without case); it is not checked |
| `login` / `logout` | a user name / anything |
| `expect` | `{tag: value}` or `{tag: {"value": v, "tolerance": d, "quality": q}}` |
| `expectPage` | `{"window": w, "name": page}`, names compared without case |
| `expectAlarm` | `{tag: "on" / "off" / "ack" / "unack" / "disabled"}` or `{tag: {"on": 0/1, "ack": 0/1, "disabled": 0/1, "state": n}}` |

A `setAlarm` state is a word or an object. The words: `on` (ON, unacknowledged), `on_ack` (ON,
acknowledged), `off_unack` (OFF, unacknowledged: still in the alarm list), `off` (OFF,
acknowledged: the normal state), `disabled`. The object's fields (each optional; a field left
out keeps or follows the alarm's state): `on`, `ack`, `disabled` (number fields, nonzero = set),
`state` (an integer: the alarm's .State value, the state enumeration in the low 5 bits + the
masks 256 unacknowledged, 512 disabled, help 1068486: `on` / `ack` / `disabled` follow it when
not given), `onTime` and `time` (when it went ON and its last change: a number = simulated
seconds like `at`, text = an ISO 8601 instant as `clock.start` takes it). A set state holds until
one of the alarm's inputs changes; the alarm scan then evaluates it again from its tags (keeping
its acknowledgement as a scan does).

A top-level `forms` list answers the dialogs the code shows (FormRead, Message, Input, the file
and printer pickers; engine: `ScenarioInteraction`): each entry `{"title": "Recipe", "kind":
"form", "result": "ok", "fields": {"Recipe No": "12", "#3": "x"}, "text": "...", "press":
["Find"], "repeat": false}` answers the next dialog of that title (without case; `"*"` any;
`kind` narrows it: form, message, input, openFile, saveFile, printer). `result` is `ok` (default),
`cancel` or `open` (a FormRead(1) form stays shown); `fields` name a form's fields by their
label (an input's prompt, a check box's or button's text; an input without one takes the text
left of it on its row) or by `#handle`; `text` is an input box's or picker's answer (default:
its default text); `press` presses buttons first (their functions run as tasks). An entry
answers once unless `repeat`. A dialog no entry answers is cancelled (299), so an unattended
run never writes a variable from a form.

A top-level `end` runs the clock on to that time after the last step. A generator writes its
first value at its step's time and then every `every` seconds.

## Reading the file

The file is JSON (RFC 8259, UTF-8). `NaN`, `Infinity` and `-Infinity` are read as numbers, an
integer may have up to 4300 digits, nesting is at most 512 deep, and a key given twice keeps
its first position and takes the last value.

- **Number fields** (`at`, `end`, `every`, `until`, `seconds`, `from`, `to`, `min`, `max`,
  `period`, `tolerance`, `clock.speed`) take a JSON number, `true` / `false` (1 / 0), or a
  string holding a number: blanks (space, tab, CR, LF, VT, FF) around an optional sign and a
  decimal literal (`12`, `1.5`, `.5`, `5.`, `1e-3`) or `inf`, `infinity`, `nan` in any case.
  Nothing else is a number (no digit separators, no other scripts' digits).
- **Integer fields** (`window`, `an`, `seed`) take a JSON integer, a number with a fraction
  (truncated toward zero), `true` / `false`, or a string holding an optional sign and decimal
  digits, blanks around them. A `seed` keeps its low 32 bits (two's complement: -1 is
  4294967295).
- **Text fields** (tag and alarm names, `call`, `login`, `user.name`) take a string as it is;
  another JSON value stands for its JSON text.
- **Sections** (`user`, `clock`, `page`, `tags`, `steps`) and the flag `user.login` are set when
  they are `true`, a nonzero number or a non-empty string, array or object; `null`, `false`, `0`
  and empty values count as absent.
- **Qualities** (`quality` of a tag, `expect` with `quality`): `good`, `bad`, `uncertain`, a
  name of the quality table (`bad_comm_failure`, `bad_not_connected` ...; any case, `-` or a
  space for `_`, an optional `qual_` prefix, blanks around it), or a number: a JSON integer or a
  string of decimal digits or `0x` and hex digits, of which the low 64 bits are kept.

## clock.start

The simulated wall time at t = 0: a JSON number (Unix seconds) or a string holding an ISO 8601
date and time, blanks around it ignored:

- a date: `YYYY-MM-DD` or `YYYYMMDD`, or a week date `YYYY-Www-D` / `YYYYWwwD` (D = 1 for
  Monday .. 7; without D, Monday); years 0001 to 9999;
- then optionally `T`, `t` or a space and a time: `hh`, `hh:mm`, `hh:mm:ss`, `hhmm` or `hhmmss`,
  the seconds with a fraction after `.` or `,` (digits past the microsecond are dropped);
  `24:00` (`24:00:00`, a zero fraction) is midnight at the end of that day;
- then optionally `Z` / `z` or an offset `+hh`, `+hh:mm` or `+hhmm` (or `-`), below 24 hours.

With `Z` or an offset the text is that instant; without one it is local time, converted as the
Universal C runtime's `_mktime64` does (its current daylight saving rule for every year; it
takes nothing before 1970-01-01 UTC or much past the year 3000). The instant must lie between
1601-01-01 and 9999-12-31 UTC (the simulated clock counts in FILETIMEs). A day or time that
does not exist (`2026-02-29`, `12:00:60`), an instant outside those ranges or anything else is
a `bad-start` error: the clock keeps its start.

## Errors

A step that cannot run is reported and the run goes on: the result counts it among its errors
and the trace gets a `scenarioError` event (traces/README.md) with the step, one of these codes
and a plain message:

| code | when |
|---|---|
| `bad-start` | `clock.start` is not a start time |
| `bad-step` | a step's value is not what the step takes: a generator that is no object or has no `tag`, a number field that holds no number, an `every` or `period` that is not above 0, a `setAlarm` state that is none, a `setEquipment` field that is no runtime field, a `setServer` value that is not true or false |
| `tag-error` | a tag could not be set (the message names the runtime error: `error 433`) |
| `no-user` | `login` or a `user` with `login` names no user |
| `no-alarm` | `ack`, `disable`, `enable` or `setAlarm` names no alarm |
| `no-equipment` | `setEquipment` names no equipment |

Anything else the format does not take (a step without exactly one known kind, an `at`, `end`,
`tolerance` or `clock.speed` that holds no number, a quality that is none of the above) is not
reported per step: reading or running the scenario fails with that message.
