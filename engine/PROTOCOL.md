# ctsim.exe: the server and its protocol (`ctsim/0`)

What `ctsim serve` and `ctsim dap` implement. Every JSON shape here is what the code sends and
accepts; clients (ctgedit's Simulate window, the VS Code extension, tests) are checked against
this file.

## Processes

```
ctsim serve --project <dir> [--id id] [--root name] [--rdb file] [--compile changed|all|none]
            [--start iso] [--clock run|paused] [--speed x] [--step seconds] [--scan ms]
            [--trace file] [--startup function|auto] [--tz zone] [--discovery dir] [--log]
            [--simulate-undefined] [--seed-trend-history]
            [--dll off|ask|on] [--sql off|ask|on] [--dll-allow path]... [--sql-allow database]...
            [--dll-timeout seconds]
ctsim dap   --project <dir> (the same options)
ctsim servers [--discovery dir] [--project dir [--root name]]
ctsim dllhost
```

- **serve** listens and prints one line on stdout, then runs until `ctsim/shutdown`, Ctrl+C or the
  process is killed:
  `{"event":"ready","id":"14892-ac9427cc","pipe":"\\\\.\\pipe\\ctsim.14892-ac9427cc","transport":"pipe","address":"\\\\.\\pipe\\ctsim.14892-ac9427cc","pid":14892,"discovery":"...\\14892-ac9427cc.json","project":"C:\\...\\ExampleSA","root":"ExampleSA","rootReason":"self","roots":["ExampleSA"],"loadMs":207}`
- **dap** speaks DAP on stdin / stdout (VS Code's DebugAdapterExecutable). Only DAP frames go to
  stdout; console output goes to stderr. It also listens on its pipe and writes a discovery file,
  so viewers can join the same simulation. It ends when the session ends.
- **servers** lists the live servers in the discovery folder, one JSON line each.
- **dllhost** is a simulation's library helper. The server starts it at the first DLL a simulation
  loads and talks to it over stdin / stdout, one JSON request and answer per line. Libraries
  (DLLOpen, DllClassCreate) live there, so a crash or hang ends the helper, not the simulation:
  the call fails with a log line and the next call starts a new helper. It's 32-bit, like the
  runtime.

Options:

- `--compile`: `none` runs `_LIBRARY.RDB`; `all` compiles every function from source; `changed`
  (default) runs the RDB but compiles the .ci files newer than it (a newer file that doesn't
  compile keeps its RDB code), and everything from source when there's no RDB. A `--project` or
  `--rdb` that doesn't exist is refused.
- `--clock`: `run` (default; follows the wall clock times `--speed`) or `paused`.
- `--speed` above 0, `--step` seconds above 0, `--scan` whole milliseconds 1 to 3600000.
- `--log`: every simulation log line on stderr.
- `--simulate-undefined`, `--seed-trend-history`, `--dll`, `--sql`, `--dll-allow`, `--sql-allow`,
  `--dll-timeout`: start with those `ctsim/options` set.
- Exit codes: 0 normal end, 1 project couldn't load or pipe couldn't open, 2 bad arguments.

When `dap` can't start the simulation (exit code 1), it keeps its stdio session up so the client
can say why: `initialize` answers (only `supportsTerminateRequest`, no `initialized` event),
`launch` and `attach` send an `output` event on stderr and fail with the reason
(`ctsim cannot load the project: ...` or `ctsim cannot open its pipe: ...`), `terminate` and
`disconnect` answer and end it. No pipe, no discovery file.

### The root

The runtime runs one compiled project (`[CtEdit] Run`) and compiles its whole include closure, so
a page of an included project sees the tags, equipment, alarms and Cicode of the project that
compiles it. The simulation does the same: it runs the opened project as its **root**.

Candidates are the registered top-level projects (included by no project; never Include or
System) whose include closure holds the opened project, or the project itself when it is one. The
root is, in order: `--root` / `{root}` when given (a root that doesn't compile the project fails
the load), the only candidate, `[CtEdit] LastDatabase` of the active citect.ini when it's a
candidate, else the first by name. A project that no registered root compiles is its own unit.
The program is the root's `_LIBRARY.RDB`.

`rootReason` (in the ready line, hello / load bodies and discovery entry): `asked`, `chosen`,
`self`, `only`, `lastDatabase`, `firstByName`, or `none` (its own unit). `roots`: the candidates.

A server of the same project with another root is a different simulation. A client attaching for
(project, root) takes a discovery entry where both match, and `ctsim/hello` / `ctsim/load` with a
`root` fail on a server of another root.

## Discovery

`%LOCALAPPDATA%\cicode-sim\servers\<id>.json` (`--discovery` or `CICODESIM_SERVERS` overrides
it), written atomically and removed when the server ends. `<id>` = `<pid>-<8 hex digits>`.

```json
{"format": "ctsim-server/0", "id": "14892-ac9427cc", "transport": "pipe",
 "pipe": "\\\\.\\pipe\\ctsim.14892-ac9427cc", "address": "\\\\.\\pipe\\ctsim.14892-ac9427cc",
 "pid": 14892, "processStart": 134353658621290372, "processStartIso": "...",
 "mode": "serve", "project": "C:\\...\\User\\ExampleSA",
 "projectName": "ExampleSA", "root": "ExampleSA", "rootReason": "self", "roots": ["ExampleSA"],
 "user": "C:\\...\\User", "install": "C:\\...", "version": "0.0.1.0",
 "protocolRevision": 30, "commit": "...", "buildTime": "...", "started": "..."}
```

- `processStart`: the process start time as a UTC FILETIME.
- An entry is stale when no process has its pid, or that pid's process started more than 1 s
  away from `processStart` (a reused pid). Listings ignore and delete stale entries and old
  temporary files.
- `transport` is `pipe` on Windows (current user only), `unix` (a socket path) or `tcp`
  (`127.0.0.1:<port>`, no access control) elsewhere; `address` is what a client connects to.

## Versions

`ctsim --version` prints the name, version, protocol revision, commit and build time;
`--version --json` gives `{name, version, protocolRevision, commit, buildTime}`. Hello and the
discovery entry carry the same fields.

`protocolRevision` goes up by one whenever the server gains something a client may rely on. A
client asks for the revision it needs and won't attach to a server with a lower one (a missing
field counts as revision 0).

## Messages

DAP base framing (`Content-Length: N\r\n\r\n` + N bytes of UTF-8 JSON) with DAP-shaped bodies.
Clients number their requests; the server numbers its own messages per connection. Output is
strict JSON: a non-finite REAL is the string `"Infinity"`, `"-Infinity"` or `"NaN"`. Every
request gets one response.

```json
{"seq": 3, "type": "request", "command": "ctsim/setTag", "arguments": {"name": "Pump1", "value": 1}}
{"seq": 7, "type": "response", "request_seq": 3, "success": true, "command": "ctsim/setTag", "body": {...}}
{"seq": 7, "type": "response", "request_seq": 3, "success": false, "command": "ctsim/setTag", "message": "setTag Pump1: error 424 Tag not found", "body": {...}}
{"seq": 8, "type": "event", "event": "ctsim/tagsChanged", "body": {...}}
```

A connection is a **viewer** (any number) when it sends `ctsim/*` requests, or the **debugger**
(one at a time) when its first request is DAP `initialize`. Anything else first is refused.
Viewer events go to connections that sent `ctsim/hello`; `ctsim/tagsChanged` goes to every
connection with subscriptions. A request whose handler fails gets `success: false` and a
`ctsim/log` error.

## Requests

### ctsim/hello `{client?, events?, root?, dialogs?, models?, images?}`

- `root`: the root the client expects (fails on a server of another root).
- `events`: the kinds wanted, any of `log`, `task`, `notSimulated`, `clock`, `scenario`, `page`
  (pageChanged and windowsChanged), `options`, `alarms`, `debug` (default all).
- `dialogs: true`: this viewer shows the simulation's dialogs (`ctsim/dialog`).
- `models: true`: this viewer sends a page's drawing when asked (`ctsim/modelWanted`).
- `images: true`: this viewer draws a window's picture when asked (`ctsim/imageWanted`).

Body:

```json
{"project": "C:\\...\\ExampleSA", "projectName": "ExampleSA", "root": "ExampleSA",
 "rootReason": "self", "roots": ["ExampleSA"], "rootDir": "C:\\...\\ExampleSA",
 "unit": [{"name": "ExampleSA", "dir": "..."}],
 "user": "C:\\...\\User", "install": "C:\\...", "functions": 1869,
 "compile": "changed", "notes": ["..."], "loadMs": 207,
 "server": "ctsim", "version": "0.0.1.0", "protocolRevision": 30, "commit": "...",
 "buildTime": "...", "protocol": "ctsim/0", "id": "14892-ac9427cc",
 "pid": 14892, "mode": "serve", "pipe": "...", "transport": "pipe", "address": "...",
 "clock": {CLOCK}, "debugger": false, "clients": 2, "user": {USER},
 "capabilities": {...}, "options": {...}}
```

`unit` lists every project the simulation reads, in search order. `capabilities` names what the
server supports: `pages`, `compile`, `scenarios`, `debug`, `simulateUndefined`, `windowTags`,
`root`, `alarms`, `dialogs`, `models`, `windowImages`, `displayText`, `navigate`, `controls`, `drawnButtons`,
`alarmListLayout`, `trendHistory`, `windowSerials`, `pageVars`, `tagProperties`, `profile`,
`profileTree`, `foregroundBreak`, `history`, `regexFilter`, `outside`, `checkExpression`. Clients
should check `protocolRevision`; the capabilities are informative.

### ctsim/load `{project?, root?, page?, scenario?, startup?, startPage?, fastStartup?, simulateUndefined?}`

- `project`, `root`: must match this server, or the request fails with a message saying what to
  start instead.
- `simulateUndefined`: as in `ctsim/options`, applied first.
- `startPage: true`: show the project's startup page in window 0 first (below).
- `page`: opened as `ctsim/openPage {name}` would.
- `startup`: a function started as a task, or `"auto"` (below).
- `scenario`: a path or inline scenario object, loaded and run.

Body: the hello body plus `loadNotes`, `page` (the openPage body), `startPage`, and the startup
fields below.

**`startup: "auto"`** (also `serve --startup auto`) runs what the runtime runs when it starts,
once per simulation, each as a task, after window 0 shows a page (SA workspaces check that a
page is shown). In order: Include's `_CitectBegin` when the library has it, then
`[Client]StartupCode`, then the StartupCode of each server role in the unit. Values are read as
ParameterGet would, except that a `citect.ini` in the project folder answers for its own sections,
and a value naming no function falls back to the project's parameter record. A code text shared
by two roles runs once.

```json
{"startupTasks": [{"function": "_CitectBegin", "task": 1}, {"function": "ClientStartup", "task": 2}],
 "notes": []}
```

With no page in window 0 yet the body has `startupTasks: []` and `pending: true`; the tasks start
when the first page shows there. Asked again, it returns the same tasks with a note. While the
startup code runs, the simulated clock runs 20 times faster until it settles (`fastStartup:
false` turns this off; the reported clock speed doesn't change).

**`startPage: true`** shows the project's startup page in window 0 before `page` opens and before
`startup: "auto"` runs. The page is the first of `[MultiMonitors] StartupPageScreen1`, `[Page]
Startup` (default `Startup`) that the simulation has, else the runtime's fallbacks `!CSV_Splash`
(when no [MultiMonitors] page was named) and Include's `!Startup`. It opens as a PageDisplay, so a
viewer with `models` gets `ctsim/modelWanted`. A window 0 that already shows a page is left alone.

```json
{"window": 0, "page": "Master_PageMenu1_HD1080", "error": 0, "held": true,
 "source": "[MultiMonitors] StartupPageScreen1 (ParameterGet)", "notes": []}
```

`already: true` when window 0 had a page; `error` 261 with `errorText` when no candidate exists;
`held`: the page waits for its drawing.

### ctsim/tags

`{filter?, regex? = false, equipment?, start? = 0, count? = 500 (1..5000), groups? = false}`,
or `{names: [...]}`, or `{page: "Name", window?}`.

Lists every variable tag of the unit (first definition of a name) and local variable, sorted by
equipment then name, case-insensitively, tags without equipment last.

- `filter`: part of the name or `equipment.item`, case-insensitive; with `*` / `?` it must match
  the whole name. With `regex: true` it's a .NET regular expression (case-insensitive, `^` / `$`
  anchor it). An invalid one, or one slower than 25 ms on a name, fails the request.
- `equipment`: one equipment's tags (`""` = none).

```json
{"total": 3454, "start": 0, "count": 100, "all": 5120,
 "tags": [{"name": "WTP_T201_LT", "equipment": "WTP.T201", "item": "LT", "kind": "variable",
           "project": "ExampleSA", "type": "REAL", "comment": "...", "engUnits": "%",
           "format": "###.#", "engZero": 0, "engFull": 100, "cluster": "Cluster1",
           "value": 42.5, "quality": "good"}],
 "groups": [{"equipment": "WTP.T201", "count": 12}]}
```

Locals have `kind` `local` and an empty `cluster`. `engZero` / `engFull` are numbers, null when
empty, or the text when it isn't a number. `groups` only with `groups: true`. Tag entries can also
carry `locked`, `rawZero`, `rawFull`, `deadband`, `unit`, `address`, `custom1` .. `custom8`, and
for overridden tags `overrides` and `configured` (see `ctsim/setTagProperty`). The listing is
built on the first request.

With `names`: `{"total", "start": 0, "count", "tags": [...], "unknown": [...]}`.

With `page` and / or `window`: the tags the page's code reads (its rows compiled here, else its
.RDB). `window` means the page that window shows; a `page` given with it must match.

