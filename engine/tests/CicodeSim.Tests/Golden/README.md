# Golden data

Frozen engine outputs that the tests in this folder recompute and compare. A golden file is the
engine's settled behaviour: it changes only with a deliberate behaviour change, and the commit
says what changed and why.

## The records

Each line is one record in a canonical text (`GoldenJson`): compact JSON, object keys sorted by
ordinal, every character outside `' '..'~'` escaped, integers in decimal, and doubles never as
numbers but as `{"r":"<16 hex digits of the IEEE-754 bits>"}` (`{"r":"nan"}` for any NaN). So no
float formatting rule leaks into a golden file. Records hold values with their type codes,
qualities and timestamps, halt and error codes, names, positions and diagnostics. Paths under the
User folder are stored relative to it.

Large outputs are stored as digests: the first 16 hex digits of the SHA-256 of the UTF-8 lines.

## Files in the repository

| File | What | Test | Trait |
|---|---|---|---|
| `scenarios.jsonl.gz` over `scenarios/*.json` | the scenarios on the synthetic install, result and trace event for event | `SyntheticGoldenTests` | light |
| `world_ops.jsonl.gz` over `world_ops_inputs.jsonl.gz` | random API operations on the synthetic install, every result and the trace | `SyntheticGoldenTests` | light |
| `vm_block_cases.json` | 2,000 random self-contained p-code blocks with their results | `Vm/BlockGoldenTests` | (always) |
| `number_format_and_parse_cases.json` | number formatting and parsing: shortest round-trip, '%.15G', %a, printf, strtod, :format | `Vm/NumberFormatTests` | (always) |

`../World/Data` holds the vectors of the text and number rules: `float_text_cases.txt`,
`number_text_cases.txt`, `sin_cases.txt`, `text_number_cases.txt`, `json_parse_cases.txt`,
`cp1252_case_mapping.txt` and `mulberry32.txt`.

## Local golden files

Some goldens are made from the installed projects: the example projects' page blocks
(`page_blocks_examples.jsonl.gz`) and world dumps (`world_Example.jsonl.gz`,
`world_ExampleSA.jsonl.gz`), the library sweep, p-code listing, all page blocks, the front end
over every unit, the world sweep and the compiler probe corpus. They hold AVEVA's project data,
so they're git-ignored and made on each computer with the install (`CICODESIM_GOLDEN_WRITE=1`);
without the install their tests skip.

## Running

Tests over installed projects skip without a Plant SCADA install; the synthetic ones need Windows
(they're frozen with the Universal CRT's sin). `CTG_SKIP_HEAVY=1` skips the heavy ones, or filter
by trait:

```
dotnet test engine/CicodeSim.sln -c Release --filter "Golden=light"
dotnet test engine/CicodeSim.sln -c Release --filter "Golden!=heavy"
```

A failing test names each differing item (function, page block, record key, field) and shows the
golden line next to the current one, then the fields that differ ("fields priority: 0 -> 1").

## Rewriting

After a deliberate behaviour change:

```
CICODESIM_GOLDEN_WRITE=1 dotnet test ... -f net10.0 --filter "FullyQualifiedName~<Test>"
```

writes the test's files into this folder (and fails on purpose). Then run the test again without
it, on both targets, and say in the commit what changed and why.
