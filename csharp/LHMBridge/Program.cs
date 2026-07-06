using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LHMBridge.Models;
using LHMBridge.Services;

namespace LHMBridge;

class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    static void Main(string[] args)
    {
        int? port = null;

        // Parse --port argument
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int parsedPort))
                {
                    port = parsedPort;
                }
            }
        }

        if (port.HasValue)
        {
            RunTcpMode(port.Value);
        }
        else
        {
            RunStdioMode();
        }
    }

    private static void RunTcpMode(int port)
    {
        HardwareMonitorService? service = null;
        TcpListener? listener = null;

        try
        {
            service = new HardwareMonitorService();
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            // Signal ready via stdout (parent process reads this)
            Console.WriteLine(JsonSerializer.Serialize(Response<string>.Ok("ready"), JsonOptions));
            Console.Out.Flush();

            // Accept single client connection
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    var response = ProcessCommand(line, service);
                    writer.WriteLine(response);

                    // Check if shutdown was requested
                    if (line.Contains("shutdown", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    var errorResponse = JsonSerializer.Serialize(
                        Response<object>.Fail($"Command processing error: {ex.Message}"),
                        JsonOptions);
                    writer.WriteLine(errorResponse);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                Response<object>.Fail($"Initialization error: {ex.Message}"),
                JsonOptions));
            Console.Out.Flush();
            Environment.Exit(1);
        }
        finally
        {
            listener?.Stop();
            service?.Dispose();
        }
    }

    private static void RunStdioMode()
    {
        HardwareMonitorService? service = null;

        try
        {
            service = new HardwareMonitorService();
            WriteStdioResponse(Response<string>.Ok("ready"));

            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    var response = ProcessCommand(line, service);
                    Console.WriteLine(response);
                    Console.Out.Flush();
                }
                catch (Exception ex)
                {
                    WriteStdioResponse(Response<object>.Fail($"Command processing error: {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            WriteStdioResponse(Response<object>.Fail($"Initialization error: {ex.Message}"));
            Environment.Exit(1);
        }
        finally
        {
            service?.Dispose();
        }
    }

    private static string ProcessCommand(string line, HardwareMonitorService service)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return JsonSerializer.Serialize(Response<object>.Fail("Empty command"), JsonOptions);
        }

        var command = JsonSerializer.Deserialize<Command>(line, JsonOptions);

        if (command == null)
        {
            return JsonSerializer.Serialize(Response<object>.Fail("Invalid command format"), JsonOptions);
        }

        switch (command.CommandName.ToLowerInvariant())
        {
            case "gethardware":
                var hardware = service.GetHardware();
                return JsonSerializer.Serialize(Response<List<HardwareInfo>>.Ok(hardware), JsonOptions);

            case "getsensors":
                var sensors = service.GetSensors();
                return JsonSerializer.Serialize(Response<List<SensorInfo>>.Ok(sensors), JsonOptions);

            case "shutdown":
                return JsonSerializer.Serialize(Response<string>.Ok("shutting down"), JsonOptions);

            default:
                return JsonSerializer.Serialize(
                    Response<object>.Fail($"Unknown command: {command.CommandName}"),
                    JsonOptions);
        }
    }

    private static void WriteStdioResponse<T>(Response<T> response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        Console.WriteLine(json);
        Console.Out.Flush();
    }
}
