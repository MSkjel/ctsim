# Trace format (`ctsim-trace/0`)

JSON Lines, one event per line, written by the engine and by the live recorder, so two runs of
the same scenario can be compared. The first line is a header:

```json
{"format": "ctsim-trace/0", "engine": "ctsim 0.0.1", "project": "ExampleSA", "scenario": "scenarios/p101.json"}
```

The header also carries `start` (the Unix seconds of t = 0) when the writer has a clock.

Events (`t` = simulated seconds, `task` = task id or null for foreground):

| `ev` | fields |
|---|---|
| `tagWrite` | `tag`, `value`, `by` (`cicode` / `user` / `scenario`) |
| `call` | `fn`, `args` (only for functions marked traced in behaviour.json, or all in verbose mode) |
| `stub` | `fn`, `args`, `returned` (a built-in that is not simulated was called) |
| `error` | `code`, `message`, `fn`, `file`, `line` (a Cicode hardware error) |
| `taskStart` / `taskEnd` | `fn`, `reason` (`returned`, `halted`, `killed`) |
| `window` | `op` (`new`, `free`, `select`), `window`, `page` |
| `page` | `window`, `name`, `event` (`entry`, `shown`, `exit`, ...) |
| `print` | `text` (TraceMsg / ErrLog / kernel output) |
| `expect` | `tag`, `want`, `got`, `ok` |

The engine also writes:

| `ev` | fields |
|---|---|
| `alarm` | `tag`, `event` (`on`, `off`, `state`, `ack`, `disable`, `enable`, `set`: a state set directly, a scenario's setAlarm or a live case's frame), `state`, `by` |
| `skip` | `step`, `reason` (a scenario step this engine cannot do) |
| `scenario` | `event` `start` (`path`, `project`, `start`) or `end` (`passed`, `failed`, `skipped`) |
| `scenarioError` | `step`, `code`, `message` (a scenario step that could not run; the codes are in scenarios/README.md) |
| `file` | `op` (`create`, `write`, `delete`, `copy`, `rename`, `time`), `path`, `other` (a copy's source, a rename's new name), `bytes` (write, copy): a change of a sandbox file (the File* built-ins; the real disk is never written) |
| `dialog` | `op` (`show`, `close`), `kind` (`form`, `message`, `input`, `openfile`, `savefile`, `selectprinter`), `title`, `result` (`ok`, `cancel`) at a close: a form or message box the code showed |
| `capiPost` | `device`, `error`: CAPIPost answered the CtAPI query a host started on the task |
| `soeEvent` | `message`, `tag`, `cluster`: SOEEventAdd journalled a user event |

Every event carries `ev`, `t`, `task` and `n` (its serial in the run, from 1); `tagWrite` adds
`quality` when it is not good, `expect` adds `kind` (`expectPage`, `expectAlarm`) and `quality`
when they apply.

## Values

- `t` is rounded to 6 decimals (half to even on the exact binary value).
- Numbers are written as JSON numbers: an integer as an integer, a double in its shortest
  round-trip form (the fewest digits that read back as the same double; positional with at
  least one decimal, `0.1`, `2.0`, when the decimal exponent is -4 to 15, else `1e+16`,
  `1.5e-05`). JSON has no NaN or infinity: such a value is written as the string `"nan"`,
  `"inf"` or `"-inf"`.
- Strings are UTF-8 JSON strings: `\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t`, `\u00xx` (lower-case
  hex) for the other control characters U+0000 to U+001F, everything else as it is (DEL and
  U+2028 too); a lone surrogate, which UTF-8 cannot hold, is written as U+FFFD. Objects are
  written compactly (no blanks), keys in the order the writer sets them; each line ends in
  `\n`. The engine's `World/TraceFormatTests` pins these rules.

Comparing two traces: events are matched in order per kind; `t` may differ by one scan (the
runtime's scan time) unless the scenario pins it.
