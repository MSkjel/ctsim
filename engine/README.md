# The Cicode engine

One .NET engine shared by ctgedit and the Cicode VS Code extension.

- `src/CicodeSim.Engine`: the library. RDB reader, p-code lister, VM, compiler (source to the
  runtime's p-code), simulated runtime (tags, clock, users, alarms), built-ins, scheduler, page
  animator and debugger target.
- `src/ctsim`: `ctsim.exe`, the host. A named-pipe server (DAP framing plus `ctsim/*` requests)
  and a small CLI.
- `src/ctsim-dev`: `ctsim-dev.exe`, development commands (p-code listing, compile checks, sweeps).
- `tests/CicodeSim.Tests`: xunit tests. The conformance runners read `../conformance`; the golden
  data in `Golden/` (see `Golden/README.md`) freezes the engine's outputs.

Targets are `net48` (built into Windows 10/11; what ctgedit and the extension ship) and `net10.0`
(Linux and CI). AnyCPU, no NuGet runtime dependencies. The net48 `ctsim.exe` prefers 32-bit, like
the runtime, so maths built-ins give the same digits as the 32-bit Universal CRT.

```
dotnet build engine/CicodeSim.sln -c Release
dotnet test engine/CicodeSim.sln -c Release
engine/src/ctsim/bin/Release/net48/ctsim.exe --version
```

The engine never reads the current culture; `CultureTests` reruns tests under a hostile one, and
`ctsim` pins the invariant culture at start.

## Layout

| Namespace | What |
|---|---|
| `CicodeSim.Values` | `Value` (type, payload, 64-bit quality, three timestamps), lossless Windows-1252, C runtime conversions (`Crt`, `Printf`, `CMath`), correctly rounded float text (`FloatText`), JSON (`JsonValue`) |
| `CicodeSim.Compiled` | compiled programs: the RDB reader, `_FUNCSYM.DBF`, opcodes, the p-code lister, the built-in table |
| `CicodeSim.Vm` | conversions, operators, the interpreter, tasks and scheduler, host interfaces, the debugger seam (`Stepper`, `IDebugTarget`) |
| `CicodeSim.Project` | DBF tables, project topology, tag scope, the compile unit, citect.ini, text and path rules |
| `CicodeSim.World` | one simulated project: tags, equipment, users, alarms, trends, the simulated clock, traces and scenarios |
| `CicodeSim.World.Display` | the display client: windows, pages, ANs, associations, what Cicode draws |
| `CicodeSim.Compiler.Syntax` | lexer, labels, AST, parser |
| `CicodeSim.Compiler.Semantics` | the compile unit, name resolution, diagnostics |
| `CicodeSim.Compiler.Codegen` | code generation, library and RDB writers, page compilation from DBF rows, `LiveProgram` |
| `CicodeSim.Page` | the page animator: open, scan and close in the runtime's order, every animation class, input |
| `CicodeSim.Builtins` | the built-in functions |
| `CicodeSim.Sim` | `Simulation` (one per server), debug target, watch expressions, tag listing, profiler, startup |
| `CicodeSim.Host` | DAP framing, connections, listeners, discovery, `SimServer` |

## Compiled code and the VM

The VM runs CtCmp32's p-code the way the runtime's interpreter does. `Registry` maps built-ins by
FUNC0 index and name; anything unimplemented returns `Value.NotSimulated`. `ProgramBuilder`
assembles p-code for tests.

## The simulated runtime

A `World` holds one project's tag database (`TagDb`: variable tags, local variables,
Equip.Item references, quality and timestamps), equipment, users, the alarm server (`AlarmDb`),
trends and the simulated clock (`SimClock`, whole milliseconds). `TagDb` is the VM's tag access
and `SimClock` its clock. `WorldRun` runs a project's `_LIBRARY.RDB` over its World.

## The compiler

