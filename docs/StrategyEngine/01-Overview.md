# Strategy Engine: Overview

## 1. Executive Summary

The **Strategy Engine** is an extensible, programmable framework that revolutionizes the traditional text-based chat interaction model for the Everywhere AI Desktop Assistant. Instead of requiring users to manually type queries and describe their current screen state, the engine autonomously analyzes the visual context (active application, selected elements, files, etc.) and presents contextually relevant actions (Strategies) that can be executed with a single click.

### Key Innovation

```text
Traditional Flow:    User → Type Query → AI Processes → Response
Strategy Engine:     Context → Evaluate Strategies → Render Options → User Clicks → Preprocess → AI Executes
```

### Design Principles

| Principle         | Description                                                                        |
| ----------------- | ---------------------------------------------------------------------------------- |
| **Context-First** | Actions are derived directly from the visual and OS context, not user input.       |
| **Zero-Friction** | One-click access to intelligent, intent-driven operations.                         |
| **Extensible**    | Simple Markdown-based configuration files let users define new strategies easily.  |
| **Composable**    | Multiple intelligence sources (providers) merge their strategies automatically.    |
| **Non-Invasive**  | Operates parallel to the existing assistant flow without disrupting standard chat. |

## 2. Problem Statement

### Current Limitations

1. **High Friction**: Users must explicitly formulate their intent as text queries.
2. **Context Blindness**: Users manually describe what they are looking at (e.g., "translate this page").
3. **Repetitive Patterns**: Identical queries are typed repeatedly for similar contexts.
4. **Discovery Problem**: Users don't inherently know what capabilities the assistant possesses at any given moment.

### Target User Experience

When a user opens a specific application or selects an item, the Strategy Engine instantly projects tailored capabilities. 

```text
┌────────────────────────────────────────────────────────────┐
│  User is on arxiv.org viewing a research paper             │
│                                                            │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  Everywhere Assistant                          ─ □ × │  │
│  ├──────────────────────────────────────────────────────┤  │
│  │                                                      │  │
│  │  Detected: Academic Paper (arxiv.org)                │  │
│  │                                                      │  │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐  │  │
│  │  │  Summarize   │ │   Explain    │ │    Find      │  │  │
│  │  │    Paper     │ │   Methods    │ │   Related    │  │  │
│  │  └──────────────┘ └──────────────┘ └──────────────┘  │  │
│  │                                                      │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────┘
```
