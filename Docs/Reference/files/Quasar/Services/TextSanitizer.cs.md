# Quasar/Services/TextSanitizer.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Small static utility that strips characters unsuitable for display in the Quasar UI from game-originated text (player names, chat messages, etc.). It iterates Unicode runes and drops replacement characters (U+FFFD), control characters, private-use code points, surrogates, and unassigned code points, then trims surrounding whitespace.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `TextSanitizer` (static class)

| Member | Description |
|---|---|
| `CleanGameText(value)` | Returns sanitized string; returns empty string for null/empty input. |
| `ShouldDrop(rune)` (private static) | Returns true for U+FFFD and any rune in Control, PrivateUse, Surrogate, OtherNotAssigned Unicode categories. |

## Dependencies
- None (BCL only: `System.Text.StringBuilder`, `System.Text.Rune`, `System.Globalization.UnicodeCategory`)
