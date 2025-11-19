using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

class Program
{
    static async Task Main()
    {

        Directory.CreateDirectory("storage");
        var db = new Db("Data Source=chat.db");
        await db.InitAsync();

        var server = new ChatServer("127.0.0.1", 8888, db);
        await server.StartAsync();
    }
}

// --- Data access (SQLite) ---
class Db
{
    private readonly string _connString;
    public Db(string connString) => _connString = connString;

    public IDbConnection Open() => new SqliteConnection(_connString);

    public async Task InitAsync()
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS Users(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  Login TEXT NOT NULL UNIQUE,
  PasswordHash TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS Messages(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  SenderId INTEGER NOT NULL,
  RecipientId INTEGER NULL,
  Content TEXT NOT NULL,
  CreatedAt TEXT NOT NULL,
  FOREIGN KEY(SenderId) REFERENCES Users(Id),
  FOREIGN KEY(RecipientId) REFERENCES Users(Id)
);
CREATE TABLE IF NOT EXISTS Files(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  OwnerId INTEGER NOT NULL,
  Filename TEXT NOT NULL UNIQUE,
  Size INTEGER NOT NULL,
  Path TEXT NOT NULL,
  CreatedAt TEXT NOT NULL,
  FOREIGN KEY(OwnerId) REFERENCES Users(Id)
);
");
    }

    public async Task<int?> GetUserIdByLoginAsync(string login)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int?>(@"SELECT Id FROM Users WHERE Login=@login", new { login });
    }

    public async Task<bool> CreateUserAsync(string login, string passwordHash)
    {
        using var conn = Open();
        try
        {
            await conn.ExecuteAsync(@"INSERT INTO Users(Login, PasswordHash) VALUES(@login,@passwordHash)", new { login, passwordHash });
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> ValidateUserAsync(string login, string passwordHash)
    {
        using var conn = Open();
        var count = await conn.ExecuteScalarAsync<int>(
            @"SELECT COUNT(1) FROM Users WHERE Login=@login AND PasswordHash=@passwordHash",
            new { login, passwordHash }
        );
        return count == 1;
    }

    public async Task SaveMessageAsync(int senderId, int? recipientId, string content)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            @"INSERT INTO Messages(SenderId, RecipientId, Content, CreatedAt) 
              VALUES(@senderId, @recipientId, @content, @ts)",
            new { senderId, recipientId, content, ts = DateTime.UtcNow.ToString("o") }
        );
    }

    public async Task<bool> SaveFileAsync(int ownerId, string filename, long size, string path)
    {
        using var conn = Open();
        try
        {
            await conn.ExecuteAsync(
                @"INSERT INTO Files(OwnerId, Filename, Size, Path, CreatedAt)
                  VALUES(@ownerId, @filename, @size, @path, @ts)",
                new { ownerId, filename, size, path, ts = DateTime.UtcNow.ToString("o") }
            );
            return true;
        }
        catch { return false; }
    }

    public async Task<(string path, long size)?> GetFileAsync(string filename)
    {
        using var conn = Open();
        var row = await conn.QuerySingleOrDefaultAsync<(string path, long size)>(
            @"SELECT Path, Size FROM Files WHERE Filename=@filename",
            new { filename }
        );
        if (row.path == null) return null;
        return row;
    }
}

// --- Server ---
class ChatServer
{
    private readonly TcpListener _listener;
    private readonly Db _db;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, string> _clientToUser = new();

    public ChatServer(string ip, int port, Db db)
    {
        _listener = new TcpListener(IPAddress.Parse(ip), port);
        _db = db;
    }

    public async Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine("Server started...");
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var stream = client.GetStream();
        var buffer = new byte[8192];
        string? login = null;

