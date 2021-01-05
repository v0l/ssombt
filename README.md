# ssombt

```
ssombt:
  Synology Surveillance Station Optical Media Backup Tool

Usage:
  ssombt [options]

Options:
  --source-paths <source-paths>    The camera directory(s) to build disk images for
  --output-path <output-path>      The output directory to save temp files and output ISO image
  --delete                         Delete temp files after creating ISO image (default=false)
  --password <password>            Encrypt files with 7z (default=null)
  --disk-size <disk-size>          Optical media size - g(iga)/m(ega)/(k)ilo bytes (default=25G)
  --parity <parity>                The percentage of the optical disk to use for parity data (default=10)
  --files                          Compress the files instead of directories (default=false)
  --version                        Show version information
  -?, -h, --help                   Show help and usage information
```