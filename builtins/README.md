# Built-in behaviour (`behaviour.json`, `ctsim-behaviour/0`)

Signatures come from `Bin\FUNC0.DBF` (the extension's `builtins/builtinFunctions.json` v2 is
a parsed copy). This file holds only what a simulator does when a built-in is called. Keys are
FUNC0 names (case as in FUNC0; lookup is case-insensitive).

```json
{
  "format": "ctsim-behaviour/0",
  "functions": {
    "StrLeft": {"status": "implemented", "cases": ["conformance/runtime/strings/strleft.ci"]},
    "SQLConnect": {"status": "stub", "returns": -1, "error": 273,
                   "notes": "no database in the simulator"},
    "DLLCall": {"status": "refused", "error": 274}
  }
}
```

- `status`: `implemented` (behaviour pinned by its `cases`, or by `doc` notes until a case
  exists), `stub` (returns `returns`, sets `error` as the hardware error when given, and
  is reported as not simulated), `refused` (raises `error`).
- `returns`: a JSON value: a number, `""` for a STRING stub, `null` for a NULL VARIANT. A stub
  whose result depends on its arguments or state has no `returns`, its rule is in `notes`.
  The engine counts every stub call in the VM's not-simulated table (the server's
  `notSimulated` event) and writes a `stub` trace event.
- `effects`: optional list of world changes in words (`"writes the tag named by arg 1"`).
- `blocking`: true when the function can block (not allowed in foreground Cicode).
- `traced`: true when calls go to the trace even in normal mode.
- `pageArg` / `tagArg`: the 0-based argument the function looks up as a graphics page / a
  variable tag. `ctsim/checkExpression` warns about fixed text reaching it (directly or through
  the Cicode functions that pass it on) that names no page / tag of the project.
- A function missing here is `stub` with the FUNC0 return type's zero value, reported as not
  simulated.
