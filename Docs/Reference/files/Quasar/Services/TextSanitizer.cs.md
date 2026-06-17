# Quasar/Services/TextSanitizer.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Small static utility that strips characters unsuitable for display in the Quasar UI from game-originated text (player names, chat messages, etc.). It iterates Unicode runes, explicitly drops Space Engineers platform icons (`U+E030` PC, `U+E031` PlayStation, `U+E032` Xbox), drops replacement characters (U+FFFD), control characters, private-use code points, surrogates, and unassigned code points, purges leading Unicode format/combining marks left before the real name, then trims surrounding whitespace.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `TextSanitizer` (static class)

| Member | Description |
|---|---|
| `CleanGameText(value)` | Returns sanitized string; returns empty string for null/empty input. |
| `ShouldDrop(rune)` (private static) | Returns true for U+FFFD, known SE platform icons, and any rune in Control, PrivateUse, Surrogate, OtherNotAssigned Unicode categories. |
| `ShouldDropLeadingPrefix(rune)` (private static) | Returns true for leading Format, NonSpacingMark, SpacingCombiningMark, or EnclosingMark runes before visible text begins. |
| `IsSpaceEngineersPlatformIcon(rune)` (private static) | Identifies the PC/PlayStation/Xbox platform icons used by Space Engineers player display names. |

## Dependencies
- None (BCL only: `System.Text.StringBuilder`, `System.Text.Rune`, `System.Globalization.UnicodeCategory`)
