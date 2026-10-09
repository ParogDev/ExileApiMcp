# Working with the user in game (both HUDs)

How an agent asks a developer for help with a guided experiment: when to ask, how to word an instruction, what goes where (in-game card, detail line, chat), pacing, and how to close. This is the UX contract for `await_change` and `guide`. The user is a developer who runs agents against the HUD; they are busy, they are in game, and they read the HUD's agent guide card, not the chat.

## When to ask for an in-game action
- **Ask** when the answer depends on state only the user can change: what a field means, whether a mapping still holds, what an action touches, which bit a toggle flips. One action with its undo settles it; reading more memory does not.
- **Don't ask** for what you can read: a value (`eval_path`, `explore_object`), a struct (`memory_layout`), a population (`memory_correlate` over all stash tabs), the code (`find_field_access`, Ghidra). Read first; ask once you know what to watch.
- **Don't ask** for things the tools forbid: you never send input, never write memory. If an action would cost the user something (trade, vendor, currency use), say so and offer a cheaper action.
- Check `experiment_presets` first: a ready-made experiment has tested instructions and watch specs.

## Show where, don't just say where
- **Point at it whenever you ask for a click:** pass `highlight` to `await_change`, `experiment_step_start` or `experiment_queue`, or call the `highlight` tool. The words go on the card; the highlight shows the place.
- **Targets:**
  - `item`: a name; every match in the inventory and the visible stash tab is shown.
  - `path`: any UI element, such as a tab, button or checkbox (find it with `explore_object`).
  - `panel` + `child`: a control inside a panel the HUD doesn't map. `panel` is text inside the panel (e.g. "Stash Tab Settings") and `child` is the index path inside it, e.g. `[0,1,7,1,11,1]`. Prefer it over `IngameUi.Children[n]` paths: the top-level indexes shift, as the dialog moved from [121] to [122] between two openings.
  - `rect`: a screen area.
- **Tiers:**
  - `primary`: click or look here. It is the only animated one, so give one or two at most.
  - `secondary`: related.
  - `context`: an area to orient the eye.
  - `text`: an element by its exact label, such as a stash tab called "DUMP" or a button. Every visible match is shown; add `within` (text inside a panel) to narrow it down.
- **Show the action:** `action: rightclick` or `click` draws a mouse cue showing which button to press.
- **Sequences follow the user:** a later step becomes current as soon as its target appears (e.g. the dialog opened), and `until: checked | unchecked | gone` ends a step. Make every sub-action its own step, so the highlight never runs ahead of or behind the user.
  - Example: 1 `text: DUMP` (`action: rightclick`), 2 the Ritual checkbox (`until: checked`), 3 the Confirm button.
- **Sequences:** give targets an `order` for "1 then 2 then 3". `highlight advance=true` moves to the next step. Clear highlights when the step is done; steps clear their own.
- Never use a highlight to make the user act faster than they want, and keep it short-lived.

## Multi-step tasks: guided flows, not fixed sequences
For anything with more than one action (open a dialog, tick something, confirm), use a **flow** (`guide_flow`, or `recipe` / `flow` on `experiment_queue`). Don't use a fixed highlight sequence.

**What the flow does:**
- **The game's state decides the next step.** Every 100 ms the current step is the first one whose `done` condition doesn't hold. If the user navigates away (closes the dialog, switches tab), it goes back by itself. The `goal` ends the flow, whatever path the user took.
- **Each step lists options best-first, each with a `when`** (default: its target is on screen). The first that's possible now is shown. The ones above it are what it unlocks, which gives the live plan: e.g. "Open the tab list → Click DUMP in the list → Right-click DUMP → Untick Ritual → Confirm". The plan shortens when a shortcut is on screen.
- **Conditions only check what can be checked.** A checkbox is read only while its dialog is open; otherwise the step that opens the dialog is current anyway.

