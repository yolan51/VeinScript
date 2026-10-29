# VeinScript primitive icons

Sixteen transparent PNGs, split from `../veinscript-primitives-icons.png` for direct use in the
editor. Every file has a 314 x 314 canvas, including a one-pixel transparent edge where necessary,
so UI code can use a uniform image size without cropping or atlas-coordinate logic.

Use the filenames as stable asset identifiers. `manifest.json` provides the same mapping in a
machine-readable form.

| File | VeinScript concept | Artwork |
| --- | --- | --- |
| `shape.png` | `$Shape` | cyan cube in a node frame |
| `mark.png` | mark | orange tag |
| `event.png` | event | gold heartbeat signal |
| `builder.png` | builder | purple tools |
| `shard.png` | shard | violet crystal |
| `publicator.png` | publicator | globe with upload arrow |
| `bridge.png` | bridge | blue bridge |
| `identity.png` | identity | cube in a linked halo |
| `bundle.png` | bundle | open box of blocks |
| `import.png` | import / `use` | document with download arrow |
| `target.png` | target | sight with magnifier |
| `fold.png` | fold | blue folder |
| `tick.png` | tick / frame | clock with loop arrow |
| `function.png` | function / route | green flow graph |
| `diagnostic.png` | diagnostic | warning console |
| `dependency.png` | dependency | directed node graph |

All backgrounds are genuinely transparent. The original atlas remains alongside this directory as
the source reference.