```json
{"total": 66, "start": 0, "count": 66, "page": "!PM800Detail", "window": 1,
 "tags": [{TAG ENTRY},
          {"name": "Pump_PV", ..., "via": "ToTW", "ref": "?TotW?"},
          {"name": "Missing1", "defined": false, "kind": "undefined", "type": "DIGITAL",
           "typeFrom": "code", "guess": "DIGITAL", "value": null, "quality": "bad", ...}],
 "unknown": ["Missing1"], "undefined": 1,
 "literals": [{"ref": "?Equip?", "via": "Equip", "value": "Plant.Mixer..."}],
 "unresolved": [{"ref": "?Speed?", "via": "Speed", "error": 323, "errorText": "not associated"}],
 "simulateUndefined": false}
```

- A name the code reads that the project doesn't define has `defined: false`, `kind` `undefined`,
  or `simulated` once setTag created a simulated tag for it. `type` is the type the code
  suggests (`typeFrom: "code"`), else REAL (`"default"`).
- A `?name?` reference is resolved through the window's associations: the tag it points at gets
  `via` (the association) and `ref` (the reference as written). Literal associations go under
  `literals`, missing ones under `unresolved` (323). Without `window` every reference is
  unresolved.
- Associations change with the window; `ctsim/windowsChanged` carries them, and viewers ask again
  when they change.

### ctsim/usedTags `{window, from?}`

