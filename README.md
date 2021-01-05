# ssombt

## Example:

```
#!/bin/bash
_UID=$(id -u)
_GID=$(id -g)

mkdir ./ss
sudo mount -t cifs //192.168.1.10/surveillance/ -o username=admin,password=admin,uid=$_UID,gid=$_GID ./ss

$(which ssombt) \
 --source-paths ./ss/CAM_A ./ss/CAM_B \
 --output-path ~/.ssombt_temp \
 --delete \
 --password my_password \
 --disk-size 25G \
 --parity 10
```

## Usage:

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