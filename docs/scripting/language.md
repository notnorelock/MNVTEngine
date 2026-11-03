# .mnvts Language Reference

`.mnvts` is a small, dynamically typed gameplay language. Source is compiled to bytecode by
`MNVTEngine.Scripting.Compiler.ScriptCompiler`. The bytecode is checked by `BytecodeSerializer.Verify` and run by
`MNVTEngine.Scripting.VM.ScriptVM`. The project `MNVTEngine.Scripting` references nothing. Scripts can only reach
the outside world through host functions that the VM is explicitly given (capability-based API), so they have no
file, process, network, native or reflection access.

## Example

```mnvts
const NEAR = 2.5
var visits = 0

func greet(name) {
    return "Hello, " + name
}

on start {
    log(greet("pilot"))
    after 2 { log("two seconds later") }
}

on event "door_opened"(door) {
    log("opened: " + door)
}
```

Missions (`mission "id" { ... }`) are described in [missions.md](missions.md).

## Lexical rules

- Statements end at a newline (or `;`). Newlines inside `(...)` and `[...]` are ignored, so calls and lists can span lines.
- Comments: `// line` and `/* block */`.
- Numbers are 64-bit floats: `3`, `2.5`. Strings use double quotes with the escapes `\n \t \" \\`.
- Keywords: `var const func return if else while for in break continue wait until event mission objective on true
  false null and or not try catch throw spawn after every requires as`.

## Grammar

```
unit        := { declaration }
declaration := var | const | func | handler | mission
var         := ("var" | "const") NAME "=" expr
func        := "func" NAME "(" [NAME {"," NAME}] ")" block
handler     := "on" "start" block
             | "on" "event" STRING ["(" NAME ")"] block
             | "on" ("complete" | "fail" | "cancel") block          // missions only
mission     := "mission" STRING "{" { metadata | var | const | func | handler | objective } "}"
metadata    := ("name" | "description" | "category") "=" STRING
objective   := "objective" STRING "{" { ("description" "=" STRING | "optional" "=" BOOL | "hidden" "=" BOOL
                                       | "requires" STRING {"," STRING}) } { statement } "}"
block       := "{" { statement } "}"
statement   := var | assignment | expr
             | "if" expr block ["else" (block | if)]
             | "while" expr block
             | "for" NAME "in" expr block
             | "break" | "continue" | "return" [expr]
             | "wait" expr                     // seconds
             | "wait" "until" expr             // re-checked once per tick
             | "wait" "for" "event" expr ["as" NAME]
             | "try" block "catch" [NAME] block
             | "throw" expr
             | "spawn" NAME "(" args ")"       // start a coroutine
             | ("after" | "every") expr block  // one-shot / repeating timer
assignment  := target ("=" | "+=" | "-=" | "*=" | "/=") expr      // target: name, a.b, a[i]
expr        := precedence, low to high: or, and, == !=, < <= > >=, .., + -, * / %, unary (- not), postfix (call . [])
primary     := NUMBER | STRING | true | false | null | NAME | "(" expr ")"
             | "[" [expr {"," expr}] "]" | "{" [key ":" expr {"," key ":" expr}] "}"
```

## Semantics

- **Values:** null, bool, number, string, list, map, range (`0..5` is 0 to 4). Only `null` and `false` are falsy.
  Lists and maps are references. `==` compares numbers, strings and bools by value, and lists and maps by identity.
- `+` concatenates when either side is a string. `and` / `or` short-circuit and return an operand value
  (`name or "default"`).
- **Scope:** top-level `var`/`const` are globals. Variables inside a block are local to it and may shadow outer
  names. Constants cannot be assigned (`MNV3004`).
- **Functions:** fixed arity checked at compile time. Recursion is allowed (bounded by the call-depth limit). Functions
  are not values: call them, or start them with `spawn`.
- **Coroutines:** every handler, objective body, `spawn`ed call and timer runs as its own fiber. `wait`, `wait until`
  and `wait for event` suspend only that fiber.
- **Timers:** `after`/`every` blocks run later, so they may use globals and mission variables but not locals of the
  code around them (`MNV3010`). The interval is evaluated once, when the timer starts.
- **Errors:** runtime errors (type errors, index out of range, division by zero, failing host functions) can be
  caught with `try/catch`. The caught value is the message string, or whatever value was thrown. An uncaught error
  stops only its fiber and is reported with a script stack trace (`at func (file:line)`).
- **Built-ins:** `len str num push keys contains abs min max floor ceil round sqrt clamp random type_of`.

## Execution model and limits

- Compile when a script is imported or changed. The `.mnvts` importer reports diagnostics in the editor.
  `MissionRunner` compiles once when its scene starts, never per frame.
- `ScriptVM.Tick(dt)` runs every due fiber on the caller's thread. A fiber runs until it waits, ends, or uses its
  slice budget (`ScriptLimits.InstructionsPerSlice`, default 100 000). Exceeding the budget faults that fiber
  ("infinite loop?") without stalling the game or the other fibers.
- Other limits: call depth 200, 10 000 stack values per fiber, 1 000 live fibers, 16 resumes per fiber per tick.
- Bytecode (`BytecodeSerializer`, format `MNVB` v1) is validated on load: opcodes, operand ranges, jump targets and
  call arities. Corrupt data throws `InvalidDataException` and is never executed.
- Host functions that a script uses but the host does not provide are rejected when the VM is created
  (`ScriptLinkException`), not when they are first called.

## Debugging

Set `ScriptVM.Debugger` to an `IScriptDebugger`. `BreakpointDebugger` pauses fibers at source lines, can record an
execution trace, and exposes `ScriptFiber.GetStackTrace()` / `GetLocals()`. `ScriptVM.Resume(fiber)` continues a
paused fiber. The editor shows script source with error lines highlighted and a mission outline, but has no
breakpoint UI yet.

## Diagnostics

Each diagnostic is formatted as `file(line,col): error CODE: message`.

| Range | Stage | Examples |
|---|---|---|
| MNV1xxx | Lexer | 1001 unexpected character, 1002 unterminated string, 1003 unterminated comment, 1004 unknown escape |
| MNV2xxx | Parser | 2000-2011, e.g. 2004 unknown metadata key, 2007 invalid assignment target; the parser recovers and reports every error |
| MNV3xxx | Semantics | 3001-3007 duplicates and misplaced hooks, 3004 assign to const, 3008/3009 unknown or circular `requires`, 3010 timer uses an outer local, 3011 undefined name, 3012-3014 bad calls, 3015 unknown API function (warning, import only), 3016 break outside loop, 3017 bad spawn, 3018 API function used without a call, 3019 wrong argument count, 3020 literal type error, 3021 non-literal metadata, 3022 negative wait |

Bytecode verification and link failures are exceptions (`InvalidDataException`, `ScriptLinkException`), not diagnostics: they only happen with corrupt data or a host that is missing functions.