The tag names the window's code used at run time (read, written or looked up by name), in first
use order, for the page it shows now: `{window, page, count, names, by}`. `names` starts at index
`from` (default 0), so a client can fetch only new ones. `by[i]` is `{function, file, line}` where
`names[i]` was first used (`function` empty for the page's own expressions and commands).

### ctsim/subscribe `{names?: [...], page?: "Name", window?, replace? = false}`

Body `{"subscribed": 2, "tags": [TAG], "unknown": [...], "page": {"name", "rdb", "tags": 17}}`
(`page` / `window` as in `ctsim/tags`; with a window, `page` also has `window`,
`viaAssociations` and `unresolved` counts). `TAG` =
`{"name": "WTP_T201_LT", "value": 42.5, "quality": "good", "timestamp": "..."}`.

- A different spelling or an Equip.Item gets `"tag"` (the defined name) besides `"name"`.
- Undefined names go under `unknown`, and are subscribed once setTag creates a simulated tag.
- A name with an extension, element or index (`Tag.Q`, `Tag.T`, `Tag.Field`, `Tag[2]`) gets its
  own value whenever its tag changes. Alarm properties (`Alarm.On`, `Alarm.State`) are read every
  batch and sent when they change.

### ctsim/unsubscribe `{names?: [...], all?}`

No names (or `all: true`) removes every subscription. Body `{"subscribed": 0}`.

### ctsim/setTag `{name, value, quality?, element?, index?, type?}`

A device-side write: the Field value, or a named element (`valid`, `override` ...), or an array
element at `index`, with an optional quality name or number. Body `{"error": 0, "tag": TAG}`; on
failure `success: false` with `{"error": 424, "errorText": "Tag not found"}`.

With `simulateUndefined` on, a plain undefined name first gets a simulated tag of `type` (DIGITAL,
INT, LONG, REAL, STRING ...; an unknown word fails), else the type the code suggests, else REAL.

### ctsim/setTagProperty `{name, field?, value?}`

A simulator-only override of a tag or local variable's definition field, for the whole
simulation. `field`: ENG_ZERO, ENG_FULL, RAW_ZERO, RAW_FULL, ENG_UNITS, FORMAT, COMMENT,
DEADBAND, UNIT (the I/O device), ADDR, CUSTOM1 .. CUSTOM8 (any case). `value` is its text (`''`
= blank). A null or missing `value` restores the field; no `field` restores them all.

Everything reads the override at once: TagGetProperty / TagInfo, scaling, display formats. Body:
the tag's entry with `overrides` and `configured`. Viewers get `ctsim/tagDefinitionsChanged
{names}` (events kind `options`). Unknown tags, fields or bad numbers fail.

### ctsim/lockTag `{name, locked? = true}`

A simulator-only lock: while locked, every write but setTag is ignored (the writer sees success)
and logged at most once per tag every 10 s. Tag entries carry `"locked": true`. Body
`{"name", "locked", "value"}`. Unknown tags and alarm properties fail.

### ctsim/options

`{simulateUndefined?, seedTrendHistory?, foregroundBreak?, foregroundErrorBreak?, dll?, sql?,
dllAllowed?, sqlAllowed?, dllTimeout?}`

Simulator-only options for the whole simulation. Body: every option's value. Changes go to
viewers as `ctsim/optionsChanged` (same body). A request with an invalid key changes nothing.

- `simulateUndefined` (default false): setTag on an undefined plain name creates a simulated tag
  that every lookup then finds. Off, undefined names give the runtime's error 424 and bad quality
  (simulated tags keep their values).
- `seedTrendHistory` (default false): samples before the simulation started, or before a pen's
  first value, are that first value, so long-span trends show their whole span at once.
- `foregroundBreak` (default true): the debugger stops page code (expressions, the functions they
  call, page events) like other code, and the simulation waits inside the scan. False gives the
  runtime's rule: foreground code never stops.
- `foregroundErrorBreak` (default false): with `foregroundBreak`, a hardware error in page code
  stops it too. Off, page-code errors are reported once per session and the page runs on.
- `dll`, `sql` (`"off"` default, `"ask"`, `"on"`): whether the simulation may load DLLs (DLLOpen,
  DLLCall, DLLCallEx, DLLClose, the DllClass functions) and reach SQL databases.
  - `off`: calls answer as the runtime does without the thing (DLLOpen -1 / 289, SQLConnect -1 /
    307, missing handles 269) and count as not simulated. Turning a kind off closes what it had
    open.
  - `ask`: the viewer showing dialogs is asked (`ctsim/dialog` kind `permission`) before loading a
    library or connecting to a database not on the allow list, and before any statement that can
    change a database (anything but a plain SELECT / WITH / SHOW / DESCRIBE / EXPLAIN query).
    Answers: `once`, `session`, `always` (adds it to the allow list) or `deny`. Denied, timed out
    (60 s) or no dialog viewer: the call fails as with `off`. Background tasks wait for the
    answer; page code can't, so its call fails at once and the answer applies from the next call.
  - `on`: everything is allowed.
- `dllAllowed`, `sqlAllowed`: the allow lists, each replaced whole. Libraries by full path (as the
  runtime's LoadLibrary would find them: the runtime's Bin folder, the Windows folders, the PATH).
  Databases by identity: DSN / FileDSN, driver or provider, server and database, never user or
  password. A database on the list is trusted for statements too. The functions the simulator
  answers itself (CTUTIL popup menus and a few Windows calls) are never loaded or asked about.
- `dllTimeout` (seconds, default 0 = no limit): a DLL call that takes longer ends the helper
  process and fails as a crash.

SQL runs over ADO.NET as the runtime's data layer does: ODBC unless the connection string's
`SCADA Data Provider` says `OLEDB` or `SQLClient`, with the runtime's handles, errors, `SQLErrMsg`
text and `[SQL]` parameters. The 32-bit net48 ctsim uses 32-bit ODBC drivers and DSNs; the net10.0
build has no providers.

### ctsim/params `{}` and ctsim/setParam `{section, name, value}`

The parameters Cicode has read (ParameterGet) or written (ParameterPut), in first-read order:
`{serial, params: [{section, name, value, default, source, reads, writes, users}]}`. `source`:
`citect.ini`, `_PARAM.RDB`, `param.dbf`, `default`, `ParameterPut` or `simulator`. `users`: up to
8 functions or page objects that used it. setParam writes the simulation's copy of citect.ini
(never the file); `""` restores the project's value.

### ctsim/user `{mode: "full" | "none" | "user", name?}`

Who's logged in: `full` (every privilege in every area), `none` (as the runtime starts), or
`user` (a users.dbf user with their roles, no password). A new simulation starts with `none`.
Body (also hello's `user` and the `ctsim/userChanged` event, sent on every change including
Cicode Login / Logout): `{"mode", "name", "fullName", "roles"}`.

### ctsim/users `{}`

`{"user": {USER}, "users": [{"name", "fullName", "roles"}]}`: the unit's users, by name.

### ctsim/setAlarm `{name, state}`

Set an alarm's state on the alarm server directly. `name`: an alarm tag or `Equipment.Item`.
`state`: a word (`on`, `on_ack`, `off_unack`, `off`, `disabled`) or an object `{on?, ack?,
disabled?, state?, onTime?, time?}`, as the scenario step `setAlarm` takes it (see
`scenarios/README.md`). It holds until one of the alarm's inputs changes. Body `{"alarm": {"tag",
"on", "ack", "disabled", "state", "onTime"?, "time"?}}`. Unknown alarms fail with 424; nothing
changes on failure.

### ctsim/setEquipment `{name, fields}`

Set an equipment's runtime fields `{MODE?, STATE?, DRMODE?}` (any case, values as text). Body
`{"name", "fields": {"MODE", "STATE", "DRMODE"}}`. Failures use EquipSetProperty's codes: 289
unknown equipment, 274 bad field or value.

### ctsim/alarms `{filter?, regex? = false, equipment?, page?, window?, start? = 0, count? = 5000 (0..5000)}`

Every configured alarm in definition order, then the raised hardware alarms. `filter` as in
`ctsim/tags`, over the tag, name and `equipment.item`. `equipment`: that equipment and its
children. `page` / `window`: alarms the page reads, alarms of the equipment its tags belong to,
and alarms whose equipment names the page. Hardware alarms are left out when filtering by
equipment or page.

```json
{"total": 412, "start": 0, "serial": 1187, "alarms": [
 {"tag": "OV08_Fault", "name": "OV08 Fault", "desc": "", "equipment": "Company.TopMilk.Mixing.OV08",
  "item": "Fault", "type": "digital", "category": 1, "priority": 1, "state": "unack_on",
  "on": true, "ack": false, "disabled": false, "onTime": "..."}]}
```

`type`: `digital`, `analog`, `advanced`, `time`, `ts_digital`, `ts_analog`, `multi_digital`,
`argyle_analog`, `double_point`, `hardware`. `state`: `off`, `unack_on`, `ack_on`, `unack_off`,
`disabled`. `onTime` is set while the alarm is listed. Hardware alarms have `tag`
`hardware:<code>`, category 255, priority 0 and a `count`.

### ctsim/equipment `{filter?, regex? = false, start? = 0, count? = 5000 (0..5000)}`

Every equipment instance with its runtime fields as EquipGetProperty answers them:
`{"total", "start", "equipment": [{"name", "type", "MODE", "STATE", "DRMODE", "scheduled"}]}`.
Unless set, an unscheduled equipment answers `Unknown` for all three (as the runtime does); a
scheduled one gives MODE and DRMODE `0` and the scheduler's current STATE. `filter` applies to the
name.

### ctsim/checkExpression `{text, kind? = "expression", project?}`

Compile a page field's text as the page compiler would, running nothing. `kind`: `expression` or
`command`. `project`: the page's project (default the simulation's).

`{"ok": true, "errors": [], "warnings": []}`, or `{"ok": false, "errors": [{"code": 2064, "name":
"E2064", "message": "Function 'TagRea' not found", "line": 1, "column": 1}], "warnings": []}`. A
simulation in compile mode `none` starts its compiler on the first check. `line` and `column` are
1-based in the text (lines end at `\n`; text of one line is line 1), 0 when not known.

`warnings` (only when there are no errors) are `{"kind", "name", "message", "line", "column"}`,
`name` the tag or page named (the literal trimmed), `line` / `column` at the name (a literal's
opening quote), in source order:

- `"kind": "tag"`: `the project does not define the tag X (the runtime raises error 424 for it)`,
  for a name the field takes for a tag, and for fixed text (a string literal) reaching a tag
  argument: TagRead, TagWrite, TagInfo, TagSubscribe ... (`tagArg` in `builtins/behaviour.json`)
  and Ass's tag, also through the Cicode functions that pass a parameter on unchanged (Include's
  AssWin, AssPage, AssPopUp ... through AssVarTags). The name is found as the runtime's lookup
  finds it (`Cluster.Tag`, `Equip.Item`, `Tag.Field`, an array element). For Ass, a `'` literal,
  a dotted name no tag has (an equipment: Ass returns 289) and a trend tag's name are no warning.
  None while `simulateUndefined` is on.
- `"kind": "page"`: `the project has no page X (the runtime cannot display it)`, for fixed text
  reaching a page argument: _PageDisplay, _PageGoto, WinNew, WinNewAt, WinNewPinAt,
  PagePushLast, TrnSelect (`pageArg`), also through Include's PageDisplay, PageGoto, PagePopUp,
  AssWin, TrendWin ... and project functions passing it on. A page counts when the project or a
  project it includes has its compiled .RDB or its rows (as the simulation displays it).
  PageExists and MenuGetPageNode (a menu.dbf page) take no page lookup.

Each name is warned about once.

A parameter counts when the function passes it on as itself before assigning it (Include's
PageDisplay assigns sPageName only on the path that does not call _PageDisplay); a value computed
at runtime (`"Mix_" + x`) is not followed. Compile time only: nothing runs.

### ctsim/checkFunctions `{project?}`

The same page and tag warnings for the Cicode of the functions in the simulation's .ci files: the
root project and the projects it includes (`project`: that project's files only). Every
occurrence is reported (a field's check reports each name once); nothing runs.

```json
{"warnings": [{"kind": "page", "name": "Mixing", "message": "the project has no page Mixing (the runtime cannot display it)",
               "line": 12, "column": 17, "file": "C:\\...\\Example\\mix.ci", "function": "GoMix", "project": "Example"}],
 "errors": [{"code": 2064, "name": "E2064", "message": "Function 'TagRea' not found",
             "line": 3, "column": 5, "file": "C:\\...\\mix.ci", "project": "Example"}]}
```

`file` is the full path, `line` / `column` 1-based at the literal's opening quote, `project` the
file's project (Include and the other libraries are among them: filter by it). `errors` are the
unit's compile errors as the compiler reports them (of `project`, when given). The check reads the
simulation's compiled unit: after an editor changes a .ci file, `ctsim/reload {file}` makes the next
check see the change.

### ctsim/call `{expr, wait? = true, timeoutMs? = 0, window?}`

`expr` is one call with literal arguments, as CtAPI's ctCicode takes it (a user function, its
arguments converted by parameter type, else a built-in). It runs as a background task in display
window `window` (default 0). The response comes when the task ends, after `timeoutMs` (then
`"timedOut": true` and the task runs on), or at once with `wait: false`.

```json
{"task": 4, "state": "stop", "result": "42", "value": "42", "type": "STRING", "error": 0}
{"task": 5, "state": "running", "result": "", "value": null, "type": "", "error": 0, "timedOut": true}
{"task": 6, "state": "halt", "result": "", "value": null, "type": "", "error": 0,
 "haltReason": "built-in function PageDisplay is not simulated", "notSimulated": "PageDisplay"}
```

`result` is the value as STRING (what ctCicode returns); `value` its JSON. A task halted by a
hardware error fails with `error`, `errorText` and `haltReason`. Text that isn't one call fails
before any task starts with 292, 433 or 529.

### ctsim/clock `{mode?: "run" | "pause" | "step" | "speed", value?}`

No mode returns the state. `step` pauses and moves simulated time on by `value` seconds (default
the step size), running tasks as they wake; it answers when done, but runs in bursts so other
requests are still served. `run` ends a step early. `speed` takes `value` > 0. Body `CLOCK`
(plus `"moved"` in ms for a step):

```json
{"mode": "run", "speed": 1.0, "ms": 3000, "t": 3.0, "time": "...", "start": "...", "stepSize": 0.25}
```

Simulated time is whole milliseconds. Running, it follows the wall clock times the speed but
never passes a waiting task's next wake-up. Paused, time stands still but ready code still runs.

### ctsim/scenario `{action: "load" | "run" | "stop" | "save", path?, scenario?}`

- `load`: a `ctsim-scenario/0` file or object.
- `run`: load one if given, then run it with step times relative to now. The end comes as
  `ctsim/scenarioFinished`.
- `stop`: cancel it.
- `save`: the simulation's state as a scenario (clock start = now, window 0's page, every tag
  written since the start with its current value and quality), in the body and, with `path`,
  written there. Paths inside the unit's project folders are refused.

Bodies: `{"loaded", "steps", "description", "project", "running"}`, `{"stopped": true}`,
`{"scenario", "tags", "saved"?}`.

### ctsim/reload `{file, text?}`

Recompile a saved .ci file (or the unsaved `text`) of the unit and swap the program: new calls run
the new code, running tasks keep theirs, and module and global variables keep their values. If
the file's declarations keep their shape only that file is compiled, otherwise the whole unit. A
file with errors leaves the program unchanged, and so does a reload while another source file
doesn't compile.

Body `{"accepted": true, "file", "compiled": true, "incremental": true, "ms", "errors": []}`; when
not compiled, `"compiled": false` with a `"reason"` and the file's own `errors` (`[{code, line,
column, message}]`). After a compiled reload, breakpoints are resolved again and event handlers
follow their functions. Nothing compiles with `--compile none`.

### ctsim/tasks `{}`

`{"tasks": [TASK]}`, `TASK` = `{"task", "name", "function", "state", "foreground"}`, plus
`haltCode` / `haltReason` for halted tasks and `stopReason` for debugger stops. States: Run, Wait,
Sleep, Halt, Stop, Ready, RunExec, Dead.

### ctsim/profile `{action? = "get", top? = 100, tree? = false}`

Where the simulation spends its real time. `start` starts timing, `stop` stops it (keeping
results), `reset` clears it, `get` reports.

```json
{"running": true, "seconds": 12.5,
 "scans": [{"window": 0, "page": "Mixer", "count": 50, "ms": 120.4, "avgUs": 2408.0, "maxUs": 4100.2}],
 "expressions": [{"window": 0, "page": "Mixer", "an": 412, "class": "fill_level", "count": 50, "ms": 30.1, "avgUs": 602.0, "maxUs": 900.0}],
 "expressionsTotal": 840,
 "tasks": [{"name": "_TagSimulateTask", "count": 120, "ms": 8.4}], "tasksTotal": 12,
 "functions": [{"name": "FillLevel", "builtin": false, "file": "...", "calls": 50, "ms": 12.0,
                "selfMs": 7.5, "avgUs": 240.0, "maxUs": 410.0, "callers": [{"name": "(page code)", "calls": 50}]}],
 "functionsTotal": 204}
