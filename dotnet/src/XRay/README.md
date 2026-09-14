![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray-content/main/dotnet/src/XRay.Content/logo.png)

# X-Ray

[![NuGet](https://img.shields.io/nuget/v/X-Ray?label=NuGet&color=5c2d91&logo=nuget&logoColor=white)](https://www.nuget.org/packages/X-Ray/)
[![Downloads](https://img.shields.io/nuget/dt/X-Ray?label=downloads&color=5c2d91)](https://www.nuget.org/packages/X-Ray/)
[![License](https://img.shields.io/badge/license-MIT%20AND%20Apache--2.0-007ec6)](https://github.com/curiosity-ai/x-ray-content/blob/main/dotnet/THIRD_PARTY_NOTICES.md)

The umbrella package for the **X-Ray** family of .NET libraries.

**This package contains no code.** Installing it brings in every X-Ray package, so a consumer
who wants the whole family writes one line instead of tracking the list as it grows:

```sh
dotnet add package X-Ray
```

## What it installs

| Package | What it does |
|---|---|
| [`X-Ray.Content`](https://www.nuget.org/packages/X-Ray.Content/) | Content extraction — text, metadata, tables, images and a structured element tree from PDF, Office, HTML, email, archives and 90+ more formats. Pure managed, with opt-in OCR. |

More family members will appear here as they ship, and this package will pull them in without
you changing anything but the version.

## Or take just the one you need

Every member is independently installable, and nothing is lost by doing so — the umbrella is a
convenience, not a prerequisite:

```sh
dotnet add package X-Ray.Content
```

Prefer this if you only need one member: it keeps your dependency graph to what you actually
use, and it means a new family member cannot arrive in your build without you asking for it.

## Versioning

The family is published together from one pipeline run, so an `X-Ray` version and the
`X-Ray.Content` version it depends on always come from the same commit. Versions are CalVer —
`yy.M.<build>`.

## Links

- [Source](https://github.com/curiosity-ai/x-ray-content) — the family lives under `dotnet/`
- [Architecture, scope and conventions](https://github.com/curiosity-ai/x-ray-content/blob/main/CLAUDE.md)
- [Issues](https://github.com/curiosity-ai/x-ray-content/issues)

## License

`MIT AND Apache-2.0` — the expression covers what this package installs. The code is MIT,
except for a handful of files in `X-Ray.Content` that are derivative works of Apache-2.0-only
Rust crates and remain under that license; see
[THIRD_PARTY_NOTICES.md](https://github.com/curiosity-ai/x-ray-content/blob/main/dotnet/THIRD_PARTY_NOTICES.md)
for exactly which files and which upstreams.
