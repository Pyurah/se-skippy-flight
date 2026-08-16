#!/usr/bin/env python
"""
wrap-mdk.py - wrap a flat-body Space Engineers PB script (the paste-into-the-block
form: top-level fields/methods/types, no usings, no namespace, no class) into the
MDK2 project form expected by Mal.Mdk2.PbPackager:

    <standard SE usings>
    namespace IngameScript {
        public partial class Program : MyGridProgram {
            <the entire flat body, verbatim>
        }
    }

The body is copied byte-for-byte from the source .cs so no logic, comment, or literal
is altered. The packager strips the usings/namespace/class wrapper back off at pack
time (the in-game PB re-adds them), so the deployed script is identical in meaning to
the hand-pasted original - just compiled by Roslyn and minified by MDK2 instead of the
old comment-stripper.

Usage:
    python tools/wrap-mdk.py <source.cs> <project/Program.cs>
"""

import sys

# The exact using set the mdk2pbscript template emits. Matching it keeps the Roslyn
# build seeing the same API surface the in-game compiler exposes.
USINGS = """using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;
"""

HEADER = USINGS + "\nnamespace IngameScript\n{\n    public partial class Program : MyGridProgram\n    {\n"
FOOTER = "\n    }\n}\n"


def main():
    if len(sys.argv) != 3:
        print("usage: python tools/wrap-mdk.py <source.cs> <project/Program.cs>", file=sys.stderr)
        return 2
    src_path, out_path = sys.argv[1], sys.argv[2]
    with open(src_path, "r", encoding="utf-8") as f:
        body = f.read()
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        f.write(body)
        f.write(FOOTER)
    print(f"wrapped {src_path} ({len(body):,} chars body) -> {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
