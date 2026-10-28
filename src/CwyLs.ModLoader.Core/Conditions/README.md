# Core/Conditions — the `when` contract

Pure, Unity-free contract for pack conditions: the grammar, the token table, the registry and the
"not available" evaluator. The evaluator itself (token semantics, midnight wrap, invariant parsing),
the game-side provider, the `cwy.if` command, console `cond eval|tokens|override` and the
author-facing value vocabularies plug into it.
Consumers: `when` on EditData patches, triggers (`data/triggers.json`, planned), re-evaluation per
`UpdatePoint` on reload, mods (`helper.Conditions`).

## Grammar (`ConditionParser`)

```
when        = JSON object; every member must hold (AND); absent or {} always holds
key         = token [ ":" argument ]            token = name | modId "/" name   (case-insensitive)
              name     = letter { letter | digit | "_" }   (≤ 32)
              argument = 1..64 of letters, digits, "_ . -"      e.g. flag:metSatone
value       = string | number | boolean | array of those
string      = [ "!" ] alternative { "," alternative }    a leading "!" negates the WHOLE list
array       = [ alternative, ... ]   one alternative per item, commas not split, no "!"
alternative = literal | low ".." high | (">=" | "<=" | ">" | "<") operand
```

- `{"weekday": "Sat, Sun", "hour": "22..23"}` — Saturday or Sunday, and 22:00-23:59.
- `{"weekday": "!Sat, Sun"}` — neither Saturday nor Sunday. `"Sat, !Sun"` is a syntax error (the
  classic "not a OR not b is always true" trap is refused).
- `{"time": "22:00..06:00"}` — a range may run backwards; wrap-around is the evaluator's rule.
- `{"date": "2026-12-24"}` is a literal (the range separator is `..`, so hyphens are free).
- `{"level": ">=5", "flag:metSatone": true, "my.mod/mood": "happy"}`.
- Numbers and booleans become invariant text (`5`, `12.5`, `true`).
- Errors name the key and the rule; `TryParse` reports every problem, not only the first.

## Types

| Type | Role |
|---|---|
| `ConditionSet`, `ConditionClause`, `ConditionAlternative`, `ConditionOperator` | the parsed, immutable block |
| `ConditionParser` (`TryParse`, `Parse`, `Read` → `ConditionParseResult`, `TryParseClause/Key/Alternative`) | the grammar |
| `ConditionTokenInfo`, `ConditionValueKind`, `ConditionArgumentRule` | one token: name, value kind, argument rule, update points |
| `ConditionTokens` | the 13 built-in names (`time hour weekday date timeOfDay pomodoro language scenery story level hasPack flag random`) |
| `UpdatePoint` (flags) | when a token's value can change; `UpdatePoint.All` for unknown tokens |
| `IConditionSource` | supplies a token's current values (invariant text); false = not known yet |
| `IConditionEvaluator`, `UnavailableConditionEvaluator` | answers a set; the stub says `Met` for an empty set and `Unavailable` otherwise |
| `ConditionResult`, `ConditionOutcome` | `Met` / `NotMet` / `Unavailable` / `Invalid` + reason; **only `Met` means apply** |
| `ConditionRegistry` (`Shared`) | tokens (framework = bare names, a mod = `<modId>/<name>`, never replaced), the evaluator slot (`UseEvaluator`), `Evaluate` (guarded), `Validate` (unknown token, argument rule), `UpdatePointsOf`, `Subscribe`/`RaiseChanged` |
| `ConditionsChanged` (`ICwyEvent`) | "conditions may answer differently": one `UpdatePoint` + detail |

## Wiring the evaluator

`ConditionRegistry.Shared.UseEvaluator(evaluator)`; `Register("cwy", ConditionTokens.BuiltIn[i], provider)`
for each built-in; `RaiseChanged(new ConditionsChanged(point, detail))` from the Bulbul provider;
`ErrorSink` → the BepInEx log. Tests: `ConditionsParserTests`, `ConditionsRegistryTests`.
