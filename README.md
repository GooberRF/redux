# REDUX - The RED User eXperience Toolkit
------

Usage:
------
- `redux.exe -input <file> -output <file> [options]`

Examples:  
------
- `redux.exe -input mygroup.rfg -output mygroup.obj`
- `redux.exe -input dm02.rfl -output dm02.obj -detailfaces true -invisiblefaces false`
 
Arguments:
------
- `-input`     Path to the input file
- `-output`    Path to the output file
- `-ver` / `--version` / `-v`  
              Print version information
- `-help` / `-h`  
              Print usage information
- `-loglevel <level>`  
              Set verbosity level (`debug`, `info`, `warn`, `error`) or (0–3). Defaults to `info`.

Additional arguments available during RFL → OBJ conversion:
- `-portalfaces <bool>`      Include portal faces. Default: false
- `-detailfaces <bool>`      Include faces from detail brushes. Default: true
- `-alphafaces <bool>`       Include faces with alpha textures. Default: true
- `-holefaces <bool>`        Include faces with shoot-through alpha textures. Default: true
- `-liquidfaces <bool>`      Include liquid surfaces. Default: false
- `-skyfaces <bool>`         Include Show Sky faces. Default: false
- `-invisiblefaces <bool>`   Include invisible faces. Default: false

Notes:  
------
- Converts Red Faction map/mesh data between supported formats.
- Current version supports `.rfg` or `.rfl` (RF1 and RF2) as input, and `.obj` or `.rfg` as output.
- Useful for importing/exporting RF content in Blender or other tools.
- Built to streamline asset prep and migration across RF and modern pipelines.
- Thanks to [rafalh](https://github.com/rafalh/rf-reversed), [wardd64](https://github.com/wardd64/UnityFaction), [natarii](https://github.com/natarii), and [Marisa-Chan](https://github.com/Marisa-Chan/RF2_rfl_rfc) for format research.