        try
        {
            await SendAsync(stream, "Welcome. Use /register or /login\n");

            while (true)
            {
                string? line = await ReadLineAsync(stream, buffer);
                if (line == null) break; 

                if (line.StartsWith("/register "))
                {
                    var parts = line.Split(' ', 3);
                    if (parts.Length < 3) { await SendAsync(stream, "ERR Usage: /register login password\n"); continue; }
                    var lg = parts[1].Trim();
                    var ph = Hash(parts[2].Trim());
                    var ok = await _db.CreateUserAsync(lg, ph);
                    await SendAsync(stream, ok ? "OK Registered\n" : "ERR LoginTaken\n");
                }
                else if (line.StartsWith("/login "))
                {
                    var parts = line.Split(' ', 3);
                    if (parts.Length < 3) { await SendAsync(stream, "ERR Usage: /login login password\n"); continue; }
                    var lg = parts[1].Trim();
                    var ph = Hash(parts[2].Trim());
                    var valid = await _db.ValidateUserAsync(lg, ph);
                    if (!valid) { await SendAsync(stream, "ERR InvalidCredentials\n"); continue; }

                    if (_clients.ContainsKey(lg))
                    {
                        await SendAsync(stream, "ERR AlreadyOnline\n");
                        continue;
                    }

                    login = lg;
                    _clients[login] = client;
                    _clientToUser[client] = login;

                    await SendAsync(stream, $"OK Welcome {login}\n");
                    await SendAsync(stream, "[AUTH OK]\n");
                    Broadcast($"[JOIN] {login}\n", sender: null);
                    BroadcastUserList();
                }
                else if (line.StartsWith("/pm "))
                {
                    if (login == null) { await SendAsync(stream, "ERR NotLoggedIn\n"); continue; }
                    var parts = line.Split(' ', 3);
                    if (parts.Length < 3) { await SendAsync(stream, "ERR Usage: /pm user text\n"); continue; }
                    var targetUser = parts[1];
                    var text = parts[2];

                    if (_clients.TryGetValue(targetUser, out var target))
                    {
                        await SendAsync(target.GetStream(), $"[PM] {login}: {text}\n");
                    }
                    else
                    {
                        await SendAsync(stream, "ERR UserOffline\n");
                    }
                    var senderId = await _db.GetUserIdByLoginAsync(login) ?? 0;
                    var targetId = await _db.GetUserIdByLoginAsync(targetUser);
                    await _db.SaveMessageAsync(senderId, targetId, text);
                }
                else if (line.StartsWith("/upload "))
                {
                    if (login == null) { await SendAsync(stream, "ERR NotLoggedIn\n"); continue; }
                    var parts = line.Split(' ', 4);
                    if (parts.Length < 4) { await SendAsync(stream, "ERR Usage: /upload targetUser filename size\n"); continue; }

                    var targetUser = parts[1].Trim();
                    var filename = parts[2].Trim();
                    if (!long.TryParse(parts[3], out long size) || size < 0 || size > 1_000_000_000)
                    {
                        await SendAsync(stream, "ERR BadSize\n"); continue;
                    }

                    await SendAsync(stream, "OK-UPLOAD\n");
                    var filePath = Path.Combine("storage", SafeFileName(filename));
                    var ok = await ReceiveFileAsync(stream, filePath, size);
                    if (!ok) { await SendAsync(stream, "ERR UploadFailed\n"); continue; }

                    var ownerId = await _db.GetUserIdByLoginAsync(login);
                    if (ownerId == null)
                    {
                        await SendAsync(stream, "ERR UnknownUser\n");
                        continue;
                    }

                    var saved = await _db.SaveFileAsync(ownerId.Value, filename, size, filePath);
                    if (saved)
                    {
                        await SendAsync(stream, "OK Uploaded\n");

                        if (_clients.TryGetValue(targetUser, out var target))
                        {
                            await SendAsync(target.GetStream(), $"[FILE] {login} sent you {filename}\n");
                        }
                        else
                        {
                            await SendAsync(stream, "ERR TargetUserOffline\n");
                        }
                    }
                    else
                    {
                        await SendAsync(stream, "ERR FileRecordFailed\n");
                    }
                }

                else if (line.StartsWith("/download "))
                {
                    if (login == null) { await SendAsync(stream, "ERR NotLoggedIn\n"); continue; }
                    var parts = line.Split(' ', 2);
                    if (parts.Length < 2) { await SendAsync(stream, "ERR Usage: /download filename\n"); continue; }

                    var filename = parts[1].Trim();
                    var file = await _db.GetFileAsync(filename);
                    if (file == null || !File.Exists(file.Value.path))
                    {
                        await SendAsync(stream, "ERR FileNotFound\n"); continue;
                    }

                    await SendAsync(stream, $"FILE {file.Value.size}\n");
                    await SendFileAsync(stream, file.Value.path, file.Value.size);
                }

                else if (line == "/quit")
                {
                    await SendAsync(stream, "OK Bye\n");
                    break;
                }
                else
                {
                    await SendAsync(stream, "ERR UnknownCommand\n");
                }
            }
        }
        catch
        {

        }
        finally
        {
            if (login != null)
            {
                _clients.TryRemove(login, out _);
                _clientToUser.TryRemove(client, out _);
                Broadcast($"[LEAVE] {login}\n", sender: null);
                BroadcastUserList();
            }
            try { client.Close(); } catch { }
        }
    }


    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private async Task<string?> ReadLineAsync(NetworkStream stream, byte[] buffer)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var s = sb.ToString();
            int idx = s.IndexOf('\n');
            if (idx >= 0)
            {
                var line = s.Substring(0, idx).TrimEnd('\r');
                return line;
            }
        }
    }

    private async Task SendAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }

    private void Broadcast(string message, TcpClient? sender)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        foreach (var kv in _clients)
        {
            var client = kv.Value;
            if (sender != null && client == sender) continue;
            try { client.GetStream().Write(bytes, 0, bytes.Length); } catch { }
        }
        Console.Write(message);
    }

    private void BroadcastUserList()
    {
        var users = string.Join(",", _clients.Keys.OrderBy(x => x));
        var msg = "[USERS] " + users + "\n";
        var bytes = Encoding.UTF8.GetBytes(msg);

        foreach (var kv in _clients)
        {
            try { kv.Value.GetStream().Write(bytes, 0, bytes.Length); } catch { }
        }
    }

    private static string Hash(string s)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h);
    }

    private async Task<bool> ReceiveFileAsync(NetworkStream stream, string path, long size)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
            long remaining = size;
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = await stream.ReadAsync(buffer, 0, toRead);
                    if (read <= 0) return false;
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return true;
        }
        catch { return false; }
    }

    private async Task SendFileAsync(NetworkStream stream, string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long remaining = size;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await fs.ReadAsync(buffer, 0, toRead);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