```

`ms` includes called functions; `selfMs` doesn't. `callers` are the five most frequent: a function
name, `(page code)` or `(task Name)`. Waiting and sleeping time isn't counted. With `tree: true`
the body also has `tree`: each call path with calls, total and own time, a root per origin, each
level costliest first. `kind` is `origin`, `cicode`, `builtin` or `other` (small paths summed).

### ctsim/answer `{id, choice?, ok?, text?, values?, press?, permission?}`

A viewer's answer to a `ctsim/dialog`; the first answer counts.

- popup menu: `choice` = the item's `id` (0, `ok: false` or an invalid id cancels);
- message box: `ok`;
- input box: `ok` and `text`;
- form: `ok` and `values` (field texts in order; missing or null keeps the text), or `press` = a
  button field's `index`;
- permission: `permission` = `once`, `session`, `always` or `deny`.

Body `{"id", "answered": true}`. A dialog no longer shown fails. `ctsim/dialogClosed` follows once
the waiting code takes the answer.

### ctsim/windowImage `{id, bmp?, none?}`

A viewer's picture for a `ctsim/imageWanted` (see Window pictures): `bmp` is a Windows bitmap
file, base64 (24 or 32 bits a pixel, uncompressed or 32-bit BI_BITFIELDS, rows either way up);
`none: true` says the viewer has no picture of the window. The first answer counts. Body `{"id",
"accepted", "width"?, "height"?, "bmp"?}`: the picture's size, and for a `clipboard` request the
recoloured picture (a 24-bit bitmap file, base64) for the viewer to put on the clipboard. A
missing id, a request no longer waiting, or a `bmp` that isn't such a bitmap fails.

### ctsim/shutdown `{}`

Answers, then the server ends and removes its discovery file. Queued messages are delivered first
(waiting up to 3 s for clients that don't read).

## Pages

The page animator runs over the simulation's own VM and scheduler, so page code, commands and
project tasks share one Cicode kernel. Scans, flashing, symbol animation and touch repeat run on
the simulated clock. Window 0 is open with no page at the start; windows are numbered 0 to
`[Page]Windows` - 1.

### ctsim/openPage `{name, window?, model?, associations?}`

Show a page, running its open events in the runtime's order (_PageStart, OnEvent 8 and 4,
ENTRY_FUNC, the first scan). Without `window`: 0 if it's empty, else the lowest free window. A
window that isn't open is opened. `associations` `{name: value}` are stored first, as
`Ass(window, name, value)` would.

`model` is a `ctsim-page/0` page model (from ctgedit's page model export): its drawing summary
(rects, colour states, texts, symbol sets, vertices, trends, metadata) becomes the page's, kept
for later displays. Its rows are compiled and run instead of the .RDB when the model isn't
current, when the .RDB belongs to another version of the page, or when there's no .RDB. A page
with neither a model nor an .RDB runs from the project's own rows if it has them (a simulator
convenience; the runtime needs the compiled page). `source` says which ran: `rdb`, `rows` or
`projectRows`.

```json
{"window": 0, "page": "Mixer", "error": 0, "source": "rows",
 "notes": ["compiled from its rows: 892 records in 210 ms", "AN 12 PGDYNOBJ VISIBLE: E2041 ..."],
 "state": PAGE}
