# Quasar/Services/IdentifierSlug.cs

**Module:** Quasar.Services.Core  **Kind:** utility  **Tier:** 2

## Summary
Static slug helper for turning human-readable names into stable lowercase identifiers. It normalizes letters and digits, collapses whitespace, underscores, and hyphens into single hyphens, drops unsupported characters, trims edge hyphens, and can generate a unique slug by appending an incrementing numeric suffix.

## Structure
- **`IdentifierSlug.Create(string? source)`** — returns an identifier slug with only lowercase letters, digits, and single hyphens; returns an empty string when no usable characters remain.
- **`IdentifierSlug.CreateUnique(string? source, string fallback, Func<string, bool> exists)`** — creates a base slug, falls back when needed, and appends `-N` until the supplied `exists` predicate reports the candidate is available.

## Dependencies
- .NET `System.Text.StringBuilder`.
