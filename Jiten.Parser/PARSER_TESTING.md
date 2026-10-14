# Parser Pipeline and Testing

Reference for parser work. The `/fix-misparses`, `/add-user-dictionary` and `/scan-segmentation` commands build on it.

## Japanese Text Processing Pipeline

1. **Morphological Analysis** (`Jiten.Parser/MorphologicalAnalyser.cs`): Sudachi tokenises text → WordInfo objects (Text, DictionaryForm, PartOfSpeech, NormalizedForm)
2. **Deconjugation** (`Jiten.Parser/Deconjugator.cs`): Applies rules from `deconjugator.json` → possible base forms with conjugation history
3. **JMDict Lookup** (`Jiten.Parser/Parser.cs`): Queries Lookups table → matches by POS compatibility → priority scoring for ambiguous matches
4. **Result** (`Jiten.Core/Data/DeckWord.cs`): WordId, ReadingIndex, OriginalText, Conjugations, Occurrences

**Caching**: Redis-backed word cache by (Text, POS, DictionaryForm) tuples. JMDict cache populated in 10K batches. Cache failures are non-fatal. **You MUST flush Redis after parser changes** (`dotnet run --project Jiten.Cli -- --flush-redis`).

## Autonomous Parser Testing

**Test commands:**
```bash
dotnet run --project Jiten.Cli -- --parse-test "飲んだから"                    # Single input diagnostics
dotnet run --project Jiten.Cli -- --parse-test "食べている" --parse-test-output diag.json
dotnet run --project Jiten.Cli -- --run-parser-tests                          # Batch segmentation tests
dotnet run --project Jiten.Cli -- --run-parser-tests --parse-test-output failures.json
dotnet run --project Jiten.Cli -- --run-form-tests                           # WordId/ReadingIndex correctness tests
dotnet run --project Jiten.Cli -- --run-form-tests --parse-test-output failures.json
dotnet run --project Jiten.Cli -- --deconjugate-test "飲んだ"                   # Show all deconjugation forms
dotnet run --project Jiten.Cli -- --deconjugate-test "食べさせられた" --parse-test-output deconj.json
```

**Database search (for diagnostics):**
```bash
dotnet run --project Jiten.Cli -- --search-word 2084700        # By WordId
dotnet run --project Jiten.Cli -- --search-word "そうする"      # By reading
dotnet run --project Jiten.Cli -- --search-lookup "そうする"    # Lookups table
```

**Diagnostic JSON output contains**: `sudachi.tokens` (raw Sudachi analysis), `sudachi.rawOutput`, `tokenStages` (processing stages with modifications), `results` (final parsed tokens), `formScoring` (per-token scoring breakdowns showing all evaluated (word, form) candidates with component scores: WordScore, FormPriorityScore, FormFlagScore, SurfaceMatchScore, ScriptScore).

**Failure types:**
1. **OverSegmentation** - Tokens split that should be combined → check `sudachi.tokens` and `tokenStages` → fix: add to `SpecialCases2`/`SpecialCases3`
2. **UnderSegmentation** - Tokens merged that should be separate → check for `"type": "merge"` in modifications → fix: add exclusion in Combine* method or `PreprocessText()`
3. **TokenMismatch** - Content differs → usually POS misclassification or wrong dictionary form

**Key files for parser fixes:**

- `MorphologicalAnalyser` is a partial class: `SpecialCases2`/`SpecialCases3` (hardcoded token combinations) in `Jiten.Parser/Helpers/MorphologicalAnalyser.RuleData.cs`, `Combine*` methods (merging logic) in `Stages/MorphologicalAnalyser.CombineStages.cs`, `RepairNTokenisation()` (ん-form fixes) in `Stages/MorphologicalAnalyser.RepairStages.cs`, `PreprocessText()` (forced splits) in `Helpers/MorphologicalAnalyser.TextProcessing.cs`. `MisparsesRemove` (tokens to filter) is in `Jiten.Parser/Parser.cs`
- `Jiten.Parser/Stages/MorphologicalAnalyser.RewriteRules.cs`: declarative token-rewrite table + engine. **A new single-surface lexical fix (pin a WordId, or split/merge a specific surface into fixed tokens) should be a `RewriteRule` row here, not a hand-rolled `if`-block.** The engine owns offsets, readings-from-templates, cloning, and pin/conjugation recovery. Phases: `Cleanup` (runs before `ApplyContextPins`), `Late` (mora-theft position), `Early` (ProcessSpecialCases position). A rule pin is soft by default (lookup-time compound matching may absorb the token into a longer attested span); set `HardPin` when the pin is a final word decision that compound matching must not swallow. Add a new pipeline **stage** only for a pattern-general mechanism, never a single lexical pattern. Context-heavy logic (wider/dynamic window scans, dynamic readings, POS-section edits, multi-token mutation, order-dependent-on-another-block) legitimately stays as code.
- `Shared/resources/deconjugator.json`: Rule-based deconjugation (~1500 rules). Types: `stdrule`, `rewriterule`, `onlyfinalrule`, `neverfinalrule`, `contextrule`. Fields: `dec_end`/`con_end` (endings), `dec_tag`/`con_tag` (grammar tags). Search for specific endings or `"detail": "past"` etc.
- `Shared/resources/user_dic.xml`: Custom Sudachi dictionary entries. Format: `surface,leftId,rightId,cost,display,pos1,pos2,pos3,pos4,conjType,conjForm,reading,normalised,dictFormId,splitType,splitA,splitB,unused`. Regenerate after editing: `sudachi ubuild "Y:\CODE\Jiten\Shared\resources\user_dic.xml" -s "S:\Jiten\sudachi.rs\resources\system_full.dic" -o "Y:\CODE\Jiten\Shared\resources\user_dic.dic"`

**Autonomous fix workflow:**
1. `--run-parser-tests` → identify failures
2. `--parse-test "input"` → full diagnostics per failure
3. Analyse `sudachi` and `tokenStages` to identify cause
4. Apply fix: Sudachi issue → `user_dic.xml`/`PreprocessText()`; missing combination → `SpecialCases2/3`; wrong merge → Combine* method; deconjugation → `deconjugator.json`; word matching → `TryLookupUsableCompound`/`GetBestReadingIndex` in `Parser.cs`
5. **Flush Redis** with `--flush-redis`
6. Re-run failing test, then full suite for regressions

**Reader parse version:** saved reader chapters carry `ParseVersion` (`Jiten.Parser/ParseVersion.cs`). Bump `ParserRevision` only for segmentation changes readers should pick up, since every bump re-parses each chapter a reader opens. JMdict/JMnedict syncs and moved-form migrations bump the data epoch themselves; the API picks it up on restart after the Redis flush.

