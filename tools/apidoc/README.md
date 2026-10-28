# apidoc — the API reference generator

Writes `docs/api/` (one Markdown page per namespace, an index and a list of undocumented members)
from the `///` comments of `CwyLs.ModLoader` and `CwyLs.ModLoader.Core`, the two assemblies a mod
references.

## The one command

```
tools/apidoc/run.sh
```

It builds `src/CwyLs.ModLoader/CwyLs.ModLoader.csproj` (which builds `CwyLs.ModLoader.Core` too) with the XML
documentation switched on from the command line, into a scratch folder
(`$CWY_APIDOC_ARTIFACTS`, default `$TMPDIR/cwy-apidoc`), then runs `apidoc.py`. It never writes into
`src/*/bin` or `obj`, changes no project file and does not touch the game.

| Option | Effect |
|---|---|
| `--check` | Write nothing; exit 1 when `docs/api/` differs from a fresh run (a stale page is named on stderr) |
| `--from DIR` | Skip the build; `DIR` holds `CwyLs.ModLoader.dll`/`.xml` and `CwyLs.ModLoader.Core.dll`/`.xml` |

Run it after changing any public API or its comments, and commit the regenerated pages in the same
change. At CWY:LS Mod Loader 1.0 a re-run gives a clean diff against the 0.9 pages: the output is
deterministic (sorted, no timestamps, independent of argument order).

The build prints the compiler's documentation warnings (CS0419 ambiguous `cref`, CS1573 missing
`<param>`, CS1574 unresolved `cref`). They do not stop the run; fix them in the source.

## How it works

`apidoc.py` (Python 3, standard library only) reads two things per assembly:

- **the XML documentation file** next to the DLL: the text of every `///` comment, keyed by
  documentation ID (`M:CwyLs.ModLoader.Mods.CwyMod.Entry(CwyLs.ModLoader.Mods.ICwyModHelper)`);
- **the assembly's metadata tables**, read as bytes (ECMA-335 Partition II): types, members,
  signatures, visibility, constants, attributes. No .NET runtime, no reflection.

The metadata is needed because the compiler also writes the comments of `internal` and `private`
members into the XML file, and the XML says nothing about visibility. The metadata decides what a
mod can see (public, or protected on a type it can derive from) and gives each member its C#
signature and parameter defaults; the XML gives the words. A public member whose ID has no
`<member>` entry is listed as not documented.

What the pages contain:

- Types sorted by name; members grouped constructors, fields, properties, events, methods, each
  alphabetical. Enums get a name/value table.
- **mod API** marks every type a mod reaches from `CwyMod` through public signatures, plus the
  types that implement or derive from those (the event records behind `Subscribe<T>`).
- `<inheritdoc/>` is resolved against base types and interfaces in the two assemblies; a member
  whose source is a .NET type (`ToString`, `IEquatable<T>.Equals`) says so.
- `[CwyDeprecated]` and `[Obsolete]` show as **Deprecated** / **Obsolete** notes with their
  version, stage and replacement.
- Left out: compiler-generated members (record plumbing), property and event accessors (shown as
  `{ get; set; }`), and undocumented parameterless constructors.
- Nullable reference marks (`string?`) are not shown.

Every generated file starts with a marker comment. A run removes marked files it no longer
produces (a namespace that disappeared) and leaves any other file in `docs/api/` alone.

## Self-check

`apidoc.py` prints `XML entry matches no member in the metadata: <ID>` for every `<member>` whose
ID it could not rebuild from the metadata. Zero such lines means every documentation ID it builds
matches the compiler's spelling. `--strict` turns any such line into exit code 1.

Direct use:

```
python3 tools/apidoc/apidoc.py --out docs/api <bin>/CwyLs.ModLoader.dll <bin>/CwyLs.ModLoader.Core.dll
python3 tools/apidoc/apidoc.py --check --out docs/api <bin>/CwyLs.ModLoader.dll <bin>/CwyLs.ModLoader.Core.dll
```

Supported metadata: compressed (`#~`) tables as the C# compiler writes them. An assembly with
uncompressed (`#-`) tables stops the run with a message.