```

`notes` list how the rows compiled and up to 50 fields that didn't. Failures: 261 not compiled and
no rows, 265 no Control record, 274 no name, 277 not in the user's area, 318 nested too deeply,
393 page and .RDB don't match, 517 the window is mid-scan.

### ctsim/navigate `{page, window?, how? = "auto", model?, timeoutMs? = 60000, afterStartup? = true}`

Show a page the way the project's navigation would.

First, with `afterStartup`, it waits (up to `timeoutMs`) for any requested startup code to settle:
window 0's page has started, the startup tasks have ended and at least 1 s of simulated time has
passed, or 10 s simulated have passed with tasks still running, or the clock is paused with every
startup task ended or asleep. The body's `startup` reports `{"state", "waitedMs", "simulatedMs"}`
with state `notAsked`, `settled`, `capped` (with `running`), `paused`, `timedOut` or `notWaited`.

In an SA project it waits until `Workspace_IsSystemInitialised()` (and `Navigation_IsInitialised()`
if present) return 1, then runs `Workspace_ShowContent("<page>")` as a task in `window`, so the
page appears in the pane its content type fits. Other projects, a workspace that isn't ready in
time, or `how: "pageDisplay"` use PageDisplay instead; `how: "workspace"` fails rather than fall
back. `model` is kept for the page's display as in openPage.

```json
{"page": "DefaultAlarm_HD1080", "method": "workspace", "task": 32, "window": 1, "shownIn": [1], "notes": []}
```

`window` is the pane window (-1 if nothing was shown) or the PageDisplay window; `shownIn` lists
the windows showing the page. A failed PageDisplay or a halted Workspace_ShowContent fails with
the body.

### ctsim/closePage `{window}`

Closes the page (_PageStop, OnEvent 5 then 9, EXIT_FUNC, child windows freed, page tasks killed);
windows other than 0 are freed. Body `{"window", "closed", "page"}`.

### ctsim/pageState `{window}`

Every object state of the window's page (`PAGE`, below). An empty window answers
`{"window": 0, "page": "", "serial": 0, "flashOn": true, "objects": []}`.

### ctsim/pageModel `{window, model}`

The drawing summary for the page a window shows (one Cicode opened). The page isn't reopened; its
objects take the drawing at once and keep it for later displays. Refused for an empty window or a
different page. What Cicode already set (metadata, names, tool tips) is kept. Body `{"window",
"page", "objects", "started"?}`. A viewer with no drawing answers `{"window": 3, "none": true}`
and the page starts without one.

### Drawings Cicode needs

When a viewer said `models: true` and Cicode displays a page without a drawing summary (PageDisplay,
WinNew / WinNewAt, AssPopUp, PageBack ...), the engine sends `ctsim/modelWanted {"window", "page",
"path"}` and holds the page's start: it's shown (`hasModel` false) but its open events and scans
wait until a `ctsim/pageModel` arrives or 20 s pass. A held page closed before then starts first,
so its entry code runs before its exit. Pages a viewer already sent a drawing for aren't held.

### Window pictures

WinFile, WinPrint and WinCopy take the picture of the task's window. When a viewer said `images:
true`, the engine sends `ctsim/imageWanted {"id", "window", "page", "function", "xScale",
"yScale", "clipboard"}` to those viewers and the calling task waits for a `ctsim/windowImage`
with that id, at most 30 s of simulated time (a paused clock doesn't time it out). The viewer
draws the window's page as the runtime shows it (the windows shown inside it and its popups, no
selection or overlays), `xScale` by `yScale` times its size. `function` is the built-in that asks;
`clipboard` is true for WinCopy.

The engine recolours the picture (bSwapBlackWhite, the sMap colour map file), then WinFile writes
it as a .bmp into the sandbox, WinPrint as the port's printout (`Printers\<port>-<n>.bmp`), and
WinCopy hands it back in the windowImage body for the clipboard. With no such viewer, no answer
in time, `none`, or when the last such viewer leaves, the call writes nothing and returns 0.

### ctsim/input

`{window, an?, ans?, kind, x?, y?, dx?, dy?, button?, key?, text?, modifiers?, value?, release?}`

Input on the window's page, as the runtime takes it. `an` is the topmost object under the
pointer; `ans` lists every object under it, topmost first, then their groups. Input goes to the
first with a touch command, button or slider (keys: a keyboard command), else to `an`. `x`, `y`
are page pixels (also the window's mouse position).

- `down`, `up`, `click`, `dblclick`: touch commands (down, up, double click, repeat while held)
  and mouse-key keyboard commands. `button`: `left` (default), `right` or `middle`; `modifiers` 1
  Shift, 2 Ctrl. Only a plain left button runs touch commands and grabs sliders; other buttons or
  held modifiers run the matching mouse-key commands (KEY_RBTN_CMD_DN ...).
- `move`: the pointer moved (-1, -1 = left the page).
- `drag`: the left button is down and moving; a slider follows (`dx`, `dy`, or the angle for a
  rotational one) and writes its tag per its mode. `release: true` ends it.
- `slider`: set `an`'s slider to `value` percent.
- `key`: one key, coded as the runtime codes key presses. `key` is the key name (F1, Return, Esc,
  Up, Del ..., a keys.dbf name or a KEY_ label), `text` the character typed, `modifiers` 1 Shift,
  2 Ctrl, 4 Alt. Typed into the window's key command line.

Body `{"ran", "an", "kind"}`. Unknown kinds or keys, or an empty window, fail.

Buttons Cicode drew (DspButton / DspButtonFn) take a plain left press like objects with touch
commands: the press runs the down function, the release the up function, each as a task with the
command cursor on the button. DspButton's keys go into the key command line. A viewer includes the
drawn button's AN in `ans` when the pointer is in its box.

### ctsim/setObjectProperty `{window, an, property, value}`

Write a property of an ActiveX control on the window's page. For ciText.ComboBox, writing
`ListIndex` selects a zero-based row and updates `Text`. An index of −1 clears the selection.
Writing free text selects a matching row when available. Body
`{"window", "an", "property", "value", "changed"}`. An empty window, a non-control
AN, an empty property or missing value fails.

### ctsim/pageVars `{window}`

The page variables the window's code has used since its page showed (PageGetInt / PageSetInt /
PageGetStr / PageSetStr), first use first: `{"window", "page", "vars": [{"name", "type", "value",
"locked"?}]}`. `PAGE` carries the same list as `pageVars`.

### ctsim/setPageVar `{window, name, value, type?, locked?}`

Set a page variable as PageSetInt (`type` `int`) or PageSetStr (`string`) would. Without a type,
the type it was used with, else `int` for whole numbers. `locked: true` makes Cicode's writes
leave it alone (and logs them). Body: the pageVars body after the change.

### ctsim/windows `{}`

The current windowsChanged body.

### Page state

`PAGE` (the pageState body and the pageChanged event body):

```json
{"window": 0, "page": "Mixer", "serial": 12, "scan": 40, "flashOn": true,
 "objects": [{"an": 112, "state": {"an": 112, "shown": true, "disabled": false,
   "disable_style": 5, "move": [0.0, 12.5], "rotate": null, "rotate_origin": null,
   "scale": null, "scale_origin": null, "fill_colour": 65280, "fill_level": 0.42,
   "fill_level_direction": 0, "fill_level_colour": null, "text": "42.5", "symbol": null,
   "symbol_state": null, "vertex_offsets": null, "error": null, "tooltip": null,
   "indication": null, "alarm": null, "trend": null}}]}
