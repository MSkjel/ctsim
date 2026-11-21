# ctsim

A Cicode simulator for AVEVA Plant SCADA. The .NET engine in `engine/` runs a project's Cicode
and pages without the Plant SCADA runtime, with simulated tags, alarms, trends and a clock.

It is used by:

- [ctgedit](https://github.com/MSkjel/ctgedit), whose Simulate window shows a page live with
  every tag it reads;
- a VS Code extension that runs and debugs Cicode against simulated tags.

| Path | What |
|---|---|
| `engine/` | The engine library, the `ctsim.exe` host and the tests. See `engine/README.md`. |
| `engine/PROTOCOL.md` | The protocol clients speak to `ctsim.exe`. |
| `builtins/behaviour.json` | What each built-in function does in the simulator: implemented, stubbed or refused. |
| `scenarios/` | The scenario format and shared scenarios. |
| `traces/` | The trace format. |
| `conformance/` | Cases recorded on the real compiler and runtime, which the tests replay. |

## Build and test

```powershell
dotnet build engine/CicodeSim.sln -c Release
dotnet test engine/CicodeSim.sln -c Release
```

Tests that need an installed Plant SCADA skip without one. Set `CTG_SKIP_HEAVY=1` to skip the
long ones.

CI checks formatting with CSharpier: `dotnet tool restore`, then `dotnet csharpier format .`.

The version is the newest `vX.Y.Z` tag; a build of a later commit takes the next patch number,
and a checkout without tags is 0.0.0. To release, push a `vX.Y.Z` tag on a commit of main. The
release workflow checks that CI passed on that commit (waiting for it if it is still running),
then publishes `ctsim.exe` (net48) as a GitHub release.

## License

Copyright (C) 2026 MSkjel

This program is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version. See [LICENSE](LICENSE).

AVEVA, Plant SCADA and Citect are trademarks of their owners. This project is not affiliated with
or endorsed by AVEVA.
