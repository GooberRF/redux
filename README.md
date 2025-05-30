# REDUX - The RED User eXperience Toolkit
------
REDUX is a lightweight, command‑line toolkit designed to convert and export Red Faction geometry and object data between different Red Faction formats, and streamline and simplify asset prep and/or migration across RF and modern pipelines involving software like Blender or 3ds Max.

Usage:
------
- `redux.exe -input <file> -output <file> [options]`

Examples:  
------
- redux.exe -input dm02.rfl -output dm02.obj -ngons
- redux.exe -input mymesh.rfg -output mymesh.v3m
- redux.exe -input dmpc03.rfl -output dmpc03.rfg -brushes -textranslate -swapitem "Medical Kit"

Supported input formats:  
------  
- `RF Group (.rfg)`           Brushes from non‑moving groups
- `RF1/RF2 Level (.rfl)`      Static geometry or brushes from non‑moving groups, most object types (if output is .rfg)
- `Wavefront OBJ (.obj)`      Geometry objects with flags in object names

Supported output formats:  
------  
- `RF Group (.rfg)`           Brushes with flags (air, portal, detail, etc.), most object types (if input is .rfl)
- `RF Static Mesh (.v3m)`     Mesh with submeshes for each brush/geometry object (basic support)
- `Wavefront OBJ (.obj)`      Geometry objects with flags in object names
 
Arguments:
------
- `-input <file>`             Path to the input file
- `-output <file>`            Path to the output file
- `-swapitem <class>`         Overwrite all item classes in exported RFGs with the specified item class.
- `-ngons <bool>`             Allow n-sided polygons. If false, triangulate all polygons. Default: false
- `-textranslate <bool>`      RF2 → RF1 texture name translation. If false, keep original RF2 filenames. Default: false
- `-brushes <bool>`           Export brush data from RFL. If false, exports static geometry. Default: false
- `-geonodetail <bool>`       Remove detail flag from geoable brushes. Only applies for brushes from RF2 RFLs. Default: false
- `-portalfaces <bool>`       Include portal faces. Default: false
- `-detailfaces <bool>`       Include faces from detail brushes. Default: true
- `-alphafaces <bool>`        Include faces with alpha textures. Default: true
- `-holefaces <bool>`         Include faces with shoot-through alpha textures. Default: true
- `-liquidfaces <bool>`       Include liquid surfaces. Default: false
- `-skyfaces <bool>`          Include Show Sky faces. Default: false
- `-invisiblefaces <bool>`    Include invisible faces. Default: false
- `-ver` / `help` / `-h`      Print version/usage/help information
- `-loglevel <level>`         Set verbosity level (`debug`, `dev`, `info`, `warn`, `error`) or (0–4). Defaults to `info`

Notes:  
------
- Boolean options accept `true`, `false`, `1`, `0`. If no value is provided, presence of the option is treated as `true`.

Credit:  
------
- REDUX is developed by Chris "Goober" Parsons
- Thanks to [rafalh](https://github.com/rafalh/rf-reversed), [wardd64](https://github.com/wardd64/UnityFaction), [natarii](https://github.com/natarii), and [Marisa-Chan](https://github.com/Marisa-Chan/RF2_rfl_rfc) for format research.