```

Field names match ctgedit's `ObjectState` (fields may be added, never renamed). Null means not
animated. Units are page pixels and degrees clockwise; colours are 0x00RRGGBB (high byte 0xFF =
transparent), resolved for the flash phase `flashOn`. `disable_style` 1..5;
`fill_level_direction` 0 up, 1 down, 2 left, 3 right; `symbol_state` the shown state's index.

- `indication`: `bad`, `uncertain`, `error`, `override`, `inhibit` or null (null with
  IgnoreValueQuality 1).
- `alarm`: `{"priority", "state": "unack_on" | "ack_on" | "unack_off" | "none", "colour",
  "flash"}`.
- `trend`: `{period, span, samples, mode, cursor, focus, pens: [{pen, tag, scale: [lo, hi],
  colour, end, values}], markers?}`. `markers` (once DspMarkerMove placed one) =
  `[{"handle", "mode": "vertical" | "horizontal", "colour", "offset"}]`: a vertical marker is a
  line across the trend at `offset` samples from its left edge (0 to `samples`), a horizontal one
  at `offset` / 32000 of its height from the bottom; `colour` 0xRRGGBB, -1 transparent.
- `alarm_list` and `alarm_row`: alarm lists and their rows. Rows have `fontInfo`; lists have
  their own and their rows' `fontInfo`. AlarmListCreate lists also have `headerFontInfo`,
  `drawHeader`, `rowHeight`, `width`, `height` and localised column `titles`; their data rows
  start at row 1.

Trend objects and alarm rows on otherwise unanimated ANs still get a state with just those fields.
`serial` grows with every change of the window, across the pages shown in it; `scan` counts scans.

**Display strings** are localised as the runtime's display client draws them: object `text` and
`tooltip`, drawn text and button captions, plot texts, alarm row text and fields, alarm list
columns, array cells and window titles. Each `@(native)` becomes the current language's text (from the unit's
lang.dbf), else the native text; width and justification suffixes are dropped. What Cicode reads
keeps the markup. Symbol, bitmap, file and URL names aren't localised.

**`fontInfo`** = `{"handle", "name", "face", "height", "pt", "bold", "italic", "underline",
"strikeout", "fg": [on, off], "bg": [on, off], "flash"}`, from the unit's fonts.dbf (first of a
name) and DspFont. `height` is pixels at 96 dpi, `pt` points; colours are 0xRRGGBB, -1
transparent.

**`drawn`** appears once Cicode has drawn on the page (DspText, DspStr, DspSym, DspSymAtSize,
DspSymAnm, DspBar, DspTrend, DspChart, DspBmp, DspFile, DspRichText, DspButton(Fn),
DspWebContentSetURL, DspArrayByAn, PlotClose): the last output of each AN, in first-drawn order:

```json
[{"an": 21, "kind": "text", "x": 40, "y": 50, "text": "hello", "font": 0,
  "args": {"length": 0, "align": 0, "trunc": 0}, "serial": 3}]
