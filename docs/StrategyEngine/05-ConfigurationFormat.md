# Configuration Format (`strategy.md`)

The fundamental unit of user extensibility in the Strategy Engine is the `strategy.md` file format.

This format provides a simple, structured way to define both the declarative structure of a Strategy (its metadata, matching conditions, and preprocessors) and the conversational payload (its system or user prompt template), utilizing standard Markdown mixed with YAML frontmatter.

## 1. Directory Structure and ID Resolution

Strategies are conventionally stored in `~/.everywhere/`. The engine translates folder names or file names directly into the ID schema.

For example:
*   `~/.everywhere/summarizer/strategy.md` becomes the Strategy ID: `user.summarizer`.
*   `~/.everywhere/explain-code/strategy.md` becomes the Strategy ID: `user.explain-code`.

## 2. Structure of `strategy.md`

A `strategy.md` file consists of two sections separated by `---`:

1.  **YAML Frontmatter**: Defines `Name`, `Description`, `Icon`, `Priority`, `Preprocessors`, and the conditional matching logic.
2.  **Markdown Body**: Defines the prompt template (`Body`), containing interpolation variables that match the Preprocessor outputs.

### Example Config

```yaml
---
name: "Explain PDF Section"
description: "Explain selected text in a PDF document"
icon: "Sparkles"
priority: 50
preprocessors:
  - "clipboard-text"
  - "pdf-metadata"
conditions:
  all:
    - process: "Acrobat.exe"
    - hasTextSelection: true
---

Please explain the following text that I selected from the PDF document titled "{PdfTitle}":

{ClipboardText}

Provide your explanation in simple terms, focusing on the core concepts.
```

## 3. Metadata Fields

*   `name`: The display text shown in the UI.
*   `description`: The subtitle/tooltip text shown in the UI.
*   `icon`: The name of the icon to render (lucide string or similar identifier).
*   `priority`: An integer (default is usually 10-50). Higher values appear first, and override strategies with identical IDs from other Providers.
*   `preprocessors`: An array of string keys referring to registered `IStrategyPreprocessor` components that must run before execution.

## 4. Conditional DSL

The `conditions` section defines the hierarchical ruleset controlling visibility. It uses Boolean grouping (`all`, `any`, `not`) holding array conditions.

```yaml
conditions:
  any:
    - process: "chrome.exe"
    - all:
      - process: "code.exe"
      - regex: ".*\\.(cs|ts)$"
```

## 5. Body Interpolation

The Markdown body below the frontmatter serves as the core instruction passed to the LLM. Variables mapped from preprocessors must be enclosed in curly braces `{}`. Missing variables at runtime (if preprocessors fail to populate them) currently result in the interpolation string remaining unmodified or throwing an execution halt (depending on engine rules).
