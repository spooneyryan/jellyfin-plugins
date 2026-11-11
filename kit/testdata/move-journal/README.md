# Move journal fixtures

`moves-v1.jsonl` is a move journal in the format the kit's README describes ("Item identity"), the contract between
Library Maintenance's `MoveJournalWriter` (the writer) and the kit's `MoveJournalReader` (every consumer). Kit.Tests
`Library/MoveJournalContractTests` reads it; Library Maintenance's writer tests are to produce the same shapes.

| Seq | Line | What the reader does |
| --- | --- | --- |
| – | header, `JournalId` `3f2a…`, `CreatedUtc` | the journal's identity |
| 1 | a Primary move of a movie (`a1…` → predicted `b2…`), with `JobId` and `Role` | Committed line |
| 2, 3, 4 | the same job's Version, Part and Extra (a `Trailer`) | Committed lines |
| 5 | the correction predicted `b2…` → actual `c3…`, `Correction: true` | Committed line, chained after 1 |
| 6 | `Status: "Planned"`, a status a later version might write | ignored (counts towards `LastSeq`) |
| 7 | a Move back (`c3…` → `a1…`), another job | Committed line: `a1…` ends where it started |
| 8 | `V: 2`, a line of a later format | ignored (doesn't count towards `LastSeq`) |
| 9 | an episode moved between drives, with an unknown field `Future` | Committed line; the field is ignored |

The ids are patterned for reading (`a1a1…`), not real item ids.