```

`kind`: text, sym, symAnm, bar, trend, chart, bmp, file, richText, button, web, array, plot.
`x`, `y` are the AN's position; `args` the call's other arguments by name. The same output again
keeps its serial; DspDel removes an entry; a page change empties the list. The engine never draws
them.

- Entries with a `font` also have `fontInfo`.
- `text`: `args.length` and `args.trunc` are iLength and its mode. Character mode is cut by the
  engine; pixel modes carry the full text for the viewer to cut. `args.align` 0 left, 1 right,
  2 centre.
- `file`: `text` is the file's name (empty until DspFileSetName). `args.height` and `args.width`
  are the window's lines and characters, `args.row` and `args.col` the scroll offsets,
  `args.count` the file's lines. `args.lines` = `[{"an", "x", "y", "text"}]`: the lines the
  window shows, line k at the AN + k (where the page has that AN), cut to the window's width
  from the column offset.
- `richText`: a rich text box (DspRichText), `args.width` by `args.height` pixels down and
  right from the AN. `text` is the loaded file's name (empty until DspRichTextLoad), `args.rtf`
  its RTF ("" when none). `args.mode` is DspRichText's mode, `args.enabled` and
  `args.editable` (1 / 0) the state DspRichTextEnable and DspRichTextEdit set; edits in a viewer
  are not sent back. The engine counts the RTF's paragraphs (`\par`) as its lines: `args.count`
  lines, `args.page` lines a page shows (16 pixels each, a guess); `args.row` is the first
  paragraph shown (its top at the box's top), `args.col` the horizontal offset in characters of
  8 pixels (a guess). The viewer renders the RTF clipped to the box.
- `plot`: a display plot PlotClose showed (PlotOpen(AN, "Display", mode)). `args.mode` is the
  PlotOpen mode, `args.output` its output, `args.prims` the primitives in drawing order, each at
  coordinates relative to the AN's position (pixels, y down):
  - `{"op": "rect" | "ellipse", "x1", "y1", "x2", "y2", "pen", "fill"}`: the box's corners (in
    any order); an ellipse fills its box. `fill` is a colour (-1 none).
  - `{"op": "line", "x1", "y1", "x2", "y2", "pen"}`, `{"op": "point", "x", "y", "pen"}` (a
    point is the pen's width square in its colour).
  - `{"op": "polyline", "points": [x0, y0, x1, y1, ...], "pen"}`: a plotted line.
  - `{"op": "text", "x", "y", "text", "font", "orient", "fontInfo"?}`: the text's baseline
    starts at (x, y) in the font's foreground (and background unless transparent); `orient` 0
    left to right, 1 upwards, 2 right to left (upside down), 3 downwards.
  - `{"op": "marker", "x", "y", "marker", "colour", "size", "symbol"?}`: centred on (x, y),
    `size` pixels across. `marker` 1 triangle, 2 square, 3 circle, 4 diamond, 5..8 the same
    filled; 20..32000 a symbol PlotSetMarker registered: `symbol` is its 'library.symbol',
    drawn with its hot spot at (x, y).

  `pen` = `{"style", "colour", "width"}`: style 0 solid, 1 dash, 2 dot, 3 dash dot, 4 dash dot
  dot, 5 none; a pen wider than 1 draws solid. Colours are 0xRRGGBB, -1 transparent.
- `sym` of a genie placed by DspSym has `args.members`: `[{"an", "handle", "name", "type"}]`, the
  element's records as page objects (the first is the AN itself), so a viewer draws each record
  with its member's state.
- `array`: `cells` = `{"rows", "cols", "layers", "data", "firstRow", "serial"}`, the array at
  the time of the call (up to 500 rows x 64 columns of the value layer, localised). SA ArrayView
  arrays also carry `xOff`, `rowHeight`, `drawHeader`, `rowFont` / `headerFont` with their
  `fontInfo`, `hidden`, `columns` `[{col, field, type, width, title}]`, `rowInfo` `[{row, font?,
  selected}]`, and `meta` for symbol columns (the symbol's state per row).

**`controls`** appears once the page's code looked up an ActiveX control or a viewer wrote to one:
`[{"an", "name", "properties": {...}, "serial"}]`, always in pageState and in pageChanged when
they changed. A ciText.ComboBox entry also includes `items`, its ordered list of strings.
`properties` carries `ListCount`, `ListIndex` and `Text`.

## Events

- **ctsim/tagsChanged** `{"t", "time", "tags": [TAG]}`: once per scan (and right after a setTag
  or clock step), only the client's subscribed tags that changed, with their value at the scan.
- **ctsim/log** `{"level": "error" | "warning" | "info", "text", "kind"?, "code"?, "call"?,
  "tag"?, "function"?, "file"?, "line"?, "task"?, "stack"?, "window"?, "page"?, "an"?,
  "source"?, "halts"?, "haltReason"?}`.
  - `kind`: `hardware`, `task` (a task halted), `message` (TraceMsg, ErrLog, DspError, prompts,
    kernel messages), `reload`, `load`, `startup`, `simulator`, `internal` or `handler`.
  - `code`: a hardware error's number; `call`: the built-in that raised it with its argument
    values; `tag`: the tag a failing read named.
  - `stack`: the task's call stack, innermost first, `[{function, file?, line?, args?}]` (at most
    32).
  - `window`, `page`, `an`, `source`: the page code that ran (`visible`, `touch.down`,
    `page.entry` ...).
  - `halts`: the error halts its task; `haltReason`: why, beyond the code. Viewers should read
    these fields rather than parse `text`.
  - Rate limits: non-halting errors from a background task at most 20 per 10 s per (code,
    function, built-in); from page code once per 10 s per object, and not at all when ErrSet
    suppresses them.
- **ctsim/notSimulated** `{"function", "count"}`: a non-simulated built-in (the task halts) or a
  stub (it returns its documented result) was called; at most once per scan.
- **ctsim/taskStarted** `TASK`, **ctsim/taskExited** `TASK`. At most 100 per second are sent
  individually; the rest of a busy second are summarised in one `ctsim/log` line. Halted tasks
  are always sent.
- **ctsim/clockChanged** `CLOCK`: mode or speed changed, or a step ended.
- **ctsim/pageChanged** `PAGE` with only the objects that changed, at most once per scan, flash
  change or slider drag, for every window.
- **ctsim/windowsChanged** `{"windows": [{"window", "page", "title", "x", "y", "w", "h", "mode",
  "parent", "active", "hasModel", "path", "associations"}]}`: after windows open, close, move or
  change page, or when only their associations change. `parent` -1 = none; `hasModel` false means
  send a pageModel; `associations` maps names to tags (`'text` for literals).
- **ctsim/optionsChanged**: the options body after a change.
- **ctsim/tagDefinitionsChanged** `{names}`: tag properties were overridden or restored.
- **ctsim/userChanged** `USER`: the logged-in user changed.
- **ctsim/alarmsChanged** `{"serial"}`: alarm states changed; at most one per 250 ms. Viewers ask
  `ctsim/alarms` again.
