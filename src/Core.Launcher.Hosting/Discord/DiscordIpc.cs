using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Core.Launcher.Hosting.Discord;

/// <summary>The opcode in front of each frame on Discord's IPC pipe.</summary>
public enum DiscordOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>What Core shows on the player's Discord profile, under the application's name ("Core").</summary>
/// <param name="Details">The first line ("Logged in as MrPodge6"); null shows none.</param>
/// <param name="State">The second line ("In Lobby").</param>
/// <param name="Started">Discord counts up from it ("00:12 elapsed"); null shows no timer.</param>
/// <param name="LargeImage">An art asset's key on the application (<see cref="DiscordPresence.LargeImageKey"/>).</param>
/// <param name="LargeText">Shown on hovering the image.</param>
public sealed record DiscordActivity(string? Details, string State, DateTimeOffset? Started, string? LargeImage, string? LargeText);

/// <summary>
/// Discord's local RPC, as its desktop client serves it on <c>\\.\pipe\discord-ipc-0</c> to <c>-9</c> (one per running
/// Discord: stable, PTB, Canary). Each frame is a little-endian int32 opcode, a little-endian int32 length and that many
/// bytes of UTF-8 JSON. The client opens with a <see cref="DiscordOpcode.Handshake"/> of <c>{"v":1,"client_id":...}</c>;
/// Discord answers with a READY dispatch, or with <see cref="DiscordOpcode.Close"/> (an unknown application id, say).
/// After that each command is a <see cref="DiscordOpcode.Frame"/> with a nonce, answered by one with the same nonce, and
/// Discord's <see cref="DiscordOpcode.Ping"/>s are answered with a <see cref="DiscordOpcode.Pong"/> of the same payload.
/// </summary>
public static class DiscordIpc
{
    public const string PipePrefix = "discord-ipc-";

    public const int PipeCount = 10;

    public const int HeaderSize = 8;

    /// <summary>The largest frame either side sends (discord-rpc's MaxRpcFrameSize); a longer one means the stream is out of step.</summary>
    public const int MaxPayload = 64 * 1024;

    /// <summary>Discord takes 2 to 128 characters for each line and the image's text.</summary>
    public const int MaxText = 128;

    public static byte[] Encode(DiscordOpcode opcode, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>The opcode and payload length of a frame's header; throws <see cref="InvalidDataException"/> on a length out of range.</summary>
    public static (DiscordOpcode Opcode, int Length) ReadHeader(ReadOnlySpan<byte> header)
    {
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (length is < 0 or > MaxPayload)
        {
            throw new InvalidDataException($"A Discord frame of {length} bytes");
        }

        return ((DiscordOpcode)opcode, length);
    }

    /// <summary>Reads one frame; null when the pipe has closed (at a frame boundary or inside one).</summary>
    public static async Task<(DiscordOpcode Opcode, string Json)?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[HeaderSize];
        if (!await FillAsync(stream, header, cancellation))
        {
            return null;
        }

        var (opcode, length) = ReadHeader(header);
        var payload = new byte[length];
        return await FillAsync(stream, payload, cancellation) ? (opcode, Encoding.UTF8.GetString(payload)) : null;
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellation)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellation);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    public static string Handshake(string applicationId) => new JsonObject { ["v"] = 1, ["client_id"] = applicationId }.ToJsonString();

    /// <summary>
    /// SET_ACTIVITY for the process <paramref name="processId"/> (Discord drops the activity once that process exits).
    /// A null <paramref name="activity"/> clears it: the command then has no activity, as discord-rpc's ClearPresence sends it.
    /// </summary>
    public static string SetActivity(int processId, DiscordActivity? activity, string nonce)
    {
        var arguments = new JsonObject { ["pid"] = processId };
        if (activity is not null)
        {
            arguments["activity"] = ActivityJson(activity);
        }

        return new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = arguments, ["nonce"] = nonce }.ToJsonString();
    }

    private static JsonObject ActivityJson(DiscordActivity activity)
    {
        var json = new JsonObject();
        if (Text(activity.Details) is { } details)
        {
            json["details"] = details;
        }

        json["state"] = Text(activity.State) ?? "Core";
        if (activity.Started is { } started)
        {
            // The activity object's timestamps are unix milliseconds.
            json["timestamps"] = new JsonObject { ["start"] = started.ToUnixTimeMilliseconds() };
        }

        var assets = new JsonObject();
        if (!string.IsNullOrEmpty(activity.LargeImage))
        {
            assets["large_image"] = activity.LargeImage;
        }

        if (Text(activity.LargeText) is { } largeText)
        {
            assets["large_text"] = largeText;
        }

        if (assets.Count > 0)
        {
            json["assets"] = assets;
        }

        json["instance"] = false;
        return json;
    }

    /// <summary>The text within Discord's limits: at most <see cref="MaxText"/> characters (cut with an ellipsis), null when shorter than 2.</summary>
    public static string? Text(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length < 2)
        {
            return null;
        }

        return trimmed.Length <= MaxText ? trimmed : trimmed[..(MaxText - 1)].TrimEnd() + "…";
    }

    /// <summary>Discord's answer to the handshake: true for the READY dispatch.</summary>
    public static bool IsReady(string json) => Field(json, "evt") == "READY";

    /// <summary>A command's error ("evt":"ERROR"), as Discord's message; null when the frame isn't one.</summary>
    public static string? Error(string json)
    {
        if (Field(json, "evt") != "ERROR")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : "unknown error";
        }
        catch (JsonException)
        {
            return "unknown error";
        }
    }

    /// <summary>A CLOSE frame's reason ({"code":4000,"message":"Invalid Client ID"}).</summary>
    public static string CloseReason(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetRawText() : null;
            return (message, code) switch
            {
                (not null, not null) => $"{message} ({code})",
                (not null, null) => message,
                (null, not null) => $"code {code}",
                _ => "no reason given",
            };
        }
        catch (JsonException)
        {
            return "no reason given";
        }
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
