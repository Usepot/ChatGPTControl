# Product

## Register

Portable Codex Companion

## Users

Developers and operators who pair a local computer with a hosted ChatGPT/MCP relay. They use the companion to trust workspaces, configure Tailscale connectivity, approve sensitive requests, and inspect tool activity while the app stays mostly in the background.

## Product Purpose

Portable Codex is a local execution companion. It accepts authenticated web requests through the relay, applies workspace and approval boundaries, runs file and shell tools on the local machine, and reports results back to ChatGPT. Setup should feel guided and reassuring; steady-state operation should be quiet, observable, and low-maintenance.

## Brand Personality

Warm, guided, and technically trustworthy. The product should explain what it needs, why a permission or setup step is required, and whether the local bridge is healthy without making the operator read implementation details.

## Anti-references

Avoid a consumer productivity-app feel, a flashy monitoring dashboard, or a dense terminal-only control surface. Do not make the always-on companion demand attention when the relay and trust boundaries are healthy.

## Design Principles

- Guide setup one decision at a time, with actionable recovery when automation stops.
- Make trust boundaries and request state legible before exposing technical detail.
- Keep the background state quiet; elevate only failures, approvals, and actions that need the operator.
- Preserve the existing relay/tool contract so the desktop shell can evolve independently.

## Accessibility & Inclusion

Use platform-default semantics, readable contrast, visible status changes, keyboard-accessible setup controls, and clear text alternatives for icons. Full WCAG auditing and advanced assistive-technology parity are not v1 scope.