- **ctsim/modelWanted** `{window, page, path}`: see Drawings Cicode needs.
- **ctsim/imageWanted** `{id, window, page, function, xScale, yScale, clipboard}` (to viewers with
  `images`): see Window pictures.
- **ctsim/debugPaused** `{"paused", "task", "function", "file", "line", "reason", "window"?,
  "an"?, "page"?, "source"?}` (kind `debug`): page code the debugger stopped holds the
  simulation, or goes on. While paused, only the debugger's requests are served, the clock
  stands still and no tagsChanged or pageChanged are sent.
- **ctsim/dialog** (to viewers with `dialogs`): `{"id", "window", "task", "kind", "title", "text",
  ...}`.
  - `popupMenu` (DspPopupMenu): `x`, `y` and `items` `[{label, enabled, checked, separator, id,
    bitmap?, items?}]`, where `id` is what DspPopupMenu returns. After DspSetPopupMenuFont,
    `font` contains the same fontInfo object as a text drawing. Popup menus use its typeface, size and style.
  - `message`: `mode` and `buttons`.
  - `input`: `fields` `[{label, value, kind, width}]` and `buttons`.
  - `form` (FormRead): `mode`, `size` [columns, rows], `fields` `[{index, label, value, kind,
    width, height, col, row, maxLength?, items?}]` and `buttons`. A field the code changes resends
    the dialog with the same id.
  - `permission`: `access` (`dll` or `sql`), `action` (`load`, `connect` or `write`), `target`,
    `detail` and `choices`. One question per library, database and action.

  The calling code waits until `ctsim/answer`, the last dialog viewer leaves, or 60 s pass (then
  it's cancelled: 299, or 0 for a menu). File and printer pickers, and every dialog when no viewer
  shows dialogs, get the headless answer (cancelled) or a scenario's. Titles, texts and labels
  are localised.
- **ctsim/dialogClosed** `{id}`: the dialog is gone.
- **ctsim/scenarioFinished** `{"passed", "failed", "skipped", "errors", "end", "failures": [{"t",
  "kind", "target", "want", "got"}], "errorList": [{"t", "step", "code", "reason"}], "stopped"?,
  "description"}`.

## The debugger

A connection whose first request is `initialize` is handed to an `IDapSession`, which gets every
message of the connection and is told when it closes. A `DapContext` gives it the `Simulation`,
the `IDebugTarget`, the output sink, and `HandleCustom` for `ctsim/*` requests. The session is
`CicodeDapSession` in `engine/src/ctsim/Dap/` (the session the VS Code extension uses), supplied through
`DapSessions.CreateSession`; `MinimalDapSession` is the fallback when that file is absent.

When a session ends, the server detaches the target (unless the session already did) and
delivers its last events before letting go of the connection. A new `initialize` arriving while
the old session is closing waits up to 15 s for the handover instead of being refused, so it
neither loses its breakpoints to the old detach nor gets the old session's events. A live
debugger still refuses a second one.

### The DAP session

- **initialize**: capabilities `supportsConfigurationDoneRequest`,
  `supportsConditionalBreakpoints`, `supportsHitConditionalBreakpoints`, `supportsLogPoints`,
  `supportsEvaluateForHovers`, `supportsSetVariable`, `supportsExceptionInfoRequest`,
  `supportsTerminateRequest`, `supportsDelayedStackTraceLoading`, `supportsClipboardContext`,
  `supportsStepBack`, `supportsDataBreakpoints`, and `exceptionBreakpointFilters`
  `[{filter "hardwareError", label "Cicode hardware errors", default true}, {filter
  "pageCodeError", label "Page code hardware errors (simulator)", default false}]`. Nothing stops
  on an error until `setExceptionBreakpoints` arrives.
- **setExceptionBreakpoints**: `hardwareError` turns on BreakOnError, `pageCodeError` turns on
  foreground error stops. Both are given back when the session ends.
- **launch**: refused when `function` is missing, unknown or PRIVATE; when `args` don't fit the
  signature; when `compile` isn't the server's own mode; or when `project` isn't this server's
  unit. `args` are written as Cicode literals by parameter type. A `scenario` is loaded and run,
  then `tags` are set. The call starts once both `launch` and `configurationDone` have arrived;
  `stopOnEntry` stops on its first line. On return the console shows `<function> returned
  <value>`, then `exited` (0) and `terminated`; a halted call writes the hardware error to stderr
  and exits with 1. `keepRunning` keeps the session and adds a `Return value` scope. `noDebug`
  runs without stopping on errors. `foregroundBreak` sets that option for the simulation.
- **attach**: joins the running simulation; refused for another project. Takes `foregroundBreak`
  too.
- **History**: `stepBack` and `reverseContinue` move a stopped task's view back through the
  recording (description `Paused (history)`); `next` / `stepIn` go forward and `stepOut` /
  `continue` return to live. Variables with recorded changes get a child per change.
  `dataBreakpointInfo` gives ids for locals, module and global variables and array elements
  (write access); `setDataBreakpoints` passes conditions on; stops have reason `data breakpoint`.
  `cicode/history {frameId}` returns the frame's changes for inline values: `{cursor, function,
  changes: [{line, name, index, value, old, entry, seq, time}]}`.
- **Page code**: a stop in page code is a thread like any other. That task sends no task events,
  so once it's released and ends the session sends `thread` with reason `exited`. Terminate and
  disconnect don't wait for held page code.
- **Other requests**: `setBreakpoints` (conditions are syntax-checked; one that doesn't parse is
  unverified with the parse message), `threads`, `stackTrace` (paged; frames without a file are
  `subtle`), `scopes`, `variables` (paged, filtered), `evaluate` (watch, hover, repl, clipboard),
  `setVariable`, `continue` (resumes every stopped task, `allThreadsContinued`), `next`, `stepIn`,
  `stepOut`, `pause`, `exceptionInfo` (`exceptionId` "Hardware error <code>"), `terminate`,
  `disconnect` (terminates unless `terminateDebuggee` is false), `source` (error), and every
  `ctsim/*` request.
- **Events**: `stopped` (a hardware-error stop pins frame 0 to the failing line), `continued`,
  `breakpoint`, `thread`, `output`, `terminated`, `exited`. Reported errors go to stderr as
  `Cicode hardware error <code> '<text>' in thread N, <function> at <file>:<line>`.
- Frame paths are shown as the client named the file.
- Requests run in order on the reader thread and target events on the event thread; events wait
  while a request is handled, so `stopped` never overtakes the response that caused it.

### IDebugTarget

Methods may be called from any thread and run on the simulation thread; events come on one event
thread holding no lock. Task ids are task handles; frame ids and variable references are valid
while their task stays stopped. Files match by project folder and file name, case-insensitively.
Events: Stopped, Continued, Parked, BreakpointChanged, ErrorReported, TaskStarted, TaskExited,
Output, Terminated. Variables are paged, with arrays as children. Evaluate takes a context (hover
evaluates names only and changes nothing) and a time budget. SetVariable throws
ArgumentException on bad input.

The runtime's hardware error rule applies while a debugger is attached (from `initialize` until
the session ends):

- a halting error is reported and the task ends, without a stop;
- a non-halting error is reported and the task stops, alive, at its next line;
- ErrSet level above 0 reports nothing;
- foreground code never stops in the runtime. In a simulation `foregroundBreak` is on, so page
  code stops like other code while the simulation waits in the scan; `ctsim/options
  {foregroundBreak: false}` restores the runtime's rule.

With no debugger attached, hardware errors are only logged and non-halting ones let the task run
on. An in-process host attaches with `DebugTarget.Attach()`; setting breakpoints, continuing,
pausing and stepping attach too.
