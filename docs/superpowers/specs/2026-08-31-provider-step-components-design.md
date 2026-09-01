# Reusable Provider Step Components

**Date:** 2026-08-31
**Status:** Approved design

## Goal

Make every setup step on the AWS provider page use the same visual and interaction
structure, then preserve that structure as reusable Blazor components for future provider
pages. A provider page should supply its business-specific status, summary, forms, scripts,
and callbacks without recreating the card shell or setup behavior.

## Scope

This change will:

- move each AWS step's heading and description inside a shared step card;
- migrate Access, IAM Identity Center, and Per-user permissions to that component;
- standardize configured, editing, unconfigured, warning, failed, and unknown states;
- standardize the collapsed easy-guide and inline reset interactions;
- add component-level and AWS-page regression coverage; and
- verify the authenticated page at desktop and narrow widths.

This change will not redesign the surrounding administration layout, change AWS setup
semantics, alter stored settings, or convert provider-specific forms and scripts into a
generic configuration schema.

## Architecture

The provider page remains the business-state owner. It reads provider status, owns edit
flags and form values, runs save and refresh callbacks, and supplies provider-specific
content. Three presentational components enforce the shared interaction language.

### `ProviderStepCard`

The card owns the visible anatomy of a setup step:

1. a status rail whose semantic color reflects the requirement status;
2. a header containing the title, description, status icon, and status label;
3. a compact summary of saved or detected values;
4. a consistently placed action row; and
5. an expanded setup body separated from the summary by a divider.

The component accepts:

- a stable element id;
- title and description text;
- `RequirementStatus` and a concise status label;
- controlled expanded state and an expanded-state callback;
- optional `Summary`, `ManualContent`, `EasyContent`, and `Actions` render fragments; and
- an optional easy-guide title.

The page controls whether the card is expanded so same-page actions can open a specific
step and business state remains observable in tests. The card controls markup, CSS classes,
ARIA relationships, and fragment placement. It does not know about AWS or persistence.

### `ProviderSetupGuide`

This component renders the easy script, guided scan, or walkthrough as a native disclosure.
It starts closed whenever its parent setup body is shown and uses the same summary styling,
spacing, and focus treatment for every provider. Manual content never goes inside this
disclosure.

### `ProviderResetAction`

This component owns the inline reset interaction: the initial destructive action, explicit
confirm and cancel actions, and optional explanatory text. It invokes a supplied callback
only after confirmation. Provider pages may render more than one reset action when a step
contains independently stored values, as Per-user permissions does.

## State Behavior

- A satisfied step is collapsed by default and shows its current-value summary and actions.
- Choosing **Edit setup** expands the body without clearing saved values.
- A not-configured, failed, warning, provisioning, or unknown step is expanded by default.
- Whenever the body is expanded, manual fields and instructions are visible immediately.
- The easy guide remains collapsed until the administrator opens it.
- Refresh and save operations keep their current business behavior and report results in
  the relevant supplied content or existing page alert.
- Reset clears only the values named by that reset action. The page refreshes requirement
  state after the callback succeeds.

Semantic presentation is consistent across all cards:

| State | Rail and icon intent | Default body |
| --- | --- | --- |
| Satisfied | Success | Collapsed |
| Warning or provisioning | Warning | Expanded |
| Failed | Error | Expanded |
| Unknown or not configured | Neutral | Expanded |

## Visual Design

The change extends the existing Connapse theme instead of introducing a separate provider
theme. The compact token set is:

- surface: `#1a1a28`;
- border: `#2a2a3e`;
- primary text: `#e4e2ee`;
- muted text: `#8b89a0`;
- accent: `#8b5cf6`; and
- success: `#22c55e`.

CSS references the existing semantic variables rather than duplicating these values. Error
and warning presentation uses the existing Bootstrap/Connapse semantic tokens.

Segoe UI remains the interface face. The existing monospace stack remains reserved for AWS
identifiers, command output, and scripts. Titles use the same size and weight in every card;
descriptions use one muted text treatment; summaries use compact labels and wrap long values.

The signature element is the thin status rail along the card edge. It communicates real
state and gives the three-step process a stable visual rhythm without adding numbering or
decorative chrome.

```
+-- status rail -------------------------------------+
| Title                              Status          |
| Description                                         |
|                                                     |
| Current-value summary                               |
| [Refresh] [Edit setup] [Reset this step]            |
+-- expanded setup body ------------------------------+
| Manual values or manual instructions                |
|                                                     |
| > Easy setup or guided scan                         |
+-----------------------------------------------------+
```

At narrow widths, status and actions wrap beneath the title without changing their order.
Long ARNs and URLs wrap rather than forcing horizontal page scrolling. The components add no
decorative animation; native disclosure behavior works with reduced-motion preferences.

## AWS Migration

### Access

The summary shows whether Connapse can read S3 and the current principal detail. Recheck,
edit/replace, and reset occupy the shared action area. Manual access-key fields remain visible
in the expanded body, while the CloudShell identity script moves into `ProviderSetupGuide`.

### IAM Identity Center

The summary uses the same compact key/value treatment for region, instance ARN, and identity
store id. Change/scan and reset occupy the shared action area. The existing settings form is
the manual content; the read-only CloudShell scan moves into `ProviderSetupGuide`.

### Per-user permissions

The summary shows the SAML entity id and optional default grant group. Edit setup occupies the
shared action area. The manual AWS instructions and SAML settings form remain visible in the
expanded body. The end-to-end guided walkthrough moves into `ProviderSetupGuide`. Separate
SAML-application and default-group reset controls remain available through reusable reset
actions because those values can be cleared independently.

## Accessibility

Each card is a labelled section whose heading id derives from the stable step id. The edit
control exposes `aria-expanded` and points at the setup body's id. Status icons remain hidden
from assistive technology when adjacent text already names the state. Buttons retain visible
keyboard focus, disclosure uses native `<details>`/`<summary>` behavior, and color is never the
only status signal.

## Error Handling

The shared components do not catch or reinterpret provider errors. Save, scan, copy, and reset
callbacks continue to report their provider-specific outcomes through the page or supplied
content. After invoking its callback, the reset component returns to its initial state; any
failure remains visible through the page's existing result message.

## Testing

Component tests will cover:

- semantic status classes and labels for every `RequirementStatus`;
- configured collapsed and editing expanded rendering;
- named-fragment placement;
- manual content remaining outside the easy-guide disclosure;
- reset requiring confirmation and supporting cancellation; and
- section, heading, `aria-expanded`, and `aria-controls` relationships.

AWS page tests will cover:

- all three steps rendering through `ProviderStepCard`;
- completed steps omitting setup guidance until edited;
- incomplete steps showing manual content while easy guides remain closed;
- existing same-page actions opening the correct card;
- independent per-user reset actions; and
- current provider/script behavior remaining unchanged.

After the normal build and test suites pass, the web container will be rebuilt and only the
web service recreated. The authenticated AWS page will be checked in its completed and editing
states at desktop and narrow widths.
