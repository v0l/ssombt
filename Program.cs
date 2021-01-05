using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CCTV.Archiver
{
    class Program
    {
        const string OutFormat = ".mkv";
        const string OutFormatName = "matroska";

        static CancellationTokenSource ProgramExit { get; } = new CancellationTokenSource();

        static long? ParseSize(string s)
        {
            if (s.EndsWith("g", true, null) && long.TryParse(s[0..^1], out long gb))
            {
                return gb * (long)Math.Pow(1000, 3);
            }
            else if (s.EndsWith("m", true, null) && long.TryParse(s[0..^1], out long mb))
            {
                return mb * (long)Math.Pow(1000, 2);
            }
            else if (s.EndsWith("k", true, null) && long.TryParse(s[0..^1], out long kb))
            {
                return kb * (long)Math.Pow(1000, 1);
            }
            else if (long.TryParse(s[0..^1], out long b))
            {
                return b;
            }
            return null;
        }

        static bool HasDeps()
        {
            string[] deps;
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                deps = new string[] {
                    "ffmpeg",
                    "7z",
                    "par2",
                    "mkisofs"
                };
            }
            else
            {
                deps = new string[] {
                    "ffmpeg",
                    "7z",
                    "par2j64",
                    "ImgBurn"
                };
            }
            foreach (var dep in deps)
            {
                if (FindExe(dep) == null)
                {
                    Console.WriteLine($"Cant find dependency {dep}, please install it or add it to PATH");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Synology Surveillance Station Optical Media Backup Tool
        /// </summary>
        /// <param name="sourcePaths">The camera directory(s) to build disk images for</param>
        /// <param name="outputPath">The output directory to save temp files and output ISO image</param>
        /// <param name="delete">Delete temp files after creating ISO image (default=false)</param>
        /// <param name="password">Encrypt files with 7z (default=null)</param>
        /// <param name="diskSize">Optical media size - g(iga)/m(ega)/(k)ilo bytes (default=25G)</param>
        /// <param name="parity">The percentage of the optical disk to use for parity data (default=10)</param>
        /// <param name="files">Compress the files instead of directories (default=false)</param>
        /// <returns></returns>
        static async Task Main(string[] sourcePaths, string outputPath, bool delete = false, string password = null, string diskSize = "25G", double parity = 10, bool files = false)
        {
            if (!HasDeps()) return;

            var dSize = ParseSize(diskSize);
            if (dSize == null || dSize == 0)
            {
                Console.WriteLine($"Invalid disk size specified \"{diskSize}\"");
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) =>
            {
                ProgramExit.Cancel();
            };

            Func<List<CCTVDir>> fnWork = () => {
                var ret = new List<CCTVDir>();
                foreach (var baseDir in sourcePaths)
                {
                    ret.AddRange(GetDirs(baseDir, outputPath));
                }
                return ret;
            };

            // build work queue
            // Parsed date, 
            var work = fnWork();
            var pSem = new SemaphoreSlim(2);

            // transcode
            foreach (var dir in work.OrderBy(a => a.Date))
            {
                var df = dir.GetSourceFiles();
                if (df.Count() > 0)
                {
                    //check if we can make disks now
                    var cd = CreateDisks(fnWork(), delete, password, dSize.Value, parity);

                    foreach (var vid in df)
                    {
                        var finalOut = dir.MapToOutputFile(vid);
                        var tmpOut = Path.ChangeExtension(finalOut, $"{OutFormat}.tmp");
                        var tmpOutDir = Path.GetDirectoryName(tmpOut);
                        if (!Directory.Exists(tmpOutDir))
                        {
                            Directory.CreateDirectory(tmpOutDir);
                        }

                        if (!File.Exists(finalOut))
                        {
                            //await Transcode(vid, tmpOut);
                            await pSem.WaitAsync();
                            Console.WriteLine($"Transcoding: {vid}\n\tInto: {tmpOut}");
                            var tx = TranscodeNVENC(vid, tmpOut).ContinueWith((t) =>
                            {
                                if (t.IsFaulted)
                                {
                                    throw t.Exception;
                                }
                                pSem.Release();
                                File.Move(tmpOut, finalOut);
                                File.Move(vid, Path.ChangeExtension(vid, ".done"));
                            });
                        }
                        else
                        {
                            File.Move(vid, Path.ChangeExtension(vid, ".done"), true);
                        }
                        //File.Delete(vid);
                    }

                    //wait for create disks to finish if its not done yet
                    await cd;
                }
            }
        }

        static string FindExe(string name)
        {
            foreach (var pth in Environment.GetEnvironmentVariable("PATH").Split(new char[] { ';', ':' }))
            {
                foreach (var ext in new string[] { null, ".sh", ".exe" })
                {
                    var pTest = Path.Combine(pth, Path.ChangeExtension(name, ext));
                    if (File.Exists(pTest))
                    {
                        return pTest;
                    }
                }
            }
            return null;
        }

        static Task TranscodeNVENC(string file, string outFile)
        {
            return RunProgram(FindExe("ffmpeg"), $"-i {file} -c:v hevc_nvenc -preset fast -s 1920x1080 -y -rc constqp -qp 35 -f {OutFormatName} {outFile}", Path.GetDirectoryName(file));
        }

        static async Task Transcode(string file, string outFile)
        {
            var lastFPS = 0m;
            var lastBitrate = "N/A";
            var lastTotalSize = 0m;
            var lastOutTime = 0m;
            var lastSpeed = 0m;

            var p = new ProcessStartInfo(FindExe("ffmpeg"));
            p.Arguments = $"-i {file} -c:v hevc -preset fast -s 1920x1080 -progress - -y -f {OutFormatName} {outFile}";
            p.UseShellExecute = false;
            p.WorkingDirectory = Path.GetDirectoryName(file);
            p.RedirectStandardOutput = true;
            p.RedirectStandardInput = true;
            p.RedirectStandardError = true;
            p.CreateNoWindow = true;

            var px = Process.Start(p);
            ProgramExit.Token.Register(() => px.Kill());

            var tcs = new TaskCompletionSource<bool>();
            var readOutput = Task.Factory.StartNew(async () =>
            {
                using var sr = new StreamReader(px.StandardOutput.BaseStream);
                string line = null;
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    if (line.StartsWith("fps="))
                    {
                        lastFPS = decimal.TryParse(line.Split('=')[1], out decimal dx) ? dx : 0m;
                    }
                    if (line.StartsWith("bitrate"))
                    {
                        lastBitrate = line.Split('=')[1];
                    }
                    if (line.StartsWith("total_size"))
                    {
                        lastTotalSize = decimal.TryParse(line.Split('=')[1], out decimal dx) ? dx : 0m;
                    }
                    if (line.StartsWith("out_time_ms"))
                    {
                        lastOutTime = decimal.TryParse(line.Split('=')[1], out decimal dx) ? dx : 0m;
                    }
                    if (line.StartsWith("speed"))
                    {
                        lastSpeed = decimal.TryParse(line.Split('=')[1].Replace("x", string.Empty), out decimal dx) ? dx : 0m;
                    }
                    if (line.StartsWith("progress"))
                    {
                        var px = line.Split('=')[1];
                        if (px.ToLower() == "end")
                        {
                            lastFPS = 0m;
                            lastFPS = 0m;
                            lastBitrate = "N/A";
                            lastTotalSize = 0;
                            lastOutTime = 0;
                            lastSpeed = 0;
                        }

                        Console.Title = $"FPS={lastFPS:0.00},Speed={lastSpeed:0.00}x";
                    }
                }
                tcs.SetResult(true);
            });

            var errOutput = Task.Factory.StartNew(async () =>
            {
                using var sr = new StreamReader(px.StandardError.BaseStream);
                var err = await sr.ReadToEndAsync();
                if (!string.IsNullOrEmpty(err) && !tcs.Task.IsCompleted)
                {
                    tcs.SetException(new Exception(err));
                }
            });

            await tcs.Task;
            px.WaitForExit();
        }

        static Task CompressDirs(string archivePassword, string cam, IEnumerable<string> dirs, string outPath)
        {
            if (dirs.Count() > 0)
            {
                if (File.Exists(Path.Combine(outPath, $"{cam}.7z.001")))
                {
                    Console.WriteLine($"Archive already exists for {outPath}, skipping..");
                }
                else
                {
                    return RunProgram(FindExe("7z"), $"a -y -m0=lzma2 -mx6{(!string.IsNullOrEmpty(archivePassword) ? $" -p{archivePassword}" : string.Empty)} -v{UInt32.MaxValue-1:0}b -ms=100f256m {cam}.7z {string.Join(" ", dirs.Select(a => $"{a}{Path.DirectorySeparatorChar}"))}", outPath);
                }
            }
            return Task.CompletedTask;
        }

        static Task MakeParityData(long diskSize, List<string> files, string outPath)
        {
            if (files.Count > 0)
            {
                var totalSize = 0L;
                foreach (var fi in files)
                {
                    totalSize += new FileInfo(fi).Length;
                }

                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    var paritySize = (diskSize - totalSize);
                    Console.WriteLine($"Making parity data with {paritySize / Math.Pow(1000.0, 3):0.00} GB of data");
                    return RunProgram(FindExe("par2"), $"c -rk{paritySize / 1024.0:0} -n{files.Count} {outPath} {string.Join(" ", files)}", Path.GetDirectoryName(outPath));
                }
                else
                {
                    var parityFactor = 100.0 * ((diskSize - totalSize) / (double)totalSize);
                    Console.WriteLine($"Making parity data with {parityFactor:0.00}% recovery");
                    return RunProgram(FindExe("par2j64"), $"c /rr{parityFactor:0.00} /rf{files.Count} {outPath} {string.Join(" ", files)}", Path.GetDirectoryName(outPath));
                }
            }
            return Task.CompletedTask;
        }

        static Task MakeDiskImage(string fromDir, string label, string outPath)
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                return RunProgram(FindExe("mkisofs"), $"-udf -A \"sssombt\" -o \"{label}.iso\" -V \"{label}\" {fromDir}", outPath);
            }
            else
            {
                return RunProgram(FindExe("ImgBurn"), $"/MODE BUILD /BUILDMODE IMAGEFILE /SRC {fromDir} /DEST {label}.iso /FILESYSTEM \"ISO9660 + UDF\" /UDFREVISION 1.02 /VOLUMELABEL \"{label}\" /OVERWRITE YES /rootfolder yes /start /LOG \"{label}.log\" /close /noimagedetails", outPath);
            }
        }

        static async Task RunProgram(string exe, string args, string workingDir, bool writeStdout = true)
        {
            var p = new ProcessStartInfo(exe);
            p.Arguments = args;
            p.UseShellExecute = false;
            p.WorkingDirectory = workingDir;
            p.RedirectStandardOutput = true;
            p.RedirectStandardInput = true;
            p.RedirectStandardError = true;
            p.CreateNoWindow = true;

            var px = Process.Start(p);
            ProgramExit.Token.Register(() => px.Kill());

            var tcs = new TaskCompletionSource<bool>();
            var tcsEx = new TaskCompletionSource<string>();
            var readOutput = Task.Factory.StartNew(async () =>
            {
                using var sr = new StreamReader(px.StandardOutput.BaseStream);
                string line = null;
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    if (writeStdout)
                    {
                        Console.WriteLine(line);
                    }
                }

                tcs.SetResult(true);
            });

            var errOutput = Task.Factory.StartNew(async () =>
            {
                using var sr = new StreamReader(px.StandardError.BaseStream);
                var err = await sr.ReadToEndAsync();
                if (!string.IsNullOrEmpty(err))
                {
                    tcsEx.SetResult(err);
                }
            });

            await Task.WhenAny(tcs.Task, tcsEx.Task);
            px.WaitForExit();
            if (px.ExitCode != 0)
            {
                throw new Exception(tcsEx.Task.Result);
            }
        }

        static async Task CreateDisks(IEnumerable<CCTVDir> inDirs, bool delete, string password, long diskSize, double paritySize)
        {
            var diskFullness = 1.0d - (paritySize / 100d);

            //find any cams with enough data to make ISO
            var cams = inDirs.GroupBy(a => a.Camera);
            var targetSize = (diskSize * diskFullness);
            foreach (var camDir in cams)
            {
                var curDiskSize = 0L;
                var includeInDisk = new List<CCTVDir>();
                var full = false;
                foreach (var dirInc in camDir.OrderBy(a => a.Date))
                {
                    var ds = dirInc.OutputDirectorySize;
                    if (ds == null)
                    {
                        Console.WriteLine($"Dir {dirInc.DateString} was in-complete but should have been included, missing content?");
                        break;
                    }
                    else
                    {
                        if ((curDiskSize + ds) < targetSize)
                        {
                            includeInDisk.Add(dirInc);
                            curDiskSize += ds.Value;
                        }
                        else
                        {
                            full = true;
                            break;
                        }
                    }
                }

                //check if we have enough data
                if (!full)
                {
                    Console.WriteLine($"Not enough data for {camDir.Key} disk {curDiskSize / Math.Pow(1000.0, 3):0.00} GB ({100 * (curDiskSize / targetSize):0.00}%)");
                    continue;
                }

                //compress
                var orderIncDisk = includeInDisk.OrderBy(a => a.Date);
                var label = $"{camDir.Key}_{orderIncDisk.First().Date.ToString("yyyyMMdd")}_{orderIncDisk.Last().Date.ToString("yyyyMMdd")}";

                Console.WriteLine($"Compressing for disk: {label} ({curDiskSize / Math.Pow(1000.0, 3):0.00} GB)");

                var outLabel = Path.Combine(camDir.First().OutputDirectory, "build", label);
                if (!Directory.Exists(outLabel))
                {
                    Directory.CreateDirectory(outLabel);
                }

                await CompressDirs(password, includeInDisk.First().Camera, includeInDisk.Select(a => a.ToOutDir()), outLabel);

                //make parity data
                var fileList = Directory.EnumerateFiles(outLabel);
                if (paritySize != 0)
                {
                    var outPar = Path.Combine(outLabel, $"{camDir.Key}.par2");
                    if (!File.Exists(outPar))
                    {
                        await MakeParityData(diskSize, fileList.ToList(), outPar);
                    }
                    else
                    {
                        Console.WriteLine("Parity data already exists, skipping..");
                    }
                }

                //make iso
                fileList = Directory.EnumerateFiles(outLabel);
                await MakeDiskImage(outLabel, label, Path.GetDirectoryName(outLabel));

                //delete build path
                Directory.Delete(outLabel, true);

                //delete dirs once iso made
                foreach (var d in includeInDisk)
                {
                    if (delete)
                    {
                        Directory.Delete(d.InputDirectory, true);
                        Directory.Delete(d.ToOutDir(), true);
                    }
                    else
                    {
                        Directory.Move(d.InputDirectory, $"{d.InputDirectory}_DONE");
                        Directory.Move(d.ToOutDir(), $"{d.ToOutDir()}_DONE");
                    }
                }
            }
        }

        static IEnumerable<CCTVDir> GetDirs(string baseDir, string outDir)
        {
            var cam = Path.GetFileName(baseDir);
            foreach (var d in Directory.EnumerateDirectories(baseDir))
            {
                var dateDir = Path.GetFileName(d);
                if (DateTime.TryParseExact(dateDir.Substring(0, dateDir.Length - 2), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime date))
                {
                    if (dateDir.EndsWith("PM"))
                    {
                        date = date.AddHours(12);
                    }
                    var cd = new CCTVDir()
                    {
                        Date = date,
                        Camera = cam,
                        InputDirectory = d,
                        OutputDirectory = outDir
                    };
                    yield return cd;
                }
            }
        }

        internal class CCTVDir
        {
            public DateTime Date { get; set; }
            public string DateString => Date.ToString("yyyyMMddtt").ToUpper();

            public string Camera { get; set; }

            public string InputDirectory { get; set; }

            public string OutputDirectory { get; set; }

            public long? InputDirectorySize => GetDirSize(InputDirectory);

            public long? OutputDirectorySize => GetDirSize(ToOutDir());

            private long? GetDirSize(string d)
            {
                if (Directory.Exists(d))
                {
                    long ret = 0;
                    foreach (var f in Directory.EnumerateFiles(d))
                    {
                        var ext = Path.GetExtension(f);
                        if (ext == ".tmp")
                        {
                            break;
                        }
                        else
                        {
                            var fi = new FileInfo(f);
                            if (fi != null)
                            {
                                ret += fi.Length;
                            }
                        }
                    }
                    return ret;
                }

                return null;
            }

            public string ToOutDir() => Path.Combine(OutputDirectory, Camera, DateString);

            public IEnumerable<string> GetSourceFiles() => Directory.EnumerateFiles(InputDirectory, "*.mp4");

            public string MapToOutputFile(string inFile) => Path.Combine(ToOutDir(), Path.GetFileName(Path.ChangeExtension(inFile, $"{OutFormat}")));

            public bool IsComplete => GetSourceFiles().All(a => File.Exists(MapToOutputFile(a)));
        }
    }
}
