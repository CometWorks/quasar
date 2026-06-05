# Quasar/Services/Auth/RbacConfig.cs

**Module:** Quasar.Services.Auth  **Kind:** class  **Tier:** 2

## Summary
Data model for the RBAC configuration persisted to `rbac.json`. Defines three kinds of access rules — subject-based role assignments, claim-based role assignments, and per-policy role overrides — together with deep-clone and normalisation helpers.

## Structure
Namespace: `Quasar.Services.Auth`

All types are `sealed`.

**`RbacConfig`**
- `SubjectRoleMappings : List<SubjectRoleMapping>` — explicit per-identity role grants
- `ClaimRoleMappings : List<ClaimRoleMapping>` — claim-value-based role grants (OIDC)
- `PolicyOverrides : Dictionary<string, List<string>>` — overrides roles required for a named policy
- `Clone() : RbacConfig` — deep copy
- `static Normalize(RbacConfig?) : RbacConfig` — trims, deduplicates, filters out blanks; sorts subject mappings by provider then subject; validates roles against `QuasarRoles.All`
- `internal static NormalizeRoles(IEnumerable<string>?) : List<string>` — shared role normalisation used by all three mapping types

**`SubjectRoleMapping`**
- `Provider` (default `"Steam"`), `Subject`, `Roles : List<string>`
- `RoleText` — `[JsonIgnore]` property for UI; getter joins roles, setter parses comma-separated string
- `Clone()`, `static Normalize(SubjectRoleMapping)`, `static ParseRoles(string?)`

**`ClaimRoleMapping`**
- `Provider` (default `"Oidc"`), `Claim`, `Value`, `Roles : List<string>`
- `Clone()`, `static Normalize(ClaimRoleMapping)`

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthConstants.cs`](QuasarAuthConstants.cs.md) — `QuasarAuthSchemes`, `QuasarRoles`
- `System.Text.Json.Serialization` — `[JsonIgnore]`
