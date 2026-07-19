# CnCBridge ↔ C# integration contract

This is the complete contract for the **C# CnC proxy** that drives CnCBridge.
It is written so you can build (or update) the C# bridge wrapper end to end
without reading the Lua. If you only skim one doc, skim this one.

- **Chain:** `isle → CnCBridge (Lua plugin) → C# proxy → IsleMicroservice`.
- **Transport:** the filesystem only. The plugin never opens a socket. The C#
  side owns all networking and reaches the files over its own channel (mount /
  SSH / local).
- **On-box location:** `TheIsle/Binaries/Win64/Mods/CnCBridge/Saved/`
  (the server's working dir is `Binaries/Win64/`). `UE4SS.log` is at
  `Binaries/Win64/UE4SS.log`.
- **Build target:** The Isle EVRIMA **0.21.720** (skin-overhaul patch).
- **Plugin version reported by `ping`:** `v002`.

---

## 1. The five streams

All streams are **NDJSON**: one JSON object per line, `\n`-terminated, UTF-8.

| File | Direction | You (C#) | Plugin | Contents |
|---|---|---|---|---|
| `inbox.ndjson`   | **you write** | append commands | drains + deletes | commands in |
| `results.ndjson` | you read  | tail | appends | per-command ack + read data |
| `events.ndjson`  | you read  | tail | appends | join / leave / death |
| `chat.ndjson`    | you read  | tail | appends | live in-game chat |
| `stats.ndjson`   | you read  | tail | appends | gated periodic snapshots |

### Writing `inbox.ndjson`

Append one command object per line, then a `\n`. The plugin renames the file to
`inbox.ndjson.processing` while it drains, so **you can keep appending** to a
fresh `inbox.ndjson` at any time — never truncate or rewrite it, only append.
The plugin deletes the stash after processing.

### Tailing the out streams (offset + shrink-reset + `.old` drain)

Track a byte offset per file. Each poll:

1. `stat` the file. If its size **decreased** since last poll, the plugin
   rotated it: it renamed `<file>` to `<file>.old` and started fresh. To avoid
   losing the tail of the rotated data, **drain `<file>.old` from your last
   offset to its end first**, then reset your offset to `0` and continue on the
   new `<file>`.
2. Read from your offset to EOF, append to a line buffer, advance the offset.
3. Split the buffer on `\n`; parse each complete line; keep any partial trailing
   line for the next poll.

Rotation is **single-generation** (`rotateMaxBytes`, default 50 MB): only one
`.old` exists at a time. Poll interval of ~500 ms–1 s is plenty.

---

## 2. Command envelope (what you write)

```json
{"id":"<uuid>","ts":1779000000,"verb":"teleport","steam":"76561198XXXXXXX","args":{"x":12345,"y":67890,"z":22500}}
```

| field | required | meaning |
|---|---|---|
| `id`   | **strongly recommended** | your correlation id. Echoed on every result. Also the dedup key — see §4. A command with no `id` still runs but you get no reliable ack correlation and no double-apply protection. |
| `ts`   | optional | your unix-seconds timestamp. Informational; the plugin does not staleness-filter on it. |
| `verb` | **required** | one of §6. Missing verb → `BAD_JSON` result (if `id` present). |
| `steam`| required for player verbs | target Steam64. Not needed for `players` / `stats` / `ping`. |
| `args` | verb-specific | nested objects are fine (parsed with balanced-brace matching). |

Use a fresh `id` per logical command. Reusing an `id` within `dedupTtlSeconds`
(default 300 s) is treated as a redelivery and rejected with `DUPLICATE`.

---

## 3. Result envelope (what you read from `results.ndjson`)

Exactly one result line per accepted command (two, if you also count a
`DUPLICATE` for a redelivery). Async verbs (`setmutations`) emit theirs ~0.5 s
later.

```json
{"id":"<uuid>","ts":1779000001,"verb":"teleport","steam":"76561198XXXXXXX","ok":true,"code":"OK","msg":"teleported ...","data":{"pos":{"x":12345,"y":67890,"z":22500}}}
```

| field | meaning |
|---|---|
| `id`   | echoes your request id (`""` if you sent none). |
| `ts`   | server unix seconds when the result was written. |
| `verb` | echoes the request verb. |
| `steam`| echoes the request steam. |
| `ok`   | boolean. |
| `code` | machine-readable enum, table below. Branch on this, not on `msg`. |
| `msg`  | free-form human string (for logs). |
| `data` | present only for read verbs / verbs that return a payload. |

### Result `code` enum

| code | ok | meaning / C# action |
|---|---|---|
| `OK`             | true  | applied. |
| `BAD_JSON`       | false | line lacked a `verb`. Fix the producer. |
| `UNKNOWN_VERB`   | false | verb not implemented on this plugin version. |
| `BAD_ARGS`       | false | missing/invalid `args` for this verb. |
| `TARGET_OFFLINE` | false | no controller for that steam (not connected). |
| `NO_PAWN`        | false | connected but no live dino (spawn-select / mid-respawn). Retry shortly. |
| `NOT_WHITELISTED`| false | `swap` species not in `allowedClasses`. |
| `RATE_LIMITED`   | false | per-`(steam,verb)` minute cap hit (if enabled). Back off. |
| `DUPLICATE`      | false | this `id` was already processed. Not an error — a redelivery was ignored. |
| `IN_PROGRESS`    | false | a `swap` for this steam is already running. Retry after ~1 s. |
| `APPLY_FAILED`   | false | the engine call did not take (e.g. teleport collision, absent setter). |
| `CRASH`          | false | the handler threw (pcall-caught). Inspect `msg` / `UE4SS.log`. |

---

## 4. Correlation, sync vs async, timeouts, idempotency

**Correlate by `id`.** Keep a `Dictionary<string, PendingCommand>` of ids you
sent with a TaskCompletionSource / callback. When a `results.ndjson` line
arrives, look up `id`, complete it, remove it.

**Sync vs async.** All verbs return a single result line. Most return it on the
plugin's next 1 s poll tick after your write lands. **`setmutations` is async**:
its result arrives ~500 ms after the command begins (deferred field-write, per
`EVRIMA_QuestMutation_Fix.md`). Treat all verbs uniformly with a timeout — you
don't need to special-case async.

**Timeouts are the C# side's job.** End-to-end latency is your inbox-write
propagation + the plugin's `inputPollSeconds` (default 1 s) + apply time. A
**5 s** correlation timeout is a safe default; give `setmutations` and `swap`
~8 s. On timeout, surface a failure to the microservice — do **not** silently
resend (see idempotency).

**Delivery is at-least-once.** The inbox survives a crash on either side, so a
command can be redelivered. The plugin dedups by `id` (ring, `dedupTtlSeconds`),
so a redelivered destructive verb (`swap`/`teleport`/`kill`) will **not**
double-apply — you get a `DUPLICATE` instead. Two rules for the C# side:

1. **Reuse the same `id` when you retry the *same* logical command** (so the
   plugin's dedup protects you). Use a **new** `id` only for a genuinely new
   command.
2. **Make your own result handling idempotent** — you may see a result for an id
   you already completed (e.g. after a C# restart re-tails `results.ndjson` from
   an old offset). Ignore results for unknown/closed ids.

---

## 5. `events.ndjson` — join / leave / death

```json
{"ts":1779000000,"kind":"join","steam":"76561198XXXXXXX"}
{"ts":1779000000,"kind":"leave","steam":"76561198XXXXXXX"}
{"ts":1779000000,"kind":"death","steam":"76561198XXXXXXX","species":"/Game/.../BP_Tyrannosaurus.BP_Tyrannosaurus_C","pos":{"x":1,"y":2,"z":3}}
```

- `join` / `leave` are **reliable** (driven by the presence registry). `join`
  fires the first time a steam is seen (connect, or first heartbeat after a
  plugin reload for already-connected players). `leave` fires on eviction.
- `death` is **best-effort and lagged**. There is no Lua-reachable death event
  (`EVRIMA_KillFeed_Design.md`), so the plugin detects HP `>0 → 0` on the stats
  tick — up to one `statsIntervalSeconds` of lag, and fast corpse cleanup or
  environmental deaths between ticks can be missed. Enable with `emitDeaths`.
  For exact kill attribution, use a dedicated KillFeed mod / RCON, not this.

### The skin-reapply flow (important)

Direct-write skins **revert on relog** (the engine rebuilds the dino from its
persisted `SkinCode`, which the plugin cannot write). **The C# side owns skin
persistence and reapplication.** The pattern:

1. C# stores the desired customizer per steam (its own DB).
2. On a `join` event, wait **~4 seconds** (let the engine finish its own skin
   load), then send `setskin` with the stored customizer.
3. Also reapply on server boot for everyone already online (send `players`, then
   `setskin` each).

---

## 6. Verb reference

Every player verb resolves the target fresh via the presence registry; a target
that is connected-but-on-spawn-select returns `NO_PAWN` (retry shortly).

### `swap` — live species swap in place
```json
{"id":"1","verb":"swap","steam":"765...","args":{"species":"Tyrannosaurus","growth":1.0}}
```
- `args.species`: short name (`"Tyrannosaurus"`) resolved via
  `speciesClassTemplate`, or a full class path (detected by `/`).
- `args.growth`: optional `[0.01,1.0]`; omitted → old dino's growth carried over.
- Whitelisted against `allowedClasses` (a non-playable class instantly crashes
  the target's client). Full paths need `allowUnwhitelistedPaths:true` to bypass.
- Codes: `OK`, `NOT_WHITELISTED`, `TARGET_OFFLINE`, `NO_PAWN`, `IN_PROGRESS`,
  `APPLY_FAILED`, `CRASH`. `data`: none.
- Note: **gender is not carried** (no setter) — a female player's dino may come
  back male.

### `setstats` — growth + vitals + prime, atomically
```json
{"id":"2","verb":"setstats","steam":"765...","args":{"health":9000,"stamina":700,"growth":0.75,"prime":true}}
```
- `args` may contain any subset of: `growth` (applied first, clamped
  `[0.01,1.0]`), `health`, `stamina`, `hunger`, `thirst`, `oxygen`, `food`,
  `blood`, `prime` (bool, applied last). Ordering matters — `SetGrowth` refills
  GAS vitals (Safety Rule 8), so growth is applied before vitals.
- `data`: `{"applied":["growth=0.750","health=9000.0","prime=true"]}`. A field
  the engine rejected is tagged `(FAILED)` in the string.
- Codes: `OK`, `TARGET_OFFLINE`, `NO_PAWN`, `BAD_ARGS` (no recognized fields).

### `prime` / `unprime`
```json
{"id":"3","verb":"prime","steam":"765..."}
```
- Forces (all 10 condition flags + cached bool) or clears prime eligibility.
- **`unprime` may not stick** on a fully-grown dino (engine locks prime at 75%
  growth). Codes: `OK`, `TARGET_OFFLINE`, `NO_PAWN`, `APPLY_FAILED`.

### `teleport` — move to engine coords  *(VERIFY-ON-BOX)*
```json
{"id":"4","verb":"teleport","steam":"765...","args":{"x":12345,"y":67890,"z":22500,"yaw":90}}
```
- `args`: `x`,`y`,`z` required numbers; `yaw` optional (keeps current yaw if
  omitted). Uses `K2_TeleportTo`.
- `data`: `{"pos":{"x":..,"y":..,"z":..}}` (post-move position for confirmation).
- Codes: `OK`, `BAD_ARGS`, `TARGET_OFFLINE`, `NO_PAWN`, `APPLY_FAILED`
  (`K2_TeleportTo` returned false — likely collision).
- Least-documented verb; confirm behavior live before relying on it.

### `getpos`
```json
{"id":"5","verb":"getpos","steam":"765..."}
```
`data`: `{"pos":{"x":..,"y":..,"z":..},"rot":{"pitch":..,"yaw":..,"roll":..}}`.
Note the HUD shows Y as latitude, X as longitude — convert consumer-side.

### `getstats` — full snapshot
```json
{"id":"6","verb":"getstats","steam":"765..."}
```
`data` (same shape as one `stats.ndjson` line):
```json
{
  "steam":"765...","ts":1779000000,
  "species":"/Game/.../BP_Tyrannosaurus.BP_Tyrannosaurus_C","growth":1.0,
  "pos":{"x":1,"y":2,"z":3},
  "vitals":{"hp":9350,"hpMax":9350,"hunger":50,"hungerMax":100,"thirst":40,"thirstMax":1000,
            "stamina":778,"staminaMax":778,"food":600,"foodMax":600,"oxygen":776,"blood":9350,
            "lockedDamage":0,"rottenValue":1800,"waterLevel":-1022646.93},
  "nutrients":{"carb":0,"protein":0,"lipid":0,"bones":0,"cannibal":0,"magy":0,
               "rottenFlesh":0,"mushrooms":0,"malnutrition":false},
  "mutations":{"MutationSlot1":"Truculency"},
  "elderStacks":0,
  "prime":{"eligible":true,"conditions":[true,false,true,false,false,false,true,true,true,true],"metCount":6},
  "skin":{"BodyColor":{"r":0.5,"g":0.5,"b":0.5,"a":1.0}, "...":"...", "SkinVariation":0, "PatternIndex":0}
}
```
Any section the plugin could not read is omitted (defensive per-field reads).
Codes: `OK`, `TARGET_OFFLINE`, `NO_PAWN`.

### `setskin` — direct-write customizer colors + indices
```json
{"id":"7","verb":"setskin","steam":"765...","args":{"customizer":{
  "BodyColor":{"r":0.8,"g":0.2,"b":0.1,"a":1.0},
  "MarkingsColor":{"r":0.2,"g":0.2,"b":0.2},
  "FlankColor":{"r":0.5,"g":0.3,"b":0.1},
  "UnderbellyColor":{"r":1,"g":1,"b":1},
  "Detail1Color":{"r":0,"g":0,"b":0},
  "EyesColor":{"r":1,"g":0,"b":0},
  "MaleDisplayColor":{"r":0,"g":1,"b":0},
  "TeethColor":{"r":1,"g":1,"b":1},
  "MouthColor":{"r":0.4,"g":0,"b":0},
  "ClawsColor":{"r":0.1,"g":0.1,"b":0.1},
  "SkinVariation":0,
  "PatternIndex":1
}}}
```
- Keys are the **engine field names**; color sub-keys are lowercase `r/g/b/a`
  (uppercase accepted; `a` defaults to `1.0`). Values are `FLinearColor` floats,
  nominally `0..1`; `>1` renders as HDR glow.
- The **ten** color fields (incl. the 0.21.720 `TeethColor`/`MouthColor`/
  `ClawsColor`), `SkinVariation` (floored, unvalidated), and `PatternIndex`.
- **`PatternIndex` is per-species range-validated by the engine, and a bad index
  silently drops the whole skin rebuild.** The plugin writes it only if it is a
  non-negative integer, and pushes it on a **separate** replication pass so a bad
  index cannot eat the colors (they render first). **You own the valid range**
  (0 .. species-pattern-count − 1); send a valid one or omit it.
- Does **not** persist across relog — reapply on `join` (see §5).
- `data`: `{"applied":["BodyColor","MarkingsColor",...,"PatternIndex"]}`.
- Codes: `OK`, `TARGET_OFFLINE`, `NO_PAWN`, `BAD_ARGS`, `CRASH`.

### `getskin`
```json
{"id":"8","verb":"getskin","steam":"765..."}
```
`data`: `{"BodyColor":{"r":..,"g":..,"b":..,"a":..}, ... ,"SkinVariation":..,"PatternIndex":..}`
(engine field names; never includes the un-readable `SkinCode`).

### `setmutations` — set mutation slots  *(ASYNC)*
```json
{"id":"9","verb":"setmutations","steam":"765...","args":{
  "slot1":"Truculency","slot2":"Accelerated Prey Drive",
  "parent1":"Truculency","elderA1":"Truculency","elderB1":"Truculency",
  "elderStacks":2,
  "unlocks":["Reniculate Kidneys"]
}}`
```
- Active slots: `slot1`..`slot4` → `MutationSlot1-4` (applied on a +500 ms
  deferred pass — this is where the result is emitted).
- Inherited (optional): `parent1`..`parent4`, `elderA1`..`elderA4`,
  `elderB1`..`elderB4` (applied synchronously).
- `elderStacks` (optional int): the lineage-tier gate that decides Life-1/2/3
  effective values (`EVRIMA_State_Restore_Cookbook.md`). Without it, restored
  slots read as Life 1.
- `unlocks` (optional array): quest-mutation unlock names written first, so
  quest mutations are permitted to equip.
- Mutation names are `FName`s and may contain spaces; obvious garbage
  (quotes/backslashes) is rejected.
- **Async:** the result arrives ~500 ms after the command, same `id`.
- `data`: `{"applied":["unlocks=1","inherited=3","elderStacks=2","active=2"]}`.
- Codes: `OK`, `TARGET_OFFLINE`, `NO_PAWN`, `APPLY_FAILED`.

### `getmutations`
```json
{"id":"10","verb":"getmutations","steam":"765..."}
```
`data`:
```json
{"slots":{"MutationSlot1":"Truculency","ParentMutationSlot1":"Truculency"},
 "elderStacks":2,
 "unlocks":["Reniculate Kidneys"]}
```
`slots` includes only populated fields among the 16 (`MutationSlot1-4`,
`ParentMutationSlot1-4`, `ElderMutationSlot1A-4A`, `ElderMutationSlot1B-4B`).

### `notify` — HUD popup  *(best-effort)*
```json
{"id":"11","verb":"notify","steam":"765...","args":{"message":"Hello!"}}
```
- Queues a `ClientShowNotification` popup, retried up to `notifyRetries` times to
  paper over post-spawn flakiness. The `OK` result means **queued**, not
  confirmed-delivered.
- **This is a popup, not a chat line.** Real chat-box / global-chat delivery is
  impossible from Lua (Safety Rule 13) — do that over **RCON**.
- Codes: `OK`, `BAD_ARGS`, `TARGET_OFFLINE`.

### `heal` / `kill`
```json
{"id":"12","verb":"heal","steam":"765..."}
{"id":"13","verb":"kill","steam":"765..."}
```
`heal` sets HP to max; `kill` sets HP to 0. Codes: `OK`, `TARGET_OFFLINE`,
`NO_PAWN`, `APPLY_FAILED`.

### `setgrowth`
```json
{"id":"14","verb":"setgrowth","steam":"765...","args":{"value":0.5}}
```
Sets growth only, clamped `[0.01,1.0]`. **Refills GAS vitals** (Safety Rule 8) —
use `setstats` if you want to set growth and vitals together. Codes: `OK`,
`BAD_ARGS`, `TARGET_OFFLINE`, `NO_PAWN`, `APPLY_FAILED`.

### `setvital`
```json
{"id":"15","verb":"setvital","steam":"765...","args":{"name":"thirst","value":900}}
```
`name` ∈ `health|stamina|hunger|thirst|oxygen|food|blood`. Codes: `OK`,
`BAD_ARGS`, `TARGET_OFFLINE`, `NO_PAWN`, `APPLY_FAILED`.

### `players` — online roster  *(no target)*
```json
{"id":"16","verb":"players"}
```
`data`: `{"players":["765...","765..."],"count":2}`.

### `stats` — control the periodic stats stream  *(no target)*
```json
{"id":"17","verb":"stats","args":{"mode":"on","interval":5}}
```
- `mode`: `"on"` / `"off"`. `interval`: optional seconds (≥1).
- `data`: `{"enabled":true,"interval":5}`.
- See §7 for the flood guard — turning it `on` is not enough; you must also keep
  the reader flag fresh.

### `ping` — liveness  *(no target)*
```json
{"id":"18","verb":"ping"}
```
`data`: `{"version":"v002","players":3,"uptime":123}` (uptime seconds since
plugin load). Use it as a heartbeat to confirm the bridge is alive.

---

## 7. `stats.ndjson` — periodic snapshots + flood guard

When enabled, the plugin writes one snapshot line (same shape as `getstats`
`data`) per online player every `statsIntervalSeconds`. At 100 players this is
~4.5 MB/hour — so it is **OFF by default and reader-gated**:

**The flood guard.** The stream only emits while a *fresh* reader flag exists at
`statsReaderFlag` (default `Saved/stats.on`). Your C# side must:

1. Send `stats {"mode":"on"}` (or set `statsStreamEnabled` in config).
2. **Write the current unix timestamp into `Saved/stats.on` every few seconds**
   (a heartbeat). The plugin stops emitting if the flag is older than
   `statsReaderTtlSeconds` (default 15 s). So if your consumer dies, the stream
   goes quiet within 15 s — it can never flood the disk when nobody's reading.
3. To stop cleanly, send `stats {"mode":"off"}` and/or delete `stats.on`.

(Set `statsReaderTtlSeconds` to `0` to disable the gate entirely — not
recommended in production.) The file rotates at `rotateMaxBytes` like the others.

For one-off reads, prefer the `getstats` verb over turning on the stream.

---

## 8. `chat.ndjson` — live chat feed

```json
{"ts":1779000000,"steam":"765...","mode":1,"modeName":"Global","name":"Rex","text":"hello"}
```
- `steam`: the **sender's** Steam64. `mode`/`modeName`: `EChatMode`
  (`0 Spatial`, `1 Global`, `2 Admin`, `3 Logging`). `name`: best-effort display
  name, **omitted if unreadable** — resolve from steam on your side. `text`: the
  message.
- The hook fires once per receiver in range; the feed de-dupes on `(steam,text)`
  within a 3 s window, so you get one line per distinct message.
- This is where you parse `!commands` (chat-command handling stays on your side;
  the plugin just relays the text).

---

## 9. Hard limits (design around these)

| Limit | Consequence for C# |
|---|---|
| No chat-box / global delivery from Lua | Use **RCON** for real chat. `notify` is a HUD popup only. |
| `SkinCode` unwritable | `setskin` writes colors/indices, not the code string. |
| Skins revert on relog | Reapply `setskin` on `join` (§5). |
| `PatternIndex` per-species, bad value drops the apply | You own/validate the range; send valid or omit. |
| No death UFunction | `death` events are poll-based + lagged + lossy; use RCON/KillFeed for exact attribution. |
| Gender has no setter | A `swap` may flip gender; warn users. |
| `unprime` may not stick past 75% growth | Don't assume `unprime` cleared it; verify with `getstats`. |
| `notify` flaky post-spawn | `OK` = queued, not confirmed. |
| `teleport` under-verified | Confirm live; check the `pos` in the result. |
| At-least-once delivery | Reuse `id` on retry; ignore results for closed ids. |

---

## 10. C# reference (pseudocode)

Illustrative, not drop-in. Adapt to your proxy's runtime.

```csharp
// ---- append a command line (never truncate the inbox) ----
void SendCommand(object cmd) {
    var line = JsonSerializer.Serialize(cmd) + "\n";
    using var fs = new FileStream(InboxPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    var bytes = Encoding.UTF8.GetBytes(line);
    fs.Write(bytes, 0, bytes.Length);
}

// ---- tail an out stream with offset + shrink-reset + .old drain ----
class NdjsonTailer {
    readonly string path; long offset; string buffer = "";
    void Poll(Action<string> onLine) {
        long size = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        if (size < offset) {                 // rotated: drain <path>.old then reset
            DrainFrom(path + ".old", offset, onLine);
            offset = 0; buffer = "";
        }
        offset = ReadFrom(path, offset, ref buffer, onLine);
    }
    long ReadFrom(string p, long from, ref string buf, Action<string> onLine) {
        if (!File.Exists(p)) return from;
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(from, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        buf += sr.ReadToEnd();
        int nl;
        while ((nl = buf.IndexOf('\n')) >= 0) {
            var line = buf[..nl]; buf = buf[(nl + 1)..];
            if (line.Length > 0) { try { onLine(line); } catch { /* log */ } }
        }
        return fs.Position;
    }
    void DrainFrom(string p, long from, Action<string> onLine) {
        var buf = ""; ReadFrom(p, from, ref buf, onLine);
    }
}

// ---- correlation map with timeout ----
class CommandClient {
    readonly ConcurrentDictionary<string, TaskCompletionSource<Result>> pending = new();
    Task<Result> Send(Cmd c, TimeSpan? timeout = null) {
        c.id ??= Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<Result>();
        pending[c.id] = tcs;
        SendCommand(c);
        var t = timeout ?? TimeSpan.FromSeconds(5);   // 8s for swap/setmutations
        return WithTimeout(tcs.Task, t, c.id);
    }
    // called for each results.ndjson line
    void OnResult(Result r) {
        if (r.id != null && pending.TryRemove(r.id, out var tcs)) tcs.TrySetResult(r);
        // else: unknown/closed id (e.g. after restart) — ignore, idempotent
    }
}

// ---- stats reader heartbeat (flood guard) ----
async Task KeepStatsAlive(CancellationToken ct) {
    await Send(new Cmd { verb = "stats", args = new { mode = "on", interval = 5 } });
    while (!ct.IsCancellationRequested) {
        File.WriteAllText(StatsOnPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
    await Send(new Cmd { verb = "stats", args = new { mode = "off" } });
    File.Delete(StatsOnPath);
}
```

---

## 11. Versioning

- Plugin build: `v002` (reported by `ping`). Bump when the verb surface or
  envelope changes.
- Game build: **0.21.720**. The `setskin`/`getskin` recipe is the post-patch
  direct-write path; the customizer struct has ten color fields. If the game
  updates, re-verify skin, teleport, and mutation paths against the
  `evrima-dev-knowledge` docs before trusting them.
