# REDUX - The RED User eXperience Toolkit
------
REDUX is a lightweight, command‑line toolkit designed to convert and export Red Faction geometry and object data between different Red Faction formats, and streamline and simplify asset prep and/or migration across RF and modern pipelines involving software like Blender or 3ds Max.

Usage:
------
- `redux.exe -input <file> -outformat <fmt> [options]`

Examples:  
------
- redux.exe -input dm02.rfl -outformat obj -ngons
- redux.exe -input mymesh.rfg -outformat v3m
- redux.exe -input dmpc03.rfl -outformat rfg -brushes -textranslate -itemswap "Medical Kit"
- redux.exe -input dm02.rfl -outformat rfg -geomirror X -ngons -brushes

Supported input formats:
------  
- `RF Group (.rfg)`                    Brushes from non‑moving groups
- `RF1/RF2 Level (.rfl)`               Static geometry or brushes from non‑moving groups, most object types (if output is .rfg)
- `RF Static Mesh (.v3m)`              Mesh with submeshes, LODs, prop points, and cspheres
- `RF Character Mesh (.v3c)`           Mesh with submeshes, LODs, prop points, and cspheres
- `Wavefront OBJ (.obj)`               Geometry objects with flags in object names
- `RF Static Mesh (.rfm)`              Mesh with submeshes, LODs, prop points, and cspheres (partial support)
- `RF Character Mesh (.rfm)`           Mesh with submeshes, LODs, prop points, and cspheres (partial support)
- `RF Animation (.rfa)`                Animation data (partial support)
- `RF2 Texture Packfile (.peg)`        Textures

Supported output formats:
------  
- `RF Group (.rfg)`                    Brushes with flags (air, portal, detail, etc.), most object types (from RFL, RFG, V3M, OBJ)
- `RF Static Mesh (.v3m)`              Mesh with submeshes for each brush/geometry object (from RFG, V3M, OBJ)
- `Wavefront OBJ (.obj)`               Geometry objects with flags in object names (from RFL, RFG, V3M, OBJ)
- `Portable Network Graphics (.png)`   Image (extract from PEG)
- `TARGA (.tga)`                       Image (extract from PEG)
 
Arguments:
------
- `-input <file>`                      Path to the input file.
- `-outformat <file>`                  Output format.
- `-itemswap <class>`                  Overwrite all item classes in exported RFGs with the specified item class.
- `-coronaswap <class>`                Replace all RF2 coronas with clutter objects of the specified class when converting to RFG.
- `-geomirror <X/Y/Z>`                 Mirror geometry and (most) objects across the provided global axis when converting to RFG.
- `-flipnormals <bool>`                Flip face normals during conversion. Default: false
- `-ngons <bool>`                      Allow n-sided polygons. If false, triangulate all polygons. Default: false
- `-simplenames <bool>`                Use simple brush name Brush_UID. If false, include brush flags in name. Default: false
- `-textranslate <bool>`               RF2 → RF1 texture name translation. If false, keep original RF2 filenames. Default: false
- `-texprefix <bool>`                  Change RF2 texture prefixes not supported by RF1. If false, keep original RF2 prefixes. Default: false
- `-brushes <bool>`                    Export brush data from RFL. If false, exports static geometry. Default: false
- `-geonodetail <bool>`                Remove detail flag from geoable brushes. Only applies for RF2 brushes. Default: false
- `-portalfaces <bool>`                Include portal faces. Default: true
- `-detailfaces <bool>`                Include faces from detail brushes. Default: true
- `-alphafaces <bool>`                 Include faces with alpha textures. Default: true
- `-holefaces <bool>`                  Include faces with shoot-through alpha textures. Default: true
- `-liquidfaces <bool>`                Include liquid surfaces. Default: false
- `-skyfaces <bool>`                   Include Show Sky faces. Default: true
- `-invisiblefaces <bool>`             Include invisible faces. Default: true
- `-ver` / `-help` / `-h`              Print version/usage/help information
- `-loglevel <level>`                  Set verbosity level (`debug`, `dev`, `info`, `warn`, `error`) or (0–4). Defaults to `info`

Notes:  
------
- Boolean options accept `true`, `false`, `1`, `0`. If no value is provided, presence of the option is treated as `true`.
- Class options reference class names from in the corresponding .tbl file. Use quotation marks if it has spaces.
- This is a perpetually work-in-progress tool for RF community developers, and has a feature set driven by need.
- This tool may have (read: probably has) bugs. It also has features that are only partially supported for the time being, with intent to improve over time.
- I always welcome PRs if you'd like to contribute, and also always welcome feedback and bug reports via GitHub/Discord.

Credit:  
------
- REDUX is developed by Chris "Goober" Parsons
- Thanks to [rafalh](https://github.com/rafalh/rf-reversed), [wardd64](https://github.com/wardd64/UnityFaction), [natarii](https://github.com/natarii), and [Marisa-Chan](https://github.com/Marisa-Chan/RF2_rfl_rfc) for format research.
