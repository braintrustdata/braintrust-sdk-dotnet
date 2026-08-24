# NSwag Liquid Template Patches

Templates here are forked from **NSwag 14.7.1** (`NJsonSchema.CodeGeneration.CSharp`)
and override the embedded originals via `/templateDirectory`. They contain the
minimum patches needed for undiscriminated Braintrust unions to round-trip.

When bumping the `NSwag.MSBuild` version, extract the new upstream template and
diff it against the file here, then re-apply the patches below. Each patch is
marked with a `BRAINTRUST PATCH` comment in the template, so `grep` finds them.

To extract the upstream original:

```csharp
var asm = Assembly.LoadFrom("<nuget>/nswag.msbuild/<ver>/tools/Net80/NJsonSchema.CodeGeneration.CSharp.dll");
var name = "NJsonSchema.CodeGeneration.CSharp.Templates.JsonInheritanceConverter.liquid";
Console.Write(new StreamReader(asm.GetManifestResourceStream(name)).ReadToEnd());
```

---

## JsonInheritanceConverter.liquid

**Upstream:** `NJsonSchema/CodeGeneration/CSharp/Templates/JsonInheritanceConverter.liquid` @ 14.7.1

Only the `UseSystemTextJson` branch is patched; the Newtonsoft branch is untouched.

### Patch 1 — do not write the discriminator twice

`Write` emits the discriminator explicitly, then copies every property of the
serialized instance. The spec normalizer keeps the discriminator off the subclass
(it belongs to the base), so on a round-trip it arrives in `[JsonExtensionData]`
and the copy loop emits it a second time — producing a duplicate JSON key such as
`{"type":"function","id":"abc","type":"function"}`. Fixed by skipping the property
whose name equals the discriminator while copying.

### Patch 2 — an unknown discriminator falls back to the base type

`GetDiscriminatorType` throws `InvalidOperationException` when no variant matches.
Against a live API that is normal — a `event_type` added server-side is unknown to
an already-shipped client — and because the throw happens mid-array, **one unknown
variant fails the entire response**. sdk-java hit exactly this on automation
listing pages. Fixed by returning the base type instead of throwing.

### Patch 3 — do not recurse on the base type

Patch 2 makes the resolved subtype equal the type being converted, and the existing
code then calls `JsonSerializer.Deserialize(..., subtype, options)`, which re-enters
this converter and recurses until the stack overflows. (Upstream has the same latent
bug on its `objectType.Name == discriminatorValue` path.) Fixed with a `ReadAsBase`
helper that materializes the base directly and fills its `[JsonExtensionData]`
member from the payload, so an unknown variant stays fully readable.

This relies on generated bases declaring no members of their own beyond extension
data — an invariant the spec normalizer enforces, since it builds each base as
`{type: object, discriminator: ...}` and leaves all properties on the subclasses.

### Patch 4 — write the base back without recursing or renaming the variant

Patches 2 and 3 keep an unknown variant readable, but `Write` was still broken for
it, in two ways. `SerializeToUtf8Bytes((object)value, options)` resolves the runtime
type, which for a base instance is the type carrying
`[JsonInheritanceConverter]` — so it re-enters this converter and recurses until the
stack overflows. And the discriminator came from
`GetDiscriminatorValue(value.GetType())`, where no `JsonInheritanceAttribute` matches
a base, so it fell through to `type.Name` while Patch 1 dropped the real value from
`[JsonExtensionData]` — rewriting `{"event_type":"brand_new_kind"}` as
`{"event_type":"ProjectAutomationConfig"}`.

Fixed with a `WriteAsBase` helper, the mirror of `ReadAsBase`: an instance whose type
has no declared discriminator is written straight from its extension data, so it
round-trips exactly as it arrived. Known subtypes keep the existing path. This leans
on the same memberless-base invariant as Patch 3.

Covered by `tests/Braintrust.Sdk.Api.Generated.Tests/UnionRoundTripTests.cs`.
