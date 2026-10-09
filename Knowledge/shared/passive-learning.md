# Passive learning (both HUDs)

How an agent learns while the user plays, without asking for anything: the HUD observes, the agent wakes on events, maps what is new, and keeps findings and knowledge current. The user only plays.

## The loop
1. **Ask once, then turn it on:** `observe action=start`.
   - Tell the user what is noted: UI panels, areas, levels, entity kinds. Read-only.
   - It stays on across HUD restarts until `observe action=stop`.
2. **Wait for something to learn:** run `observe_wait since=<seq>` in the background.
   - In Claude Code, run `tools\mcp-call.ps1 observe_wait since=<seq> -TimeoutSec 3700` as a background task.
   - It wakes you for a panel the HUD doesn't map opening for the first time, an area change, or a level up. Pass `kinds` to widen that.
   - Don't poll.
3. **Triage what woke you, cheapest first.** See "What to do with an event" below.
4. **Record** what you learned (see "Recording"). Then go back to step 2 with the new `seq`.

## What to do with an event
- **Unmapped panel:**
  - Its `texts` usually name it ("Character", "Passive Skill Tree", "Waystones"…).
  - If it is still open, run `memory_layout address=<address>` and `explore_object` on `IngameUi.Children[<index>]`. If not, the event's 512-byte `snapshot` is what you have.
  - Compare repeat openings: what is constant is structure, and what differs is state.
  - Check the HUD for a property that should map it (`hud_type IngameUIElements`). A missing or wrong one is a HUD gap worth a finding.
- **Area / level:** good moments to re-check facts that change with progress, such as stats, quest and waypoint state, passive points, or what the area's entities look like.
- **Entity kinds:** a new metadata prefix (league mechanics, NPCs, chests) is a candidate for `explore_object` while it is near.

## Recording
- **Findings:** add facts to `Knowledge/findings.json` as `unverified`, with the evidence you have and a `check` someone can run. Promote them to `verified` only with proof: a population, a code path, or a one-variable experiment.
  - For facts that need an action, queue the experiment (`experiment_queue`, with a note). Never interrupt play for it.
- **Knowledge packs:** short and factual. Add one when you verified something other agents would otherwise rediscover.

## Updating yourself while the user plays
- **Allowed any time:** MCP tools, knowledge and findings. Use the usual dev cycle (branch → PR → CI → merge → bump the pointers), then restart the MCP server. That doesn't touch the game.
- **Bridge (HUD plugin) changes need a HUD restart,** which interrupts the overlay for about 15 s. `reload_plugin` cannot reload the bridge itself.
  - Ask the user once whether you may restart mid-play. If yes, restart when needed (`tools\restart-hud.ps1`) and say so in one line.
  - If not, batch the changes and restart at their next break.
- **Never:** send input, write memory, or edit HUD config while it runs.

## Cost
- Every wake costs tokens, so keep the default triggers.
- Handle a batch of events per wake, not one at a time.
- Stop observing (`observe action=stop`) when the session ends.