**Rules for a good flow:**
- One physical action per option.
- The tab row is not the tab list: right-click the tab in the row (clipped to the row's visible part), and only click it in the list to bring it into view.
- **Recipes** live in `Knowledge/flows.json`, with params and values derived from game tables (e.g. an affinity name → its bit via `StashTabAffinities.dat`). Add a recipe when a flow is worth reusing.
- **Conditions:** `visible`, `checked` / `unchecked`, `text` + `equals`, `eval` + `equals`, `memory` (a collection item `where` a property equals a value; offset / size / mask / equals), combined with `all` / `any` / `not`.
- **Targets** as in highlights, plus `rel` (`"^"` = parent, `"n"` = child n) to go from a label to its checkbox, and `clipTo`.

## Live step or queued step
- **Live (`await_change`):** use it when the user is at the game and answering you now. Capture starts at once, and the step fails if they are slow.
- **Queued (`experiment_queue`):** use it when they are away, busy, or you don't know.
  - The step waits on the in-game card ("queued for you") with your instruction, `note` (why you need it, what you expect) and repeats. The user presses **Start** when ready, and only then does the HUD record. It records by itself, so you need not be connected.
  - The queue survives HUD restarts, so a developer coming back later sees what you asked for.
  - Collect with `experiment_queue_status`. It diffs finished steps into the experiment record, so `experiment_summary` works as usual.
  - Cancel what you no longer need (`experiment_queue_cancel`).
- Queue the action and its undo as separate labels, or use repeats on a toggle: each repeat starts from the state the last one left.
- **Continue by yourself:** right after queuing, start `experiment_queue_wait experiment=<name>` where it can block without holding the conversation.
  - In Claude Code, run `tools\mcp-call.ps1 experiment_queue_wait experiment=<name> -TimeoutSec 3700` as a background task. Its completion wakes you with the collected results.
  - Then report and go on; the user should not have to come back and say "done".
  - On `waiting:true` (timeout), start it again.
- **A series:** queue the first step plain and the rest with `chain=true`. One Start press then runs them one after another, each starting as soon as the previous one is captured.
- **Pitfall: the action done before Start.** Users read a queued instruction as "do it now". If the action happens before Start, it is already in the baseline: that step records nothing, the user's next action lands under its label, and every chained step after it shifts by one.
  - Word the first step "Press Start first, then ...".
  - For one-way actions (a page that loads once), watch every candidate in each step, so a shifted capture still shows what really changed.
  - Check results against the content (items, names), not only the label.

## Phrasing an instruction (the in-game card)
One action, imperative, about 70 characters, with the key and the mouse target. The card shows it large; longer text wraps and loses the glance.
- Name the modifier key in caps and the direction or count: `Ctrl+scroll DOWN once`, `Ctrl+left-click ONE item`.
- Name where the mouse is: `over the stash`, `in your inventory`, `an empty stash cell`.
- Say what the user will see when it worked, in parentheses, when it is short: `(one tab forward)`, `(it moves to your inventory)`.
- Ask for the undo as its own step with its own label: `to-inventory` then `to-stash`, `next-tab` then `prev-tab`. The answer is what flips on and back.

| Good (from the stash presets) | Bad |
|---|---|
| `Hover the stash and Ctrl+scroll DOWN once (one tab forward).` | `Switch tabs` (which key? how many?) |
| `Ctrl+left-click ONE item in the stash tab (it moves to your inventory).` | `Move some items around so I can see the inventory change` (how many? which way?) |
| `Move the mouse off the item to an empty stash cell.` | `Stop hovering` (where should the mouse go?) |
| `Ctrl+scroll UP once (back to the previous tab).` | `Now undo that` (the card stands alone; the user may have looked away) |

Labels are short kebab-case verbs the summary groups by: `next-tab`, `to-inventory`, `hover`. Reuse the exact label on every repeat, or `experiment_summary` cannot count them.

## What goes where
- **The card (`instruction`)**: only the action. It is read in a glance, in game.
- **`detail`**: one line of context the user may want while acting: what is being watched (`Watching 3 values for up to 45 s`), what was captured (`ItemCount 11 -> 12`), what to fix (`Nothing lasting changed - is the stash open?`). `await_change` fills it; override it only to say something more useful.
- **`title`**: the experiment, so the card and the record match: `Experiment: stash-ctrl-click`.
- **Chat**: before step 1, one or two lines: what the experiment will show and what the user will do (`I'll watch the stash and inventory counts while you Ctrl+click an item out and back, twice each`). Then results only: one line per capture, the finding at the end. The user is looking at the game, so do not narrate the waiting, and do not repeat the instruction in chat.
- **Guide log** (`guide log=`): short notes between steps that are not instructions: `Captured, now the reverse`, `Nothing changed, checking the watch`. Tool calls are logged there automatically.

## Setup before step 1
- Ask for the setup in chat and on the card, once: `guide instruction="Open your stash on a tab with items" status=info title="Experiment: ..."`. The presets' `setup` line is written for this.
- Confirm the watched paths read before waiting: `eval_path` on each `value:` path, `memory_layout` on each `memory:` object. A path that fails or reads nonsense costs the user a wasted step.
- Prefer narrow objects. Whole UI elements flicker on hover and animation; `await_change` ignores changes that revert, but a noisy watch slows the settle.
- Pass `step=n steps=m` so the card shows progress. Count repeats as steps (`step 3 / 6`), so "do it again" is expected, not a surprise.

## Pacing and timeouts
- `timeoutMs`: 45-60 s for one click or scroll; up to 120 s when the user must open a window first. The card shows the elapsed time; a short timeout makes the card go red while they are still finding the item.
- `settleMs`: 500 ms (default) for clicks; 1000-1500 ms when the action triggers a server round trip (stash tab settings, affinities), since the change lands after the confirm.
- Between steps, call the next `await_change` right away; the card going from CAPTURED to DO THIS NOW is the cue. Don't make them wait on a chat message.
- **On `changed: false` (TRY AGAIN)**: the card already says it. Check the cheap causes before asking again: is the panel open, is the game window focused, does the watched value follow the action at all (`eval_path` before and after a manual test)? If a `transientChanges` count is high, the watch is too wide: narrow it. Then run the same label again; a second failure means the watch is wrong, not the user. Say so and change the watch, don't ask a third time.
- Repeat each action 2-3 times. After two repeats `await_change` returns `consistent`; three make `sometimes` meaningful. Stop when the evidence is clear, not at a fixed count.

## Closing
1. `guide status=done title="Experiment: <name>" instruction="<one-line result>"`, e.g. `Inventory ItemCount follows Ctrl+click; the stash tab's ItemCount does not on Currency tabs`. The card stays as a receipt until the user dismisses it.
2. In chat, the result in one line, then the evidence in one more (`4 steps, 2 repeats each; ServerData...ItemCount changed every time`).
3. List what to undo in game, even when it seems obvious: `an item is in your inventory that came from the stash`, `stash affinities were moved to tab 19`, `the stash is on tab 2, not the one you started on`. Experiments that pair every action with its undo leave nothing, and say so.
4. Record new facts in `Knowledge/findings.json` with the experiment as evidence, and add to the relevant knowledge pack.
5. `guide clear=true` only if the user asks; a done card is quiet and hides itself after two minutes.

## Tone
- Direct and specific. The card is an instruction, not a request: `Ctrl+click ONE item`, not `Could you please try clicking an item?`.
- No cheering, no "great job". A captured step is reported as a fact: `Captured: ItemCount 11 -> 12`.
- Own the misses: `Nothing lasting changed; the watch is probably too wide. Narrowing it to the tab's inventory.` Not `Please make sure you did it right`.
- Keep the user's time in mind: say up front how many actions it will take and stop when the evidence is in.
