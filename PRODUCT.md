# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Single self-contained HTML file, directly openable in a browser.

## Users

An algorithm course student investigating Slay the Spire 2 combat, plus classmates reviewing the decision model.

## Product Purpose

Help users enter the visible combat state and compare card-play choices against enemy intent, while explaining the scoring rule and its limits for a course report.

## Capabilities and Constraints

- Include all three Act 3 boss encounters and allow current HP, block, intent, and player resources to be edited.
- Support solo and co-op combat with one to four player state panels and a shared enemy field.
- Search legal card subsets and target choices from the currently entered hands using energy, hit damage, hit count, block, and visible incoming damage.
- Label simplifications clearly. The tool is a transparent current-turn decision aid, not an automated in-game solver or full-run RL policy.
- Use REA to inspect the locally installed game files. Distinguish REA-derived structure from community reference values and model assumptions.
- Use Traditional Chinese in the interface.
- The user selected a visual-comp-first workflow.
- The user asked to study `rank-Yu/sts2-solver` and `zhiyue/sts2-rl-agent` as open-source references, and selected solo plus multiplayer support.
