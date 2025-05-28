# REDUX - The RED User eXperience Toolkit
------

Usage:
------
- `redux.exe -input <file> -output <file> [options]`

Examples:  
------
- redux.exe -input mygroup.rfg -output mygroup.obj
- redux.exe -input dm02.rfl -output dm02.obj -ngons -brushes
- redux.exe -input mymesh.obj -output mymesh.rfg
 
Arguments:
------
- `-input <file>`             Path to the input file
- `-output <file>`            Path to the output file
- `-ver` / `--version` / `-v` Print version information
- `-help` / `-h`              Print usage information
- `-loglevel <level>`         Set verbosity level (`debug`, `info`, `warn`, `error`) or (0–3). Defaults to `info`
- `-ngons <bool>`             Allow n-sided polygons. If false, triangulate all polygons. Default: false
- `-textranslate <bool>`      Enable RF2 → RF1 texture name translation. If false, keep original RF2 filenames. Default: false
- `-brushes <bool>`           Export brush data from RFL. If false, exports static geometry. Default: false
- `-geonodetail <bool>`       Remove detail flag from all geoable brushes. Only applies for brushes from RF2 RFLs. Default: false
- `-portalfaces <bool>`       Include portal faces. Default: false
- `-detailfaces <bool>`       Include faces from detail brushes. Default: true
- `-alphafaces <bool>`        Include faces with alpha textures. Default: true
- `-holefaces <bool>`         Include faces with shoot-through alpha textures. Default: true
- `-liquidfaces <bool>`       Include liquid surfaces. Default: false
- `-skyfaces <bool>`          Include Show Sky faces. Default: false
- `-invisiblefaces <bool>`    Include invisible faces. Default: false

Notes:  
------
- Boolean options accept `true`, `false`, `1`, `0`. If no value is provided, presence of the switch is treated as `true`.
- Converts Red Faction map/mesh data between supported formats.
- Current version supports `.rfg` or `.rfl` (RF1 and RF2) as input, and `.obj` or `.rfg` as output.
- Useful for importing/exporting RF content in Blender or other tools.
- Built to streamline asset prep and migration across RF and modern pipelines.

Credit:  
------
- REDUX is developed by Chris "Goober" Parsons
- Thanks to [rafalh](https://github.com/rafalh/rf-reversed), [wardd64](https://github.com/wardd64/UnityFaction), [natarii](https://github.com/natarii), and [Marisa-Chan](https://github.com/Marisa-Chan/RF2_rfl_rfc) for format research.
