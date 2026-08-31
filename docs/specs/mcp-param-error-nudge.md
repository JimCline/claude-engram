# MCP parameter-error nudge

**Status:** final — implementation-ready. NE-1 resolved (Branch B, evidence in §4.2); §5 rename approved by Jim.
**Origin:** Jim, verbatim — *"if the LLM tries to call engram MCP tools without the correct params, it just gives a non-descript error. We need to return a nudge to read the tools schema/params/instructions."*

**Amendment history.** Revised after NE-1 came back Branch B. Three things changed, and the second is the one an Implementor working from the earlier draft would get wrong:
1. §4.2 collapsed to one shape; Branch A deleted.
2. **§4.1 inverted.** Branch B puts the filter *inside* the SDK's catch, which means the filter now sees every exception from every tool, not only binding failures. The earlier draft told the Implementor to copy the SDK's rethrow exclusion set verbatim; that is now **wrong**, and the correct design is a positive type predicate that never catches those exceptions in the first place (§4.1). Smaller, and it removes the detail most likely to be silently wrong.
3. §5 unblocked; §4.3's example rewritten, because the rename makes the old example a *successful* call.

---

## 0. Ruling, up front

**Jim hit failure class 2 (SDK-level), not class 1 (this codebase's validation). Confidence: high — and the brief's framing of class 1 is wrong in engram's favour.**

The brief speculated that class 1 might "still not point back at the schema." It does. Verified by grep across `src/Engram.Cli/EngramMcpTools.cs`:

- **Zero `throw ` statements. Zero `ArgumentException`/`InvalidOperationException`/`NotSupportedException`/`default:`.** The file cannot raise an exception of its own by any path a grep can see.
- Every constrained-value parameter validates and returns a conversational string that *names the legal values*:
  - `expand.view` → `:383` — `Unknown view '{view}'. The views are history, related, evidence, source, and details.`
  - `judge.relation` → `:586-587` — `'{relation}' is not a recognized relation; expected one of {string.Join(", ", FactRelations.Kinds)}. Nothing was recorded.`
  - `index_repo.decision` → `:526` — `'{decision}' is not a recognized decision; expected enroll, decline, or later. Nothing was recorded.`
  - `navigate.relation` → `:757-758` — `Unknown relation '{relation}'. Use defined_at, imports, callers, callees, implements, implementers, or members.`
  - fact handles → eight sites (`:132, :263, :357, :411, :570, :575, :637, :655`), each `'{x}' is not a fact handle; they look like 'f42'. Nothing was <verb>.`
  - `review_after` → `:1709`; `details` ceiling → `:1681-1694`.

Every one of those names the valid form **and** states what did not happen. That is already the nudge shape the brief asks to build. **Class 1 needs no change, and this spec proposes none.**

So a report of a *non-descript* error is, by elimination, a call whose arguments never reached the method body. That is class 2, and §1 identifies the exact line of SDK source that discards the detail.

---

## 1. Root cause — one line of SDK source

`ModelContextProtocol.Core` v2.0.0 (pinned at `Directory.Packages.props:14-15`), `McpServerImpl.cs` — `CreateToolCallErrorResult`, `:1626-1636`:

```csharp
return new()
{
    IsError = true,
    Content = [new TextContentBlock
    {
        Text = exception is McpException ?
            $"An error occurred invoking '{request.Params?.Name}': {exception.Message}" :
            $"An error occurred invoking '{request.Params?.Name}'.",
    }],
};
```

**`An error occurred invoking 'engram_recall'.`** — that string, with no cause, no parameter name, no schema pointer, is what Jim saw. The SDK deliberately sanitizes: an exception's message is forwarded **only** if the exception is an `McpException`, and discarded otherwise.

Two consequences that decide the rest of this spec:

1. **engram cannot pick the exception type.** The throw happens inside `AIFunction.InvokeAsync` (Microsoft.Extensions.AI's argument marshaller), reached from `AIFunctionMcpServerTool.InvokeAsync` (`:347-364`), which has **no try/catch of its own** — it bare-awaits `AIFunction.InvokeAsync` at `:364`. engram's code has not run and will not run. So "just throw `McpException`" is **not available** for this class — it is the SDK's escape hatch for handler code, and binding failure is not handler code.
2. **The discarded detail is only visible inside the invocation pipeline.** Past `CreateToolCallErrorResult` the cause is gone permanently. Any fix must sit inside the pipeline, which is what §3 selects.

### 1.1 What actually reaches this path

Parameter names below are as of today, i.e. **before** §5's rename.

- **2a — missing required argument.** Required (no-default) params across the surface: `recall.query`; `remember.statement`; `forget.id`; `browse.path`; `expand.id`, `expand.view`; `revise.fact_id`, `.statement`, `.reason`; `index_repo.path`, `.decision`; `judge.fact_id`, `.related_id`, `.relation`, `.reason`; `pin.fact_id`; `unpin.fact_id`; `navigate.query`, `.relation`.
- **2b — wrong JSON type.** `expand.budget_tokens` and `expand.offset` are `int` (not `int?`), `navigate.limit` is `int`, `recall.budget_tokens` is `int?`. A model passing `"500"` (string) rather than `500` fails to deserialize and throws before the body runs. `remember.sync` is `bool`; `revise.sync` is `bool?`.
- **2c — misspelled argument name**, which reduces to 2a: the correct name is absent.

### 1.2 A root cause behind the root cause — parameter naming is inconsistent

`forget` and `expand` name the fact handle **`id`**. `revise`, `judge`, `pin`, `unpin` name it **`fact_id`**. Same concept, same format (`"f42"`), two spellings across six tools.

A model that has just called `engram_pin(fact_id: "f42")` and then calls `engram_forget(fact_id: "f42")` supplies an unknown argument *and* omits the required `id` — a textbook 2c→2a, produced by engram's own surface rather than by model error. Per the repo's root-cause-over-symptom rule, this is worth fixing alongside the message. **Approved; see §5.** It remains a separate commit and is **not** a prerequisite for §3.

---

## 2. Mechanisms considered and rejected

Recorded so they are not re-proposed.

- **Throw `McpException` to get the message forwarded.** Rejected: unavailable. §1 consequence 1 — engram's code never executes on this path.
- **Wrap every tool body in try/catch.** Rejected: the exception is thrown *before* the body is entered, so a body-level catch cannot see it. It would also add 14 catch blocks to fix zero of the failures, and the repo has zero `try/catch` at tool level today by design.
- **Retype the constrained-value params as C# `enum`s so the emitted JSON schema carries `enum: [...]`.** Rejected on ladder rung 1: those four params are exactly the ones class 1 *already* handles well (§0). This would spend an AOT-risky change (enum converters, `NoReflectionJsonTests`) to improve the one area that is not broken.
- **Set `McpServerOptions.ServerInstructions`.** Available and currently unset — `grep "Instructions" src/Engram.Cli/` returns **zero matches**, so engram ships no server-level instructions at all. Rejected *as the fix*: it is always-on context, charged to every session forever, to pre-empt an occasional event, and it does not make the failing call self-correcting — the nudge is only useful attached to the failure, where it is read at the moment it applies. Recorded as a genuine and separate gap; if it is ever wanted, it is a one-sentence change in the `AddMcpServer(...)` callback at `ServeCommand.cs:138`, and it should be argued on its own merits, not folded in here.
- **A static `Dictionary<toolName, requiredParams>` to enumerate required params in the nudge.** Rejected: a second copy of every signature, guaranteed to drift from the first the next time a parameter is added. §4.3 gets the same information without duplicating anything.
- **Forward the raw `exception.Message` for *every* failure, not just binding failures.** Rejected, added at amendment time. Branch B (§4.2) makes it trivially available, which is exactly why it needs an explicit refusal: the SDK's sanitization is deliberate, and overriding it globally would push internal detail (SQLite messages, file paths) to the client for a class of failure nobody asked about. §4.1 rethrows everything it does not positively recognise, so behaviour outside the target class is unchanged.

---

## 3. Mechanism — a CallTool filter

**Verified present in the pinned version.** `McpServerOptions.Filters` (`McpServerFilters` → `Request` / `Message`), reached from the builder by:

```csharp
public static IMcpServerBuilder WithRequestFilters(this IMcpServerBuilder builder, Action<IMcpRequestFilterBuilder> configure)
```

and `McpRequestFilterBuilderExtensions` supplies **`AddCallToolFilter`** among its eleven `AddXxxFilter` methods.

This is the right mechanism for four reasons:

1. It is the SDK's own supported extension point for exactly this — not a wrapper invented here, which §[3] of the brief rules out.
2. **One registration reaches all fourteen tools**, present and future. Nothing is per-tool, nothing is added to `EngramMcpTools.cs`.
3. It is delegate-based: no reflection, no serialization change, so the AOT and `NoReflectionJsonTests` constraints are untouched.
4. It is the **only** place the real cause is still visible (§1 consequence 2), now confirmed by the §4.2 trace rather than assumed.

### 3.1 Registration

`src/Engram.Cli/ServeCommand.cs`, in the chain beginning at `:138`:

```csharp
var mcpBuilder = builder.Services.AddMcpServer()
    // ... existing configuration ...
    .WithRequestFilters(f => f.AddCallToolFilter(McpCallNudge.Filter))
    .WithHttpTransport(options => options.Stateless = false);
```

Order relative to `WithHttpTransport` is not load-bearing; keep it adjacent to the `WithTools<T>()` calls at `:149`/`:154` so the three related registrations read together. Note the comment already at `:129-131` explaining why the generic `WithTools<T>()` form is load-bearing for AOT — do not disturb it.

### 3.2 New file

`src/Engram.Cli/McpCallNudge.cs` — one static class, one public entry point. It does not belong in `EngramMcpTools.cs`: that file is the tool surface, and this is transport-layer error shaping.

---

## 4. Behaviour

### 4.1 The filter catches by positive type predicate, and catches nothing else

**This section replaces the earlier draft's instruction to copy the SDK's rethrow exclusion set. Do not do that.** It was written on the assumption that the filter might sit outside the SDK's catch; §4.2 establishes it sits inside, which changes what the filter is responsible for.

Inside the chain, `await next(...)` can throw **anything any tool raises**, not just a binding failure — a `SqliteException` from `FactStore`, an `IOException`, anything downstream. A bare `catch (Exception)` would therefore (a) label a database error as a schema mismatch, and (b) swallow `OperationCanceledException`, `McpProtocolException` and `InputRequiredException` before the SDK's own catch could rethrow them, which is the failure the earlier draft was trying to prevent by copying that set.

The correct shape is a positive predicate, not a negative exclusion list:

```csharp
catch (Exception e) when (IsArgumentBindingFailure(e))
{
    return NudgeResult(request, e);
}
```

Everything unmatched propagates untouched to `InvokeOrdinaryPipelineAsync`, which handles it exactly as it does today. The three excluded types are excluded *automatically* — none of them is a binding-failure type, so the `when` clause is false and they were never caught. **No exclusion list is written, and none can rot.**

Two rules on the predicate, both load-bearing:

- **It must enumerate types positively.** `ArgumentException` (which covers `ArgumentNullException`), `System.Text.Json.JsonException`, and `NotSupportedException` (STJ throws this for an unconvertible type) are the candidates. See NE-6 for confirming the actual type; a miss there fails loudly (§7).
- **It may never be `catch (Exception)` with an exclusion list bolted on.** That form reintroduces exactly the hazard this design removes, and it will pass every test in §6 except by accident.

**Consequence worth stating explicitly, because it is what makes Branch B work:** when the filter *returns* a result rather than rethrowing, `InvokeOrdinaryPipelineAsync` sees a normal return. `CreateToolCallErrorResult` never runs, so §1's sanitization never applies and the nudge text reaches the client verbatim.

### 4.2 NE-1 — RESOLVED: the filter runs inside the SDK's catch

Traced through a per-file `ilspycmd` decompile of `ModelContextProtocol.Core.dll` v2.0.0, in `McpServerImpl.ConfigureTools`:

- **`:1342`** — the raw tool invoker, `req => ((McpServerTool)matched).InvokeAsync(req, ct)`. For an `AIFunction`-backed tool that is `AIFunctionMcpServerTool.InvokeAsync` (`:347-364`) — no try/catch, bare-awaits `AIFunction.InvokeAsync(...)` at `:364`, which is where per-parameter binding into the typed C# signature happens and where the exception is thrown.
- **`:1344`** — `BuildFilterPipeline(handler, callToolFilters)` wraps that invoker with exactly the `AddCallToolFilter` registrations. `BuildFilterPipeline` (`:1605-1617`) is pure delegate composition with **zero try/catch of its own**, so each filter's `next()` calls straight down to the unguarded invoker.
- **`:1345`** — the filtered pipeline goes to `BuildComposedCallToolHandler` (`:1357-1399`), whose `InvokeOrdinaryPipelineAsync` (`:1379-1397`) is the **only** catch site. It wraps the whole filter chain, rethrows `OperationCanceledException` (when cancelled) / `McpProtocolException` / `InputRequiredException`, and converts everything else via `CreateToolCallErrorResult` (`:1439-1456`).

**The catch is outside and after the entire filter chain.** A filter therefore receives the raw exception when it awaits `next(...)`, and can read `exception.Message` and `exception.GetType().Name` directly. **Branch A is deleted** — no string-prefix matching against SDK text, and §6.3 drops from mandatory to advisory.

### 4.3 The nudge text

Two constraints: it is read by a model, not a person, and it must not restate anything that could drift out of sync with the schema (§2).

Compose from what the filter already holds, with no second copy of any signature:

- `request.Params?.Name` — the tool that failed.
- `request.Params?.Arguments` keys — **the argument names actually received**. Free, and it cannot drift, because it is the live call rather than a description of one.
- `exception.Message` — available under Branch B, and for a binding failure the marshaller's message usually names the offending parameter. Include it; it is the single most useful token in the result and costs nothing.

Shape (example is a 2b type mismatch, since §5's rename retires the 2c example the earlier draft used):

```
engram_expand: the arguments did not match this tool's schema, so nothing ran.
Received: fact_id, view, budget_tokens. Detail: budget_tokens could not be
converted to Int32. Re-read engram_expand's inputSchema — its parameter names,
which are required, and their types — and retry.
```

Rules for the wording:

- **Name the tool.** The model may have several calls in flight.
- **Echo the received argument names, never a list of expected ones.** Letting the model diff what it sent against the schema it already holds is what makes this self-correcting, and it is the half that cannot go stale.
- **State that nothing happened.** This matches every class-1 message in the codebase (`Nothing was retracted.`, `Nothing was recorded.`, `Nothing was saved.`) and prevents a retry being read as a possible double-write.
- **Do not paste the schema in.** The client already has it; repeating it costs tokens on every failure and is a third copy to drift.
- Keep it to roughly the length above. This is an error path, not documentation.

### 4.4 What must not change

- No change to any `[Description]` text.
- No change to any tool signature — except §5's approved rename, which is its own commit.
- No change to class-1 validation messages — they are correct (§0).
- The result must still carry `IsError = true`. Do not downgrade it to an ordinary result: clients distinguish, and a failed call reported as success is worse than the non-descript message this replaces.
- No new `try/catch` inside `EngramMcpTools.cs`.
- **No behaviour change for any exception the predicate does not match.** That is the §4.1 contract and §6.2 is what holds it.

---

## 5. The naming inconsistency (§1.2) — APPROVED

**Jim's answer: "Yes, rename to fact_id."** Rename `forget`'s `id` and `expand`'s `id` to `fact_id`, making all six handle-taking tools agree. `fact_id` is the majority spelling (four tools to two) and is self-describing at the call site.

Why this is lower-risk than it sounds: an MCP tool schema is delivered fresh at `initialize` on every connection and is not persisted client-side, so there is no cached-contract population to strand. The risk is confined to anything *inside* this repo that names those parameters.

Remaining work, mechanical:

- Rename the parameter in both method signatures in `src/Engram.Cli/EngramMcpTools.cs` and every reference within those two method bodies (`:263`, `:357` and `:411` are among the sites that interpolate it into a class-1 message — the message text says `'{x}' is not a fact handle`, so it needs no rewording, only the variable rename).
- **Run NE-5's grep and report what it found** (`grep -rn "engram_forget\|engram_expand" --include=*.cs --include=*.md .`). The decision is made; the grep is now a check for collateral, not a gate. If it turns up a prompt, primer, doc, skill, or test hardcoding `id:` for these two tools, fix those in the same commit and say so.
- Separate commit from §3. §3 stands alone and must not wait on it.

---

## 6. Tests

Tier 2 (integration) is where this belongs — it needs a real server and a real client call, which no unit test reaches.

1. **The fires-at-all test.** Call a tool omitting a required argument (e.g. `engram_recall` with no `query`) and assert the returned text contains the nudge's distinctive phrasing **and** the tool's own name. This is the whole feature; without it there is no evidence the filter is wired in.
2. **The pass-through test (§4.1) — now the load-bearing one.** Assert that a non-binding failure is *not* converted by the filter. A cancelled call must still surface as cancellation, not as a nudge. Under Branch B the filter sits in the path of every exception every tool can raise, so this is what proves the predicate is positive rather than a disguised `catch (Exception)`. **Falsify it**: widen the predicate to `catch (Exception)` and confirm this test goes red. If it stays green it is not testing the predicate.
3. **SDK-text-dependency test — advisory.** Branch B removes the string-prefix dependency that made this mandatory. Worth keeping only as a cheap tripwire on a `ModelContextProtocol` bump.
4. **Falsification is required before this is believed** — per the repo rule that a guard which cannot fail is worthless, and per D60's two process lessons. Remove the `.WithRequestFilters(...)` line, run `git diff --quiet` to confirm the break actually landed in the working tree, and confirm test 1 goes red. Restore. **If test 1 stays green with the registration removed, report and stop** — the filter is not doing the work and the test is measuring something else.

Note for whoever runs these: `Engram.EndToEnd.Tests` skips wholesale without a published binary and still prints `Passed!`. Read the skip count, not the pass count.

---

## 7. NEEDS-EVIDENCE

- **NE-1 — RESOLVED.** Branch B; evidence recorded in §4.2. No longer blocking.
- **NE-2 — confirms §1, does not gate it.** Reproduce Jim's report against a running server and capture the literal text. Expected: `An error occurred invoking '<tool>'.` It does not change the design — every 2a/2b/2c path lands in the same filter regardless of the wording — so this bounds confidence, not direction.
- **NE-3.** Which mis-call did Jim actually make? Now only of historical interest: §5 is approved regardless, so this can no longer change any decision. **Drop it.**
- **NE-4 — §4.3 mechanics. Downgraded to a test assertion.** Under Branch B the filter holds its own `request` reference on the stack across `await next(...)`, and it is the same object it passed down — the SDK has no reason to mutate it, so `Params.Arguments` should be intact. Assert it in test 1 rather than gathering it separately; if it *is* empty, drop the "Received:" clause and keep the rest.
- **NE-5 — §5.** The grep, now a collateral check rather than a gate. Report findings (§5).
- **NE-6 — new, non-blocking, tunes §4.1's predicate.** What exception type does `AIFunction.InvokeAsync` actually throw for (a) a missing required argument and (b) an unconvertible type? *Method:* it falls out of test 1 — log `e.GetType().FullName` the first time the filter fires. **Non-blocking because a miss fails loudly**: if the predicate does not match the real type, the exception propagates, the SDK sanitizes it, and test 1 goes red with the old non-descript string. There is no silent-failure mode here, which is the whole reason this is a test-time discovery rather than a prerequisite.

---

## 8. Confidence and what I did not verify

- **High** that the sanitized `CreateToolCallErrorResult` string is Jim's non-descript error, and that class 1 is not implicated. Both rest on read source and exhaustive grep, not inference.
- **High** that `AddCallToolFilter` exists in the pinned 2.0.0 and is the correct mechanism.
- **High, now on evidence rather than reasoning**, that the filter sits inside the SDK's catch (§4.2, decompiled `ModelContextProtocol.Core.dll` v2.0.0).
- **Open, deliberately:** the exact exception type for a binding failure (NE-6). Specified around rather than resolved, because the failure mode is loud.
- The SDK source was read at tag `v2.0.0` matching `Directory.Packages.props`, except `AIFunctionMcpServerTool.cs`, `McpServerToolCreateOptions.cs`, `McpServerOptions.cs` and `McpException.cs`, which were read at `main` and used only for structural claims. The §4.2 trace is from a decompile of the pinned assembly itself, which is the stronger source; where the two disagree, prefer the decompile.
- Three versions of `ModelContextProtocol` sit in the local NuGet cache (`0.4.0-preview.1`, `2.0.0`, `2.2.0`). `Directory.Packages.props` pins `2.0.0`; the others are unrelated restores. Do not read behaviour from `2.2.0`.