The front end follows CtCmp32 2023 R2: its tokens, precedence, error recovery and message order.
Nesting is capped at `Parser.MaxNesting` (150, F4008), and left-leaning chains are walked in loops,
so deep input can't overflow the stack.

The code generator reproduces CtCmp32's output: the example projects compile to their
`_LIBRARY.RDB` and `_FUNCSYM.DBF` byte for byte. `SourceLibrary` keeps a warm compiler so one
edited file recompiles alone, and `LiveProgram` is what a simulation runs: the RDB, everything
from source, or only changed files. A reload never takes code from a file that doesn't compile,
and module and global variables keep their values across it.

## Built-ins

Each built-in is a static method with a `[Builtin("Name", ...)]` attribute describing its
behaviour. `ctsim-dev builtins-behaviour --out builtins/behaviour.json` regenerates
`builtins/behaviour.json` from them, and a test fails when it's stale.

Notes on a few families:

- **Blocking built-ins** refuse foreground code (282), as in the runtime.
- **Files** go through a sandbox: real files under the project folders are read-only; writes go
  to a sandbox folder or memory, never to the real file. Paths that could escape the sandbox are
  refused.
- **Forms and dialogs** go to `SimHost.Interaction` (ctgedit, VS Code, a test or a scenario).
  Headless runs cancel every dialog.
- **DLLs and SQL** are off by default. When allowed, DLLs run in a helper process (`ctsim
  dllhost`) and SQL is a port of the runtime's data layer over ADO.NET.
- **DDE** conversations run entirely in the simulation. To model an application named Excel,
  call `ParameterPut("SimulatedDDE", "Excel", "1")`. `DDEWrite` and `DDERead` share values by
  application, document and item. Conversation handles also support buffered line reads and
  writes. Execute requests appear in the simulation log. With `[DDE]Enable=1`, `DDEPost`
  publishes read-only items on `Citect|Data`.
- **Serial ports** use the project's Boards and Ports tables. For a simulated cable, set
  `ParameterPut("SimulatedSerial", "PortA", "PortB")` and the reverse mapping, then open both
  endpoints. For a loopback test, give the same name on both sides. Reads and writes use
  the Cicode buffer and length arguments. Timeouts use the simulation clock.
- **The outside world** (ActiveX, CiVBA, devices, printing, reports and so on) is
  stubbed to answer as the runtime does when the thing isn't there. Stubs are counted and
  reported as not simulated.
- **Users** use an in-memory copy of users.dbf with the runtime's password rule; nothing is
  written back.

`CICODESIM_EXCLUDE_BUILTINS=a,b` leaves named built-ins out, for bisecting.

## The page animator

`Animator` opens, scans and closes pages in the runtime's order (page events, entry and ready
tasks, every object's animation, input handling), over the simulation's VM so page code and
project tasks share globals, event handlers and the task table. It isn't thread-safe: drive it on
the VM's thread. Pages come from their compiled `.RDB`, or are compiled from their DBF rows when a
viewer's page model says the RDB is stale. Output is `PageState` / `ObjectState`.

## The server

The protocol, discovery file and process modes are documented in `PROTOCOL.md`.

- `ctsim serve --project <dir>` runs one simulation behind `\\.\pipe\ctsim.<id>` (current user
  only), with any number of `ctsim/*` viewers and one DAP debugger. Off Windows it uses a Unix
  socket.
- `ctsim dap --project <dir>` runs a DAP session on stdio, for the VS Code extension's
  debugger. The DAP layer is in `src/ctsim/Dap/`.
- `ctsim servers` lists running servers.

Each `Simulation` runs on its own thread with a command queue; events are raised on a separate
thread, never under the simulation's lock.

## Development commands

`ctsim-dev` has the commands used to check the engine against real projects, including
`pcode`, `run`, `sweep`, `world-check`, `world-dump`, `world-run`, `world-sweep`, `parse`,
`funcsym`, `compile`, `compile-check`, `compile-pages`, `page-run`, `page-sweep` and `page-case`.
Run it without arguments for the full list.
